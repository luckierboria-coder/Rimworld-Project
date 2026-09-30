using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// v0.3 diagnostics-only slow DetermineNextJob correlation.
    /// DetermineNextJob itself is timed on every call (one timestamp pair). WorkGiver method
    /// Stopwatches are active only in periodic deep windows or a bounded burst after a slow DNJ,
    /// so the diagnostics companion does not turn every scanner call into a permanent profiler.
    /// No gameplay result/state is changed.
    /// </summary>
    internal static class DiagnosticsV03
    {
        private const long SlowDetermineUs = 20000L;
        private const int RecentCapacity = 32;
        private const int MaxMethodsPerDetermine = 96;
        private const int TopMethodsPerBurst = 12;
        private const int PostSlowDetailPackages = 24;

        private static readonly FieldInfo JobTrackerPawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
        private static readonly SlowDetermineRecord[] Recent = new SlowDetermineRecord[RecentCapacity];

        [ThreadStatic] private static DetermineContext current;
        [ThreadStatic] private static int detailPackagesRemaining;
        private static int recentPos;
        private static int recentCount;
        private static long determines;
        private static long slowDetermines;
        private static long detailedDetermines;
        private static long slowDetailed;
        private static long slowUndetailed;
        private static long workGiverCallsInsideDetermine;
        private static long workGiverUsInsideDetermine;
        private static long workGiverPatched;
        private static long patchFailures;
        private static long contextReentry;
        private static long failures;

        internal static bool InDetermine { get { return current != null; } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                HashSet<MethodBase> patched = new HashSet<MethodBase>();
                for (int ai = 0; ai < assemblies.Length; ai++)
                {
                    Type[] types;
                    try { types = assemblies[ai].GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    catch { continue; }
                    if (types == null) continue;
                    for (int ti = 0; ti < types.Length; ti++)
                    {
                        Type t = types[ti];
                        if (t == null || t.IsAbstract || !typeof(WorkGiver_Scanner).IsAssignableFrom(t)) continue;
                        MethodInfo[] methods;
                        try { methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                        catch { continue; }
                        for (int mi = 0; mi < methods.Length; mi++)
                        {
                            MethodInfo m = methods[mi];
                            if (m == null || (m.Name != "HasJobOnThing" && m.Name != "JobOnThing" && m.Name != "NonScanJob" && m.Name != "ShouldSkip")) continue;
                            if (!patched.Add(m)) continue;
                            try
                            {
                                harmony.Patch(m,
                                    prefix: new HarmonyMethod(typeof(DiagnosticsV03), nameof(WorkGiverPrefix)) { priority = Priority.First + 20 },
                                    postfix: new HarmonyMethod(typeof(DiagnosticsV03), nameof(WorkGiverPostfix)) { priority = Priority.Last - 20 });
                                workGiverPatched++;
                            }
                            catch { patchFailures++; }
                        }
                    }
                }
            }
            catch { patchFailures++; }
        }

        public static void WorkGiverPrefix(ref long __state)
        {
            DetermineContext ctx = current;
            __state = ctx == null || !ctx.CaptureDetails ? 0L : Stopwatch.GetTimestamp();
        }

        public static void WorkGiverPostfix(object __instance, MethodBase __originalMethod, long __state)
        {
            RecordWorkGiver(__instance, __originalMethod, __state);
        }

        internal static void BeginDetermine(Pawn_JobTracker tracker)
        {
            if (current != null)
            {
                contextReentry++;
                current = null;
            }

            Pawn pawn = null;
            try
            {
                if (tracker != null && JobTrackerPawnField != null)
                    pawn = JobTrackerPawnField.GetValue(tracker) as Pawn;
            }
            catch { failures++; }

            bool burst = detailPackagesRemaining > 0;
            if (burst) detailPackagesRemaining--;
            bool capture = DiagnosticsHub.DeepActive || burst;

            DetermineContext ctx = new DetermineContext();
            ctx.Started = Stopwatch.GetTimestamp();
            ctx.Pawn = pawn;
            ctx.CaptureDetails = capture;
            if (capture)
            {
                ctx.Methods = new Dictionary<string, MethodAccum>(StringComparer.Ordinal);
                detailedDetermines++;
            }
            current = ctx;
            determines++;
        }

        internal static void RecordWorkGiver(object instance, MethodBase method, long started)
        {
            DetermineContext ctx = current;
            if (ctx == null || !ctx.CaptureDetails || ctx.Methods == null || started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = elapsed * 1000000L / Stopwatch.Frequency;
            string type = instance == null
                ? (method == null || method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName)
                : instance.GetType().FullName;
            if (string.IsNullOrEmpty(type)) type = "<unknown>";
            string name = type + "." + (method == null ? "<method>" : method.Name);

            MethodAccum a;
            if (!ctx.Methods.TryGetValue(name, out a))
            {
                if (ctx.Methods.Count >= MaxMethodsPerDetermine) name = "<other>";
                if (!ctx.Methods.TryGetValue(name, out a)) a = new MethodAccum();
            }
            a.Calls++;
            a.TotalUs += us;
            if (us > a.MaxUs) a.MaxUs = us;
            ctx.Methods[name] = a;
            workGiverCallsInsideDetermine++;
            workGiverUsInsideDetermine += us;
        }

        internal static void EndDetermine(Pawn_JobTracker tracker, ThinkResult result)
        {
            DetermineContext ctx = current;
            current = null;
            if (ctx == null || ctx.Started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - ctx.Started;
            if (elapsed <= 0L) return;
            long totalUs = elapsed * 1000000L / Stopwatch.Frequency;
            if (totalUs < SlowDetermineUs) return;
            slowDetermines++;

            if (ctx.CaptureDetails) slowDetailed++;
            else
            {
                slowUndetailed++;
                if (detailPackagesRemaining < PostSlowDetailPackages)
                    detailPackagesRemaining = PostSlowDetailPackages;
            }

            int tick = -1;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; } catch { }
            string pawn = PawnText(ctx.Pawn);
            string resultJob = "<none>";
            string source = "<none>";
            try
            {
                if (result.Job != null && result.Job.def != null) resultJob = result.Job.def.defName;
                if (result.SourceNode != null) source = result.SourceNode.GetType().FullName;
            }
            catch { failures++; }

            string top = "detail-not-armed";
            if (ctx.Methods != null && ctx.Methods.Count != 0)
            {
                top = string.Join(" | ", ctx.Methods
                    .OrderByDescending(kv => kv.Value.TotalUs)
                    .Take(TopMethodsPerBurst)
                    .Select(kv => kv.Key + "[calls=" + kv.Value.Calls + ",totalMs=" + (kv.Value.TotalUs / 1000.0).ToString("F2") + ",maxMs=" + (kv.Value.MaxUs / 1000.0).ToString("F2") + "]")
                    .ToArray());
            }
            Recent[recentPos] = new SlowDetermineRecord(tick, pawn, totalUs, resultJob, source, top);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.Append("SlowDNJCorrelation: determines=").Append(determines)
              .Append(", slow>=20ms=").Append(slowDetermines)
              .Append(", detailedDetermines=").Append(detailedDetermines)
              .Append(", slowDetailed/undetailed=").Append(slowDetailed).Append('/').Append(slowUndetailed)
              .Append(", detailBurstRemaining=").Append(detailPackagesRemaining)
              .Append(", workGiverPatched=").Append(workGiverPatched)
              .Append(", patchFailures=").Append(patchFailures)
              .Append(", workGiverCalls=").Append(workGiverCallsInsideDetermine)
              .Append(", workGiverTimeMs=").Append((workGiverUsInsideDetermine / 1000.0).ToString("F1"))
              .Append(", contextReentry=").Append(contextReentry)
              .Append(", failures=").Append(failures).AppendLine();
            sb.Append("RecentSlowDNJ=");
            if (recentCount == 0)
            {
                sb.AppendLine("none");
                return sb.ToString();
            }
            sb.AppendLine();
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                SlowDetermineRecord e = Recent[(start + i) % RecentCapacity];
                sb.Append(" - tick=").Append(e.Tick)
                  .Append(", pawn=").Append(e.Pawn)
                  .Append(", totalMs=").Append((e.TotalUs / 1000.0).ToString("F2"))
                  .Append(", result=").Append(e.ResultJob)
                  .Append(", source=").Append(e.Source)
                  .Append(", top=").Append(string.IsNullOrEmpty(e.TopMethods) ? "none" : e.TopMethods)
                  .AppendLine();
            }
            return sb.ToString();
        }

        internal static void Reset()
        {
            Array.Clear(Recent, 0, Recent.Length);
            recentPos = recentCount = 0;
            determines = slowDetermines = detailedDetermines = slowDetailed = slowUndetailed = 0L;
            workGiverCallsInsideDetermine = workGiverUsInsideDetermine = contextReentry = failures = 0L;
            detailPackagesRemaining = 0;
            current = null;
        }

        private static string PawnText(Pawn pawn)
        {
            if (pawn == null) return "<null>";
            string label = null;
            try { label = pawn.LabelShortCap; } catch { }
            if (string.IsNullOrEmpty(label)) label = pawn.def == null ? "Pawn" : pawn.def.defName;
            return label + "#" + pawn.thingIDNumber;
        }

        private sealed class DetermineContext
        {
            internal long Started;
            internal Pawn Pawn;
            internal bool CaptureDetails;
            internal Dictionary<string, MethodAccum> Methods;
        }

        private struct MethodAccum
        {
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
        }

        private struct SlowDetermineRecord
        {
            internal readonly int Tick;
            internal readonly string Pawn;
            internal readonly long TotalUs;
            internal readonly string ResultJob;
            internal readonly string Source;
            internal readonly string TopMethods;

            internal SlowDetermineRecord(int tick, string pawn, long totalUs, string resultJob, string source, string topMethods)
            {
                Tick = tick;
                Pawn = pawn;
                TotalUs = totalUs;
                ResultJob = resultJob;
                Source = source;
                TopMethods = topMethods;
            }
        }
    }
}
