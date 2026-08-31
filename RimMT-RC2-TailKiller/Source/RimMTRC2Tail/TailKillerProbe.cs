using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimMTRC2Tail
{
    [StaticConstructorOnStartup]
    internal static class TailKillerProbe
    {
        private const string HarmonyId = "allen.rimmt";
        private const int WarmupTicks = 120;
        private const int CampaignTicks = 1800;
        private const double JobArmMs = 32.0;
        private const double DriverKeepMs = 4.0;
        private const double PathBurstKeepMs = 16.0;
        private const double WorldKeepMs = 16.0;
        private const int KeepTop = 16;

        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly List<Sample> DriverTop = new List<Sample>();
        private static readonly List<Sample> PathBurstTop = new List<Sample>();
        private static readonly List<Sample> WorldTop = new List<Sample>();
        private static readonly List<Sample> StorytellerTop = new List<Sample>();
        private static MethodInfo detailStart;
        private static MethodInfo detailIsActiveGetter;
        private static bool installed;
        private static bool reportHooked;
        private static int startTick = -1;
        private static int sampledTicks;
        private static bool campaignComplete;
        private static bool jobDetailArmAttempted;
        private static bool jobDetailArmSucceeded;
        private static long jobCalls;
        private static long slowJobCalls;
        private static double maxJobMs;
        private static string maxJobPawn;
        private static long driverCalls;
        private static long driverOver4;
        private static double maxDriverMs;
        private static long pathCalls;
        private static long pathOver2;
        private static long pathOver5;
        private static long pathOver10;
        private static long pathOver30;
        private static long currentPathCalls;
        private static long currentPathOver2;
        private static long currentPathOver5;
        private static long currentPathOver10;
        private static long currentPathOver30;
        private static long currentPathTicks;
        private static long currentPathMaxTicks;
        private static int currentPathUniqueEstimate;
        private static Pawn lastPathPawn;
        private static long worldCalls;
        private static double maxWorldMs;
        private static long storytellerCalls;
        private static double maxStorytellerMs;
        private static int failures;

        private struct ProbeState
        {
            internal long Started;
            internal string Label;
        }

        private sealed class Sample
        {
            internal double Ms;
            internal string Label;
            internal string Extra;
        }

        static TailKillerProbe()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                PatchJobGiver();
                PatchDriver();
                PatchPaths();
                PatchWorld();
                PatchTickBoundary();
                HookRuntimeReport();
                ResolveExistingDetailProfiler();
                Log.Message("[RimMT-RC2] Tail-killer probe installed. Bounded campaign: warmup=120 ticks, capture=1800 ticks. It auto-arms the existing JobGiver detail profiler after the first >=32ms package and records only top JobDriver/Path/World/Storyteller tails.");
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warning("[RimMT-RC2] Tail-killer probe install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchJobGiver()
        {
            MethodInfo m = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new Type[] { typeof(Pawn), typeof(JobIssueParams) });
            if (m == null) throw new MissingMethodException("JobGiver_Work.TryIssueJobPackage");
            Harmony.Patch(m,
                prefix: new HarmonyMethod(typeof(TailKillerProbe), nameof(JobPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(JobPostfix)) { priority = Priority.Last });
        }

        private static void PatchDriver()
        {
            MethodInfo m = AccessTools.Method(typeof(JobDriver), "DriverTick");
            if (m == null) throw new MissingMethodException("JobDriver.DriverTick");
            Harmony.Patch(m,
                prefix: new HarmonyMethod(typeof(TailKillerProbe), nameof(DriverPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(DriverPostfix)) { priority = Priority.Last });
        }

        private static void PatchPaths()
        {
            MethodInfo[] methods = typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int patched = 0;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m == null || m.Name != "FindPath") continue;
                Harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(TailKillerProbe), nameof(PathPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(PathPostfix)) { priority = Priority.Last });
                patched++;
            }
            if (patched == 0) throw new MissingMethodException("PathFinder.FindPath");
        }

        private static void PatchWorld()
        {
            MethodInfo world = AccessTools.Method(typeof(World), "WorldTick");
            MethodInfo storyteller = AccessTools.Method(typeof(Storyteller), "StorytellerTick");
            if (world != null)
                Harmony.Patch(world, prefix: new HarmonyMethod(typeof(TailKillerProbe), nameof(WorldPrefix)), postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(WorldPostfix)));
            if (storyteller != null)
                Harmony.Patch(storyteller, prefix: new HarmonyMethod(typeof(TailKillerProbe), nameof(StoryPrefix)), postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(StoryPostfix)));
        }

        private static void PatchTickBoundary()
        {
            MethodInfo tick = AccessTools.Method(typeof(TickManager), "DoSingleTick");
            if (tick == null) throw new MissingMethodException("TickManager.DoSingleTick");
            Harmony.Patch(tick,
                prefix: new HarmonyMethod(typeof(TailKillerProbe), nameof(TickPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(TickPostfix)) { priority = Priority.Last });
        }

        private static void HookRuntimeReport()
        {
            Type t = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo m = t == null ? null : AccessTools.Method(t, "LogRuntimeReport");
            if (m == null) return;
            Harmony.Patch(m, postfix: new HarmonyMethod(typeof(TailKillerProbe), nameof(ReportPostfix)) { priority = Priority.Last });
            reportHooked = true;
        }

        private static void ResolveExistingDetailProfiler()
        {
            Type t = AccessTools.TypeByName("RimMT.WorkGiverDetailPatches");
            if (t == null) return;
            detailStart = AccessTools.Method(t, "StartCapture");
            PropertyInfo p = t.GetProperty("CaptureActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            detailIsActiveGetter = p == null ? null : p.GetGetMethod(true);
        }

        private static bool CampaignActive()
        {
            if (campaignComplete || Current.ProgramState != ProgramState.Playing) return false;
            int now = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (startTick < 0) startTick = now;
            int age = now - startTick;
            if (age < WarmupTicks) return false;
            if (age >= WarmupTicks + CampaignTicks)
            {
                campaignComplete = true;
                return false;
            }
            return true;
        }

        public static void TickPrefix()
        {
            currentPathCalls = 0;
            currentPathOver2 = 0;
            currentPathOver5 = 0;
            currentPathOver10 = 0;
            currentPathOver30 = 0;
            currentPathTicks = 0;
            currentPathMaxTicks = 0;
            currentPathUniqueEstimate = 0;
            lastPathPawn = null;
        }

        public static void TickPostfix()
        {
            if (!CampaignActive()) return;
            sampledTicks++;
            double totalMs = ToMs(currentPathTicks);
            if (totalMs >= PathBurstKeepMs)
            {
                string extra = "calls=" + currentPathCalls + ", max=" + ToMs(currentPathMaxTicks).ToString("F3") + "ms, >2/5/10/30=" + currentPathOver2 + "/" + currentPathOver5 + "/" + currentPathOver10 + "/" + currentPathOver30 + ", uniquePawnEstimate=" + currentPathUniqueEstimate;
                Keep(PathBurstTop, totalMs, "PathBurst", extra);
            }
        }

        public static void JobPrefix(Pawn __0, ref ProbeState __state)
        {
            __state.Started = CampaignActive() ? Stopwatch.GetTimestamp() : 0L;
            __state.Label = __0 == null ? "<null pawn>" : __0.LabelShortCap;
        }

        public static void JobPostfix(Pawn __0, ProbeState __state)
        {
            if (__state.Started == 0L) return;
            double ms = ToMs(Stopwatch.GetTimestamp() - __state.Started);
            jobCalls++;
            if (ms > maxJobMs) { maxJobMs = ms; maxJobPawn = __state.Label; }
            if (ms < JobArmMs) return;
            slowJobCalls++;
            if (!jobDetailArmAttempted)
            {
                jobDetailArmAttempted = true;
                try
                {
                    bool already = false;
                    if (detailIsActiveGetter != null) already = (bool)detailIsActiveGetter.Invoke(null, null);
                    if (!already && detailStart != null) jobDetailArmSucceeded = (bool)detailStart.Invoke(null, null);
                    else jobDetailArmSucceeded = already;
                    Log.Message("[RimMT-RC2] First >=32ms JobPackage observed: " + ms.ToString("F3") + "ms pawn=" + __state.Label + ". Existing JobGiver detail capture auto-arm=" + jobDetailArmSucceeded + ".");
                }
                catch (Exception ex)
                {
                    failures++;
                    Log.Warning("[RimMT-RC2] JobGiver detail auto-arm failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public static void DriverPrefix(JobDriver __instance, ref ProbeState __state)
        {
            __state.Started = CampaignActive() ? Stopwatch.GetTimestamp() : 0L;
            if (__state.Started == 0L) return;
            string driver = __instance == null ? "<null>" : __instance.GetType().FullName;
            string job = (__instance == null || __instance.job == null || __instance.job.def == null) ? "<no-job>" : __instance.job.def.defName;
            string pawn = (__instance == null || __instance.pawn == null) ? "<no-pawn>" : __instance.pawn.LabelShortCap;
            __state.Label = pawn + " :: " + job + " :: " + driver;
        }

        public static void DriverPostfix(ProbeState __state)
        {
            if (__state.Started == 0L) return;
            double ms = ToMs(Stopwatch.GetTimestamp() - __state.Started);
            driverCalls++;
            if (ms > maxDriverMs) maxDriverMs = ms;
            if (ms >= DriverKeepMs)
            {
                driverOver4++;
                Keep(DriverTop, ms, __state.Label, null);
            }
        }

        public static void PathPrefix(object[] __args, ref ProbeState __state)
        {
            __state.Started = CampaignActive() ? Stopwatch.GetTimestamp() : 0L;
            __state.Label = null;
            if (__state.Started == 0L || __args == null) return;
            for (int i = 0; i < __args.Length; i++)
            {
                Pawn pawn = __args[i] as Pawn;
                if (pawn != null)
                {
                    __state.Label = pawn.LabelShortCap;
                    if (!object.ReferenceEquals(lastPathPawn, pawn)) { currentPathUniqueEstimate++; lastPathPawn = pawn; }
                    break;
                }
                if (__args[i] is TraverseParms)
                {
                    TraverseParms tp = (TraverseParms)__args[i];
                    Pawn p = tp.pawn;
                    if (p != null)
                    {
                        __state.Label = p.LabelShortCap;
                        if (!object.ReferenceEquals(lastPathPawn, p)) { currentPathUniqueEstimate++; lastPathPawn = p; }
                        break;
                    }
                }
            }
        }

        public static void PathPostfix(ProbeState __state)
        {
            if (__state.Started == 0L) return;
            long ticks = Stopwatch.GetTimestamp() - __state.Started;
            double ms = ToMs(ticks);
            pathCalls++;
            currentPathCalls++;
            currentPathTicks += ticks;
            if (ticks > currentPathMaxTicks) currentPathMaxTicks = ticks;
            if (ms >= 2.0) { pathOver2++; currentPathOver2++; }
            if (ms >= 5.0) { pathOver5++; currentPathOver5++; }
            if (ms >= 10.0) { pathOver10++; currentPathOver10++; }
            if (ms >= 30.0) { pathOver30++; currentPathOver30++; }
        }

        public static void WorldPrefix(ref long __state) { __state = CampaignActive() ? Stopwatch.GetTimestamp() : 0L; }
        public static void WorldPostfix(long __state)
        {
            if (__state == 0L) return;
            double ms = ToMs(Stopwatch.GetTimestamp() - __state);
            worldCalls++;
            if (ms > maxWorldMs) maxWorldMs = ms;
            if (ms >= WorldKeepMs) Keep(WorldTop, ms, "WorldTick", null);
        }

        public static void StoryPrefix(ref long __state) { __state = CampaignActive() ? Stopwatch.GetTimestamp() : 0L; }
        public static void StoryPostfix(long __state)
        {
            if (__state == 0L) return;
            double ms = ToMs(Stopwatch.GetTimestamp() - __state);
            storytellerCalls++;
            if (ms > maxStorytellerMs) maxStorytellerMs = ms;
            if (ms >= WorldKeepMs) Keep(StorytellerTop, ms, "StorytellerTick", null);
        }

        private static void Keep(List<Sample> list, double ms, string label, string extra)
        {
            Sample s = new Sample { Ms = ms, Label = label ?? "?", Extra = extra };
            int insert = 0;
            while (insert < list.Count && list[insert].Ms >= ms) insert++;
            list.Insert(insert, s);
            if (list.Count > KeepTop) list.RemoveAt(list.Count - 1);
        }

        private static double ToMs(long ticks)
        {
            return ticks <= 0L ? 0.0 : (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
        }

        public static void ReportPostfix()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[RimMT] RC2 tail-killer report: campaignComplete=").Append(campaignComplete)
              .Append(", sampledTicks=").Append(sampledTicks)
              .Append(", jobCalls/slow>=32=").Append(jobCalls).Append('/').Append(slowJobCalls)
              .Append(", maxJobMs=").Append(maxJobMs.ToString("F3")).Append(" pawn=").Append(maxJobPawn ?? "?")
              .Append(", detailAutoArm=").Append(jobDetailArmAttempted).Append('/').Append(jobDetailArmSucceeded)
              .Append(", driverCalls/>=4ms=").Append(driverCalls).Append('/').Append(driverOver4)
              .Append(", maxDriverMs=").Append(maxDriverMs.ToString("F3"))
              .Append(", pathCalls >2/5/10/30=").Append(pathCalls).Append(' ').Append(pathOver2).Append('/').Append(pathOver5).Append('/').Append(pathOver10).Append('/').Append(pathOver30)
              .Append(", worldCalls/maxMs=").Append(worldCalls).Append('/').Append(maxWorldMs.ToString("F3"))
              .Append(", storytellerCalls/maxMs=").Append(storytellerCalls).Append('/').Append(maxStorytellerMs.ToString("F3"))
              .Append(", failures=").Append(failures).Append('.');
            AppendTop(sb, "Driver", DriverTop);
            AppendTop(sb, "PathBurst", PathBurstTop);
            AppendTop(sb, "World", WorldTop);
            AppendTop(sb, "Storyteller", StorytellerTop);
            Log.Message(sb.ToString());
        }

        private static void AppendTop(StringBuilder sb, string name, List<Sample> list)
        {
            sb.Append("\n[RimMT] RC2 ").Append(name).Append(" top:");
            int n = Math.Min(list.Count, 8);
            for (int i = 0; i < n; i++)
            {
                Sample s = list[i];
                sb.Append("\n #").Append(i + 1).Append(' ').Append(s.Ms.ToString("F3")).Append("ms :: ").Append(s.Label);
                if (!string.IsNullOrEmpty(s.Extra)) sb.Append(" :: ").Append(s.Extra);
            }
            if (n == 0) sb.Append(" none");
        }
    }
}
