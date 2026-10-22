using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Allen.StallTrace17
{
    // Measurement only: this mod does not change Job, Pawn, pathfinding or scheduler results.
    public sealed class StallTraceMod : Mod
    {
        internal static StallTraceSettings Settings;

        public StallTraceMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<StallTraceSettings>();
            StallTraceRuntime.Install();
        }

        public override string SettingsCategory() { return "StallTrace 1.7"; }

        public override void DoSettingsWindowContents(Rect rect)
        {
            float x = rect.x + 8f;
            float y = rect.y + 8f;
            float w = Math.Min(rect.width - 16f, 680f);
            Widgets.Label(new Rect(x, y, w, 28f), "StallTrace17 | RimWorld 1.7 / Waylith | diagnostics only");
            y += 35f;
            Widgets.CheckboxLabeled(new Rect(x, y, w, 28f), "Enable low-overhead stall tracing", ref Settings.Enabled);
            y += 34f;
            Widgets.CheckboxLabeled(new Rect(x, y, w, 28f), "Auto-export every 120 seconds (only when slow events exist)", ref Settings.AutoExport);
            y += 40f;
            Widgets.Label(new Rect(x, y, w, 52f), "Slow thresholds: frame 50ms, tick 30ms, pawn 5ms, job search 4ms, work scan 4ms, driver 5ms, path 5ms.");
            y += 57f;
            if (Widgets.ButtonText(new Rect(x, y, 220f, 34f), "Export report now"))
                Log.Message("[StallTrace17] Export: " + StallTraceRuntime.ExportNow());
            if (Widgets.ButtonText(new Rect(x + 234f, y, 160f, 34f), "Reset capture"))
                StallTraceRuntime.Reset();
            y += 42f;
            Widgets.Label(new Rect(x, y, w, 70f), "Report folder: " + StallTraceRuntime.ReportFolder + "\nCapture is bounded to 512 outliers. Timings are inclusive/nested and must NOT be added together as independent CPU costs.");
        }
    }

    public sealed class StallTraceSettings : ModSettings
    {
        public bool Enabled = true;
        public bool AutoExport = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "enabled", true);
            Scribe_Values.Look(ref AutoExport, "autoExport", true);
        }
    }

    internal static class StallTraceRuntime
    {
        private const string Owner = "allen.stalltrace17";
        private const int Capacity = 512;
        private static readonly long[] Threshold = MakeThresholds();
        private static readonly string[] Names = { "Root_Play.Update", "DoSingleTick", "Pawn.TickInterval", "JobSearch", "WorkSearch", "JobDriver.DriverTick", "PathFinder.FindPath" };
        private static readonly long[] Total = new long[Names.Length];
        private static readonly long[] Max = new long[Names.Length];
        private static readonly long[] Count = new long[Names.Length];
        private static readonly long[] FrameTotal = new long[Names.Length];
        private static readonly long[] FrameCount = new long[Names.Length];
        private static readonly Queue<string> Events = new Queue<string>();
        private static readonly Dictionary<int, JobBurst> JobBursts = new Dictionary<int, JobBurst>();
        private static readonly object EventLock = new object();
        private static readonly FieldInfo TrackerPawn = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
        private static long frameNo;
        private static long lastAutoExport;
        private static int patchInstalled;
        private static int patchFailed;
        private static int jobLoopAlerts;
        private static bool frameOpen;
        private static int mainThreadId;
        private static bool initialized;

        [ThreadStatic] private static Pawn contextPawn;

        private struct JobBurst
        {
            internal int Tick;
            internal int Count;
        }

        internal static string ReportFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RimWorld-StallTrace17");
            }
        }

        private static long[] MakeThresholds()
        {
            long t = Math.Max(1, Stopwatch.Frequency / 1000);
            return new[] { 50 * t, 30 * t, 5 * t, 4 * t, 4 * t, 5 * t, 5 * t };
        }

        private static bool Active
        {
            get { return StallTraceMod.Settings != null && StallTraceMod.Settings.Enabled; }
        }

        internal static void Install()
        {
            if (initialized)
                return;
            initialized = true;
            // Captured lazily on the first Root_Play.Update, not on the async mod-loading thread.
            mainThreadId = 0;
            try
            {
                Harmony h = new Harmony(Owner);
                Patch(h, typeof(Root_Play), "Update", 0, nameof(RootPrefix), nameof(RootPostfix));
                Patch(h, typeof(TickManager), "DoSingleTick", 0, nameof(TickPrefix), nameof(TickPostfix));
                Patch(h, typeof(Pawn), "TickInterval", 1, nameof(PawnPrefix), nameof(PawnPostfix));
                Patch(h, typeof(Pawn_JobTracker), "TryFindAndStartJob", 0, nameof(JobSearchPrefix), nameof(JobSearchPostfix));
                Patch(h, typeof(JobGiver_Work), "TryIssueJobPackage", 2, nameof(WorkPrefix), nameof(WorkPostfix));
                Patch(h, typeof(JobDriver), "DriverTick", 0, nameof(DriverPrefix), nameof(DriverPostfix));
                Patch(h, typeof(Pawn_JobTracker), "StartJob", -1, nameof(StartJobPrefix), null, typeof(Job));

                foreach (MethodInfo method in typeof(PathFinder).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name == "FindPath" && !method.ContainsGenericParameters && !method.IsAbstract)
                        PatchMethod(h, method, nameof(PathPrefix), nameof(PathPostfix));
                }
                Log.Message("[StallTrace17] Waylith 1.7 measurement hooks: installed=" + patchInstalled +
                    " failed=" + patchFailed + "; no gameplay authority or job results modified. " +
                    "Use Mod Settings > StallTrace 1.7 > Export report now.");
            }
            catch (Exception e)
            {
                Log.Warning("[StallTrace17] Initialization failed closed: " + e);
            }
        }

        private static void Patch(Harmony h, Type type, string methodName, int parameterCount,
                                  string prefix, string postfix, Type firstType = null)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            bool found = false;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters)
                    continue;
                ParameterInfo[] args = method.GetParameters();
                if (parameterCount >= 0 && args.Length != parameterCount)
                    continue;
                if (firstType != null && (args.Length == 0 || args[0].ParameterType != firstType))
                    continue;
                found = true;
                PatchMethod(h, method, prefix, postfix);
                if (methodName != "StartJob")
                    break;
            }
            if (!found)
            {
                patchFailed++;
                Log.Warning("[StallTrace17] Signature not found (disabled): " + type.FullName + "." + methodName);
            }
        }

        private static void PatchMethod(Harmony h, MethodInfo method, string prefix, string postfix)
        {
            try
            {
                HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(
                    AccessTools.Method(typeof(StallTraceRuntime), prefix));
                HarmonyMethod post = postfix == null ? null : new HarmonyMethod(
                    AccessTools.Method(typeof(StallTraceRuntime), postfix));
                h.Patch(method, prefix: pre, postfix: post);
                patchInstalled++;
            }
            catch (Exception e)
            {
                patchFailed++;
                Log.Warning("[StallTrace17] Hook rejected, method left untouched: " + method +
                    " / " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static long Start()
        {
            return Active ? Stopwatch.GetTimestamp() : 0L;
        }

        private static long Elapsed(long state)
        {
            return state == 0 ? 0 : Math.Max(0, Stopwatch.GetTimestamp() - state);
        }

        private static double Millis(long ticks)
        {
            return ticks * (1000.0 / Stopwatch.Frequency);
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private static bool IsMainThread()
        {
            return System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId;
        }

        // These counters deliberately have no locks on the hot path; we trace main-thread gameplay only.
        private static void Record(int kind, long elapsed, Pawn pawn = null, string extra = null)
        {
            if (!Active || elapsed <= 0 || !IsMainThread())
                return;
            Count[kind]++;
            Total[kind] += elapsed;
            if (elapsed > Max[kind]) Max[kind] = elapsed;
            if (frameOpen && kind != 0)
            {
                FrameTotal[kind] += elapsed;
                FrameCount[kind]++;
            }
            if (elapsed < Threshold[kind])
                return;

            AddEvent(Names[kind], elapsed, pawn, extra);
        }

        private static void AddEvent(string category, long elapsed, Pawn pawn, string extra)
        {
            string p = "<none>";
            string job = "<none>";
            if (pawn != null)
            {
                try
                {
                    p = pawn.ToString();
                    job = pawn.CurJob == null || pawn.CurJob.def == null
                        ? "<none>" : pawn.CurJob.def.defName;
                }
                catch { p = "<pawn lookup failed>"; }
            }

            int tick = CurrentTick();
            string entry = DateTime.Now.ToString("HH:mm:ss.fff") +
                "\tframe=" + frameNo +
                "\ttick=" + tick +
                "\tcycleHour=" + (tick < 0 ? -1 : (tick % 60000) / 2500) +
                "\tcategory=" + category +
                "\tms=" + Millis(elapsed).ToString("F3") +
                "\tpawn=" + p +
                "\tjob=" + job +
                (extra == null ? "" : "\t" + extra);
            lock (EventLock)
            {
                if (Events.Count >= Capacity)
                    Events.Dequeue();
                Events.Enqueue(entry);
            }
        }

        public static void RootPrefix(ref long __state)
        {
            if (mainThreadId == 0)
            {
                mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                lastAutoExport = Stopwatch.GetTimestamp();
            }
            __state = Start();
            if (__state == 0) return;
            frameNo++;
            frameOpen = true;
            Array.Clear(FrameTotal, 0, FrameTotal.Length);
            Array.Clear(FrameCount, 0, FrameCount.Length);
        }

        public static void RootPostfix(long __state)
        {
            if (__state == 0) return;
            long elapsed = Elapsed(__state);
            frameOpen = false;
            if (elapsed >= Threshold[0])
            {
                string breakdown = "tickCalls=" + FrameCount[1] + ",tickMs=" + Millis(FrameTotal[1]).ToString("F2") +
                    ",pawnCalls=" + FrameCount[2] + ",pawnMs=" + Millis(FrameTotal[2]).ToString("F2") +
                    ",jobSearchMs=" + Millis(FrameTotal[3]).ToString("F2") +
                    ",workSearchMs=" + Millis(FrameTotal[4]).ToString("F2") +
                    ",driverMs=" + Millis(FrameTotal[5]).ToString("F2") +
                    ",pathMs=" + Millis(FrameTotal[6]).ToString("F2");
                Record(0, elapsed, null, breakdown);
            }
            else
            {
                Record(0, elapsed);
            }

            // Rate-limited background file I/O. No Unity or game state is read by the worker.
            if (StallTraceMod.Settings != null && StallTraceMod.Settings.AutoExport &&
                Stopwatch.GetTimestamp() - lastAutoExport > Stopwatch.Frequency * 120)
            {
                lastAutoExport = Stopwatch.GetTimestamp();
                string snapshot = BuildReport();
                Task.Run(() => { try { WriteReport(snapshot, "auto"); } catch { } });
            }
        }

        public static void TickPrefix(ref long __state) { __state = Start(); }
        public static void TickPostfix(long __state) { Record(1, Elapsed(__state)); }

        public static void PawnPrefix(Pawn __instance, ref long __state)
        {
            __state = Start();
            if (__state != 0) contextPawn = __instance;
        }
        public static void PawnPostfix(Pawn __instance, long __state)
        {
            Record(2, Elapsed(__state), __instance);
            if (__state != 0) contextPawn = null;
        }

        public static void JobSearchPrefix(ref long __state) { __state = Start(); }
        public static void JobSearchPostfix(Pawn_JobTracker __instance, long __state)
        {
            long elapsed = Elapsed(__state);
            if (elapsed >= Threshold[3])
                Record(3, elapsed, GetTrackerPawn(__instance));
            else
                Record(3, elapsed);
        }

        public static void WorkPrefix(Pawn __0, ref long __state) { __state = Start(); }
        public static void WorkPostfix(Pawn __0, long __state)
        {
            long elapsed = Elapsed(__state);
            if (elapsed >= Threshold[4])
                Record(4, elapsed, __0);
            else
                Record(4, elapsed);
        }

        public static void DriverPrefix(ref long __state) { __state = Start(); }
        public static void DriverPostfix(JobDriver __instance, long __state)
        {
            long elapsed = Elapsed(__state);
            if (elapsed >= Threshold[5])
                Record(5, elapsed, __instance == null ? null : __instance.pawn);
            else
                Record(5, elapsed);
        }

        public static void PathPrefix(ref long __state) { __state = Start(); }
        public static void PathPostfix(long __state)
        {
            long elapsed = Elapsed(__state);
            if (elapsed >= Threshold[6])
                Record(6, elapsed, contextPawn, contextPawn == null ? "source=non-Pawn.TickInterval" : "source=Pawn.TickInterval");
            else
                Record(6, elapsed);
        }

        private static Pawn GetTrackerPawn(Pawn_JobTracker tracker)
        {
            try { return tracker == null || TrackerPawn == null ? null : TrackerPawn.GetValue(tracker) as Pawn; }
            catch { return null; }
        }

        // Detect the same-tick StartJob storm demonstrated by repeated Ingest on the same food.
        public static void StartJobPrefix(Pawn_JobTracker __instance, Job __0)
        {
            if (!Active || !IsMainThread())
                return;
            Pawn pawn = GetTrackerPawn(__instance);
            if (pawn == null)
                return;
            int tick = CurrentTick();
            int id = pawn.thingIDNumber;
            JobBurst previous;
            if (!JobBursts.TryGetValue(id, out previous) || previous.Tick != tick)
                previous = new JobBurst { Tick = tick, Count = 0 };
            previous.Count++;
            JobBursts[id] = previous;
            if (previous.Count == 10 || (previous.Count > 10 && previous.Count % 20 == 0))
            {
                jobLoopAlerts++;
                string type = __0 == null || __0.def == null ? "<null>" : __0.def.defName;
                AddEvent("JOB_LOOP", 0, pawn, "startedJobsSameTick=" + previous.Count + ",newJob=" + type);
                if (previous.Count == 10)
                    Log.Warning("[StallTrace17] Job loop: " + pawn + " started 10 jobs in tick " + tick +
                        "; newJob=" + type + ". See StallTrace17 report for context.");
            }
            if (JobBursts.Count > 1024)
                JobBursts.Clear();
        }

        internal static void Reset()
        {
            if (!IsMainThread()) return;
            Array.Clear(Count, 0, Count.Length);
            Array.Clear(Max, 0, Max.Length);
            Array.Clear(Total, 0, Total.Length);
            JobBursts.Clear();
            jobLoopAlerts = 0;
            lock (EventLock) Events.Clear();
            Log.Message("[StallTrace17] Capture reset.");
        }

        private static string BuildReport()
        {
            StringBuilder sb = new StringBuilder(22000);
            sb.AppendLine("StallTrace17 / Waylith native diagnostic / report UTC " + DateTime.UtcNow.ToString("O"));
            sb.AppendLine("Status: installed=" + patchInstalled + ",failed=" + patchFailed +
                ",enabled=" + Active + ",frames=" + frameNo + ",jobLoopAlerts=" + jobLoopAlerts);
            sb.AppendLine("Timings are inclusive: Tick inside frame, WorkSearch inside JobSearch, etc. Do NOT add categories together.");
            sb.AppendLine("cycleHour is ticksGame % 60000 / 2500; it is NOT colony-local daylight time.");
            sb.AppendLine("kind\tcalls\ttotal_ms\tavg_ms\tmax_ms");
            for (int i = 0; i < Names.Length; i++)
            {
                sb.Append(Names[i]).Append('\t').Append(Count[i]).Append('\t')
                  .Append(Millis(Total[i]).ToString("F3")).Append('\t')
                  .Append(Count[i] == 0 ? "0" : Millis(Total[i] / Count[i]).ToString("F3")).Append('\t')
                  .Append(Millis(Max[i]).ToString("F3")).AppendLine();
            }
            sb.AppendLine("--- SLOW EVENTS (last 512, oldest first) ---");
            lock (EventLock)
            {
                foreach (string line in Events)
                    sb.AppendLine(line);
            }
            return sb.ToString();
        }

        internal static string ExportNow()
        {
            try { return WriteReport(BuildReport(), "manual"); }
            catch (Exception ex) { return "FAILED: " + ex; }
        }

        private static string WriteReport(string contents, string mode)
        {
            Directory.CreateDirectory(ReportFolder);
            string path = Path.Combine(ReportFolder, "StallTrace17-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + mode + ".txt");
            File.WriteAllText(path, contents, Encoding.UTF8);
            return path;
        }
    }
}