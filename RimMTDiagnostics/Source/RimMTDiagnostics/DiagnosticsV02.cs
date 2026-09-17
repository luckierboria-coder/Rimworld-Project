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
    /// v0.2 targeted diagnostics. Everything here is observation-only. No Job result, reservation,
    /// path, ThinkResult, WorkGiver return value or RimMT production state is mutated.
    /// </summary>
    internal static class DiagnosticsV02
    {
        private const string BurstHarmonyId = "allen.rimmt.diagnostics.workgiverburst";
        private static long rootFrames;
        private static long lastTickWall;
        private static long lastTickRootFrame;
        private static readonly GapEvent[] GapRing = new GapEvent[24];
        private static int gapPos;
        private static int gapCount;
        private static long gapOver250;
        private static long gapOver1000;
        private static long maxGapUs;
        private static long maxGapFrames;

        private static readonly Dictionary<string, StageStat> PatherStages = new Dictionary<string, StageStat>(StringComparer.Ordinal);
        private static int patherPatched;
        private static int patherMissing;

        [ThreadStatic] private static int reachCaptureDepth;
        private static StageStat reachCaptureDrain = new StageStat();
        private static StageStat reachRegionAllows = new StageStat();
        private static int reachCapturePatched;
        private static int reachAllowsPatched;
        private static int reachPatchFailures;

        private static readonly Dictionary<int, IdleDwell> IdleDwells = new Dictionary<int, IdleDwell>();
        private static long waitCensusSamples;
        private static long waitCensusIdleSamples;
        private static long waitCensusEvictions;
        private static long longestObservedIdleTicks;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            PatchRootUpdate(harmony);
            PatchPatherStages(harmony);
            PatchReachCapture(harmony);
        }

        internal static void OnTickBegin()
        {
            WorkGiverBurstProfiler.OnTickBoundary();
            if (!RimMTDiagnosticsSettings.EnablePauseGapTrace) return;
            long now = Stopwatch.GetTimestamp();
            long frame = rootFrames;
            long previous = lastTickWall;
            long previousFrame = lastTickRootFrame;
            lastTickWall = now;
            lastTickRootFrame = frame;
            if (previous == 0L) return;
            long us = (long)((now - previous) * 1000000.0 / Stopwatch.Frequency);
            long frames = frame - previousFrame;
            if (us > maxGapUs) maxGapUs = us;
            if (frames > maxGapFrames) maxGapFrames = frames;
            if (us < 250000L && frames < 120L) return;
            gapOver250++;
            if (us >= 1000000L) gapOver1000++;
            int tick = -1;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; } catch { }
            GapRing[gapPos] = new GapEvent(tick, us, frames);
            gapPos = (gapPos + 1) % GapRing.Length;
            if (gapCount < GapRing.Length) gapCount++;
        }

        internal static void OnTickEnd()
        {
            WorkGiverBurstProfiler.OnTickBoundary();
        }

        internal static void ObservePawn(Pawn pawn)
        {
            if (!RimMTDiagnosticsSettings.EnableWaitTrace || !DiagnosticsHub.DeepActive || pawn == null) return;
            try
            {
                if (pawn.Destroyed || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.Faction != Faction.OfPlayer) return;
                waitCensusSamples++;
                Job job = pawn.CurJob;
                bool idle = IsIdle(job);
                if (idle) waitCensusIdleSamples++;
                int tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
                int id = pawn.thingIDNumber;
                IdleDwell state;
                if (!IdleDwells.TryGetValue(id, out state))
                {
                    if (IdleDwells.Count >= 256)
                    {
                        int remove = IdleDwells.Keys.FirstOrDefault();
                        IdleDwells.Remove(remove);
                        waitCensusEvictions++;
                    }
                    state = new IdleDwell { PawnId = id, PawnLabel = PawnLabel(pawn), IdleSinceTick = -1 };
                }

                if (!idle)
                {
                    state.IdleSinceTick = -1;
                    state.LastIdleJob = null;
                }
                else
                {
                    if (state.IdleSinceTick < 0) state.IdleSinceTick = tick;
                    state.LastIdleJob = job == null || job.def == null ? "<null>" : job.def.defName;
                    long duration = tick >= 0 && state.IdleSinceTick >= 0 ? tick - state.IdleSinceTick : 0;
                    if (duration > state.LongestIdleTicks) state.LongestIdleTicks = duration;
                    if (duration > longestObservedIdleTicks) longestObservedIdleTicks = duration;
                }
                state.LastSeenTick = tick;
                IdleDwells[id] = state;
            }
            catch { }
        }

        internal static void ObserveDetermineTail(long started)
        {
            WorkGiverBurstProfiler.ObserveDetermine(started);
        }

        internal static void ObserveInfrastructure(bool reach)
        {
            WorkGiverBurstProfiler.ObserveInfrastructure(reach);
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("[RimMT Diagnostics v0.2 targeted]");
            sb.Append("PauseGap: rootFrames=").Append(rootFrames)
              .Append(", gaps>=250ms=").Append(gapOver250)
              .Append(", >=1000ms=").Append(gapOver1000)
              .Append(", maxGapMs=").Append((maxGapUs / 1000.0).ToString("F2"))
              .Append(", maxRootFramesBetweenTicks=").Append(maxGapFrames).AppendLine();
            sb.AppendLine("RecentGaps=" + GapSummary());
            sb.Append("WaitCensus: samples=").Append(waitCensusSamples)
              .Append(", idleSamples=").Append(waitCensusIdleSamples)
              .Append(", trackedPawns=").Append(IdleDwells.Count)
              .Append(", longestObservedIdleTicks=").Append(longestObservedIdleTicks)
              .Append(", evictions=").Append(waitCensusEvictions).AppendLine();
            sb.AppendLine("ActiveIdle=" + ActiveIdleSummary());
            sb.Append("PatherBreakdown: patched=").Append(patherPatched).Append(", missing=").Append(patherMissing)
              .Append(", stages=").Append(PatherStages.Count).AppendLine();
            foreach (var kv in PatherStages.OrderByDescending(kv => kv.Value.TotalUs).Take(16))
                sb.AppendLine("  " + kv.Key + " " + kv.Value.Summary());
            sb.Append("ReachCaptureTrace: captureMethodPatched=").Append(reachCapturePatched)
              .Append(", regionAllowsPatched=").Append(reachAllowsPatched)
              .Append(", failures=").Append(reachPatchFailures).AppendLine();
            sb.AppendLine("  Drain " + reachCaptureDrain.Summary());
            sb.AppendLine("  Region.Allows(in capture) " + reachRegionAllows.Summary());
            sb.Append(WorkGiverBurstProfiler.Summary());
            return sb.ToString();
        }

        internal static void Reset()
        {
            rootFrames = lastTickWall = lastTickRootFrame = 0L;
            Array.Clear(GapRing, 0, GapRing.Length);
            gapPos = gapCount = 0;
            gapOver250 = gapOver1000 = maxGapUs = maxGapFrames = 0L;
            PatherStages.Clear();
            reachCaptureDrain = new StageStat();
            reachRegionAllows = new StageStat();
            IdleDwells.Clear();
            waitCensusSamples = waitCensusIdleSamples = waitCensusEvictions = longestObservedIdleTicks = 0L;
            WorkGiverBurstProfiler.Reset();
        }

        private static void PatchRootUpdate(Harmony harmony)
        {
            try
            {
                MethodBase m = AccessTools.Method(typeof(Root_Play), "Update");
                if (m != null) harmony.Patch(m, postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(RootUpdatePostfix)) { priority = Priority.Last });
            }
            catch { }
        }

        public static void RootUpdatePostfix()
        {
            rootFrames++;
        }

        private static void PatchPatherStages(Harmony harmony)
        {
            string[] names = new string[]
            {
                "TryEnterNextPathCell", "SetupMoveIntoNextCell", "TrySetNewPath", "NeedNewPath",
                "CostToMoveIntoCell", "RecoverFromUnwalkablePosition", "TryRecoverFromUnwalkablePosition", "StartPath"
            };
            HashSet<string> wanted = new HashSet<string>(names, StringComparer.Ordinal);
            try
            {
                List<MethodInfo> methods = AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower));
                for (int i = 0; i < methods.Count; i++)
                {
                    MethodInfo m = methods[i];
                    if (m == null || !wanted.Contains(m.Name)) continue;
                    try
                    {
                        harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(PatherStagePrefix)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(PatherStagePostfix)) { priority = Priority.Last });
                        patherPatched++;
                        wanted.Remove(m.Name);
                    }
                    catch { patherMissing++; }
                }
                patherMissing += wanted.Count;
            }
            catch { patherMissing += names.Length; }
        }

        public static void PatherStagePrefix(MethodBase __originalMethod, ref long __state)
        {
            __state = RimMTDiagnosticsSettings.EnablePatherBreakdown && DiagnosticsHub.DeepActive ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void PatherStagePostfix(MethodBase __originalMethod, long __state)
        {
            if (__state == 0L || __originalMethod == null) return;
            long us = ElapsedUs(__state);
            string name = __originalMethod.Name;
            StageStat stat;
            if (!PatherStages.TryGetValue(name, out stat)) stat = new StageStat();
            stat.Add(us);
            PatherStages[name] = stat;
        }

        private static void PatchReachCapture(Harmony harmony)
        {
            try
            {
                Type t = FindLoadedType("RimMT.AggressiveReachabilityProfilesV17");
                if (t != null)
                {
                    MethodInfo drain = t.GetMethod("DrainProfileCaptureBudget", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (drain != null)
                    {
                        harmony.Patch(drain,
                            prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(ReachCapturePrefix)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(ReachCapturePostfix)) { priority = Priority.Last });
                        reachCapturePatched++;
                    }
                }

                List<MethodInfo> allows = AccessTools.GetDeclaredMethods(typeof(Region));
                for (int i = 0; i < allows.Count; i++)
                {
                    MethodInfo m = allows[i];
                    if (m == null || m.Name != "Allows") continue;
                    try
                    {
                        harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(RegionAllowsPrefix)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(RegionAllowsPostfix)) { priority = Priority.Last });
                        reachAllowsPatched++;
                    }
                    catch { reachPatchFailures++; }
                }
            }
            catch { reachPatchFailures++; }
        }

        public static void ReachCapturePrefix(ref long __state)
        {
            if (!RimMTDiagnosticsSettings.EnableReachCaptureTrace) { __state = 0L; return; }
            reachCaptureDepth++;
            __state = Stopwatch.GetTimestamp();
        }

        public static void ReachCapturePostfix(long __state)
        {
            if (reachCaptureDepth > 0) reachCaptureDepth--;
            if (__state == 0L) return;
            reachCaptureDrain.Add(ElapsedUs(__state));
        }

        public static void RegionAllowsPrefix(ref long __state)
        {
            __state = RimMTDiagnosticsSettings.EnableReachCaptureTrace && reachCaptureDepth > 0 ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void RegionAllowsPostfix(long __state)
        {
            if (__state == 0L) return;
            reachRegionAllows.Add(ElapsedUs(__state));
        }

        private static Type FindLoadedType(string fullName)
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type t = assemblies[i].GetType(fullName, false);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        private static bool IsIdle(Job job)
        {
            if (job == null || job.def == null || string.IsNullOrEmpty(job.def.defName)) return false;
            string n = job.def.defName;
            return n == "Wait" || n.StartsWith("Wait_", StringComparison.Ordinal) ||
                   n.IndexOf("IdleWait", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string PawnLabel(Pawn pawn)
        {
            if (pawn == null) return "<null>";
            string def = pawn.def == null ? "Pawn" : pawn.def.defName;
            string name = null;
            try { name = pawn.LabelShortCap; } catch { }
            return string.IsNullOrEmpty(name) ? def + "#" + pawn.thingIDNumber : name + "#" + pawn.thingIDNumber;
        }

        private static string ActiveIdleSummary()
        {
            int now = -1;
            try { now = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; } catch { }
            return string.Join("; ", IdleDwells.Values.Where(s => s.IdleSinceTick >= 0)
                .OrderByDescending(s => now >= 0 ? now - s.IdleSinceTick : 0).Take(16)
                .Select(s => s.PawnLabel + " job=" + (s.LastIdleJob ?? "<none>") +
                    " durTicks~=" + (now >= 0 ? Math.Max(0, now - s.IdleSinceTick) : 0) +
                    " longest=" + s.LongestIdleTicks).ToArray());
        }

        private static string GapSummary()
        {
            if (gapCount == 0) return "none";
            List<string> rows = new List<string>();
            int start = gapCount == GapRing.Length ? gapPos : 0;
            for (int i = 0; i < gapCount; i++)
            {
                GapEvent e = GapRing[(start + i) % GapRing.Length];
                rows.Add("tick=" + e.Tick + ",gapMs=" + (e.Us / 1000.0).ToString("F1") + ",rootFrames=" + e.RootFrames);
            }
            return string.Join("; ", rows.ToArray());
        }

        private static long ElapsedUs(long start)
        {
            if (start == 0L) return 0L;
            long d = Stopwatch.GetTimestamp() - start;
            if (d <= 0L) return 0L;
            return (long)(d * 1000000.0 / Stopwatch.Frequency);
        }

        private struct GapEvent
        {
            internal readonly int Tick;
            internal readonly long Us;
            internal readonly long RootFrames;
            internal GapEvent(int tick, long us, long rootFrames) { Tick = tick; Us = us; RootFrames = rootFrames; }
        }

        private sealed class IdleDwell
        {
            internal int PawnId;
            internal string PawnLabel;
            internal int IdleSinceTick;
            internal int LastSeenTick;
            internal string LastIdleJob;
            internal long LongestIdleTicks;
        }

        private struct StageStat
        {
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
            internal long Over2;
            internal long Over5;
            internal long Over20;
            internal void Add(long us)
            {
                if (us < 0L) return;
                Calls++;
                TotalUs += us;
                if (us > MaxUs) MaxUs = us;
                if (us >= 2000L) Over2++;
                if (us >= 5000L) Over5++;
                if (us >= 20000L) Over20++;
            }
            internal string Summary()
            {
                return "calls=" + Calls + ",avgUs=" + (Calls == 0 ? "0.0" : (TotalUs / (double)Calls).ToString("F1")) +
                    ",>2/5/20ms=" + Over2 + "/" + Over5 + "/" + Over20 + ",maxMs=" + (MaxUs / 1000.0).ToString("F2");
            }
        }

        private static class WorkGiverBurstProfiler
        {
            private static readonly HashSet<string> MethodNames = new HashSet<string>(new string[]
            {
                "ShouldSkip", "NonScanJob", "PotentialWorkThingsGlobal", "HasJobOnThing", "JobOnThing"
            }, StringComparer.Ordinal);
            private static readonly Dictionary<string, WorkStat> Stats = new Dictionary<string, WorkStat>(StringComparer.Ordinal);
            private static Harmony burstHarmony;
            private static bool requested;
            private static bool active;
            private static bool stopRequested;
            private static int packagesRemaining;
            private static int patchedMethods;
            private static int patchFailures;
            private static long triggers;
            private static long maxTriggerUs;
            private static long packages;
            private static long packageTotalUs;
            private static long packageMaxUs;
            [ThreadStatic] private static string currentWorkGiver;

            internal static void ObserveDetermine(long started)
            {
                if (!RimMTDiagnosticsSettings.EnableWorkGiverBurst || started == 0L) return;
                long us = ElapsedUs(started);
                long threshold = RimMTDiagnosticsSettings.WorkGiverTriggerMs * 1000L;
                if (us < threshold) return;
                triggers++;
                if (us > maxTriggerUs) maxTriggerUs = us;
                if (!active && !requested) requested = true;
            }

            internal static void ObserveInfrastructure(bool reach)
            {
                if (!active || string.IsNullOrEmpty(currentWorkGiver)) return;
                WorkStat stat = Get(currentWorkGiver);
                if (reach) stat.ReachCalls++;
                else stat.GenClosestCalls++;
                Stats[currentWorkGiver] = stat;
            }

            internal static void OnTickBoundary()
            {
                if (stopRequested)
                {
                    Stop();
                    stopRequested = false;
                }
                if (requested && !active)
                {
                    requested = false;
                    Start();
                }
            }

            private static void Start()
            {
                if (!RimMTDiagnosticsSettings.EnableWorkGiverBurst) return;
                try
                {
                    burstHarmony = new Harmony(BurstHarmonyId);
                    MethodInfo package = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                    if (package != null)
                    {
                        burstHarmony.Patch(package,
                            prefix: new HarmonyMethod(typeof(WorkGiverBurstProfiler), nameof(PackagePrefix)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(WorkGiverBurstProfiler), nameof(PackagePostfix)) { priority = Priority.Last });
                        patchedMethods++;
                    }

                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    HashSet<MethodBase> seen = new HashSet<MethodBase>();
                    for (int ai = 0; ai < assemblies.Length; ai++)
                    {
                        Type[] types;
                        try { types = assemblies[ai].GetTypes(); }
                        catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                        catch { continue; }
                        if (types == null) continue;
                        for (int ti = 0; ti < types.Length; ti++)
                        {
                            Type t = types[ti];
                            if (t == null || t.IsAbstract || !typeof(WorkGiver).IsAssignableFrom(t)) continue;
                            MethodInfo[] methods;
                            try { methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                            catch { continue; }
                            for (int mi = 0; mi < methods.Length; mi++)
                            {
                                MethodInfo m = methods[mi];
                                if (m == null || !MethodNames.Contains(m.Name) || m.ContainsGenericParameters || !seen.Add(m)) continue;
                                try
                                {
                                    burstHarmony.Patch(m,
                                        prefix: new HarmonyMethod(typeof(WorkGiverBurstProfiler), nameof(WorkPrefix)) { priority = Priority.First },
                                        postfix: new HarmonyMethod(typeof(WorkGiverBurstProfiler), nameof(WorkPostfix)) { priority = Priority.Last });
                                    patchedMethods++;
                                }
                                catch { patchFailures++; }
                            }
                        }
                    }
                    packagesRemaining = RimMTDiagnosticsSettings.WorkGiverBurstPackages;
                    active = true;
                }
                catch
                {
                    patchFailures++;
                    Stop();
                }
            }

            private static void Stop()
            {
                try { if (burstHarmony != null) burstHarmony.UnpatchAll(BurstHarmonyId); } catch { }
                burstHarmony = null;
                active = false;
                packagesRemaining = 0;
                currentWorkGiver = null;
            }

            public static void PackagePrefix(ref long __state)
            {
                __state = active ? Stopwatch.GetTimestamp() : 0L;
            }

            public static void PackagePostfix(long __state)
            {
                if (!active) return;
                long us = ElapsedUs(__state);
                packages++;
                packageTotalUs += us;
                if (us > packageMaxUs) packageMaxUs = us;
                packagesRemaining--;
                if (packagesRemaining <= 0) stopRequested = true;
            }

            public static void WorkPrefix(object __instance, MethodBase __originalMethod, ref WorkCallState __state)
            {
                __state = default(WorkCallState);
                if (!active || __instance == null || __originalMethod == null) return;
                __state.Started = Stopwatch.GetTimestamp();
                __state.Previous = currentWorkGiver;
                Type t = __instance.GetType();
                __state.Key = t.FullName ?? t.Name;
                __state.Method = __originalMethod.Name;
                currentWorkGiver = __state.Key;
            }

            public static void WorkPostfix(WorkCallState __state)
            {
                if (__state.Started == 0L) return;
                long us = ElapsedUs(__state.Started);
                WorkStat stat = Get(__state.Key);
                stat.Calls++;
                stat.TotalUs += us;
                if (us > stat.MaxUs) stat.MaxUs = us;
                if (us >= 5000L) stat.Over5++;
                if (us >= 20000L) stat.Over20++;
                long count;
                stat.MethodCalls.TryGetValue(__state.Method, out count);
                stat.MethodCalls[__state.Method] = count + 1L;
                Stats[__state.Key] = stat;
                currentWorkGiver = __state.Previous;
            }

            private static WorkStat Get(string key)
            {
                WorkStat stat;
                if (!Stats.TryGetValue(key, out stat)) stat = new WorkStat();
                return stat;
            }

            internal static string Summary()
            {
                StringBuilder sb = new StringBuilder(8192);
                sb.Append("WorkGiverBurst: active=").Append(active).Append(", requested=").Append(requested)
                  .Append(", triggers=").Append(triggers).Append(", maxTriggerMs=").Append((maxTriggerUs / 1000.0).ToString("F2"))
                  .Append(", patchedMethods=").Append(patchedMethods).Append(", patchFailures=").Append(patchFailures)
                  .Append(", packages=").Append(packages).Append(", avgPackageMs=").Append(packages == 0 ? "0.00" : (packageTotalUs / 1000.0 / packages).ToString("F2"))
                  .Append(", maxPackageMs=").Append((packageMaxUs / 1000.0).ToString("F2")).AppendLine();
                foreach (var kv in Stats.OrderByDescending(kv => kv.Value.TotalUs).Take(20))
                {
                    WorkStat s = kv.Value;
                    sb.Append("  ").Append(kv.Key).Append(": calls=").Append(s.Calls)
                      .Append(", totalMs=").Append((s.TotalUs / 1000.0).ToString("F2"))
                      .Append(", avgUs=").Append(s.Calls == 0 ? "0.0" : (s.TotalUs / (double)s.Calls).ToString("F1"))
                      .Append(", >5/20ms=").Append(s.Over5).Append('/').Append(s.Over20)
                      .Append(", maxMs=").Append((s.MaxUs / 1000.0).ToString("F2"))
                      .Append(", GenClosest=").Append(s.GenClosestCalls).Append(", Reach=").Append(s.ReachCalls)
                      .Append(", methods=").Append(string.Join(",", s.MethodCalls.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key + "=" + x.Value).ToArray()))
                      .AppendLine();
                }
                return sb.ToString();
            }

            internal static void Reset()
            {
                Stats.Clear();
                triggers = maxTriggerUs = packages = packageTotalUs = packageMaxUs = 0L;
                patchedMethods = patchFailures = 0;
            }

            private struct WorkCallState
            {
                internal long Started;
                internal string Previous;
                internal string Key;
                internal string Method;
            }

            private struct WorkStat
            {
                internal long Calls;
                internal long TotalUs;
                internal long MaxUs;
                internal long Over5;
                internal long Over20;
                internal long GenClosestCalls;
                internal long ReachCalls;
                internal Dictionary<string, long> MethodCalls;

                internal void Ensure()
                {
                    if (MethodCalls == null) MethodCalls = new Dictionary<string, long>(StringComparer.Ordinal);
                }
            }
        }
    }
}
