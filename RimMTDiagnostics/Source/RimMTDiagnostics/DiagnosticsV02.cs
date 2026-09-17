using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// v0.2 diagnostics only. No gameplay mutation, no worker scheduling, no waits.
    /// Adds: current-job Wait census, pause/resume markers, sampled WorkGiver method timing,
    /// Pather child timing, ReachProfile Region.Allows capture attribution, and known catastrophic
    /// world-component timing. Everything is optional by removing/disabling the diagnostics mod.
    /// </summary>
    internal static class DiagnosticsV02
    {
        private const int WaitCensusEveryTicks = 30;
        private const int MaxWaitStates = 512;
        private const int MaxRows = 48;
        private const long GapThresholdMs = 1000;

        private static readonly Dictionary<int, LiveWaitState> LiveWait = new Dictionary<int, LiveWaitState>();
        private static readonly Dictionary<string, MethodStat> PatherChildren = new Dictionary<string, MethodStat>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodStat> WorkGivers = new Dictionary<string, MethodStat>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodStat> WorldCatastrophic = new Dictionary<string, MethodStat>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodStat> ReachCaptureMethods = new Dictionary<string, MethodStat>(StringComparer.Ordinal);

        [ThreadStatic] private static int reachCaptureDepth;
        private static long lastTickWall;
        private static long pauseGapCount;
        private static long maxPauseGapMs;
        private static int lastGapGameTick = -1;
        private static long waitCensuses;
        private static long waitPawnSamples;
        private static long workGiverPatched;
        private static long patherChildPatched;
        private static long reachCapturePatched;
        private static long worldCatPatched;
        private static long installFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            PatchPatherChildren(harmony);
            PatchWorkGiverMethods(harmony);
            PatchReachProfileCapture(harmony);
            PatchKnownWorldCatastrophic(harmony);
        }

        internal static void OnTickBegin()
        {
            long now = Stopwatch.GetTimestamp();
            long prev = lastTickWall;
            lastTickWall = now;
            if (prev == 0L) return;
            long ms = (now - prev) * 1000L / Stopwatch.Frequency;
            if (ms < GapThresholdMs) return;
            pauseGapCount++;
            if (ms > maxPauseGapMs) maxPauseGapMs = ms;
            try { lastGapGameTick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { lastGapGameTick = -1; }
        }

        internal static void OnTickEnd()
        {
            if (!RimMTDiagnosticsSettings.EnableWaitTrace) return;
            int tick;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return; }
            if (tick < 0 || tick % WaitCensusEveryTicks != 0) return;
            RunWaitCensus(tick);
        }

        private static void RunWaitCensus(int tick)
        {
            waitCensuses++;
            HashSet<int> seen = new HashSet<int>();
            try
            {
                List<Map> maps = Find.Maps;
                for (int mi = 0; mi < maps.Count; mi++)
                {
                    Map map = maps[mi];
                    if (map == null || map.mapPawns == null) continue;
                    List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Pawn pawn = pawns[i];
                        if (pawn == null || pawn.Destroyed) continue;
                        int id = pawn.thingIDNumber;
                        seen.Add(id);
                        waitPawnSamples++;
                        Job job = null;
                        try { job = pawn.CurJob; } catch { }
                        bool idle = IsIdle(job);
                        LiveWaitState state;
                        if (!LiveWait.TryGetValue(id, out state))
                        {
                            if (LiveWait.Count >= MaxWaitStates)
                            {
                                int first = LiveWait.Keys.FirstOrDefault();
                                LiveWait.Remove(first);
                            }
                            state = new LiveWaitState { PawnId = id, PawnLabel = PawnLabel(pawn), IdleSince = -1 };
                        }
                        if (idle)
                        {
                            if (state.IdleSince < 0) state.IdleSince = tick;
                            state.LastJob = job == null || job.def == null ? "<null>" : job.def.defName;
                            state.LastSeen = tick;
                        }
                        else
                        {
                            state.IdleSince = -1;
                            state.LastJob = job == null || job.def == null ? "<null>" : job.def.defName;
                            state.LastSeen = tick;
                        }
                        LiveWait[id] = state;
                    }
                }
                if (LiveWait.Count > 0)
                {
                    List<int> remove = null;
                    foreach (KeyValuePair<int, LiveWaitState> kv in LiveWait)
                    {
                        if (seen.Contains(kv.Key)) continue;
                        if (remove == null) remove = new List<int>();
                        remove.Add(kv.Key);
                    }
                    if (remove != null) for (int i = 0; i < remove.Count; i++) LiveWait.Remove(remove[i]);
                }
            }
            catch { }
        }

        private static void PatchPatherChildren(Harmony harmony)
        {
            string[] names = new string[] { "TryEnterNextPathCell", "SetupMoveIntoNextCell", "CostToMoveIntoCell", "StartPath", "StopDead", "PatherFailed" };
            try
            {
                List<MethodInfo> methods = AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower));
                for (int i = 0; i < methods.Count; i++)
                {
                    MethodInfo m = methods[i];
                    if (m == null || Array.IndexOf(names, m.Name) < 0) continue;
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(PatherChildPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(PatherChildPostfix)) { priority = Priority.Last });
                    patherChildPatched++;
                }
            }
            catch { installFailures++; }
        }

        public static void PatherChildPrefix(ref long __state)
        {
            __state = DiagnosticsHub.DeepActive ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void PatherChildPostfix(MethodBase __originalMethod, long __state)
        {
            RecordMethod(PatherChildren, __originalMethod, __state);
        }

        private static void PatchWorkGiverMethods(Harmony harmony)
        {
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
                                    prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(WorkGiverPrefix)) { priority = Priority.First },
                                    postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(WorkGiverPostfix)) { priority = Priority.Last });
                                workGiverPatched++;
                            }
                            catch { installFailures++; }
                        }
                    }
                }
            }
            catch { installFailures++; }
        }

        public static void WorkGiverPrefix(ref long __state)
        {
            __state = DiagnosticsHub.DeepActive ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void WorkGiverPostfix(object __instance, MethodBase __originalMethod, long __state)
        {
            if (__state == 0L) return;
            string type = __instance == null ? (__originalMethod == null || __originalMethod.DeclaringType == null ? "<unknown>" : __originalMethod.DeclaringType.FullName) : __instance.GetType().FullName;
            string name = type + "." + (__originalMethod == null ? "<method>" : __originalMethod.Name);
            RecordMethod(WorkGivers, name, __state);
        }

        private static void PatchReachProfileCapture(Harmony harmony)
        {
            try
            {
                Assembly rimmt = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RimMT");
                Type t = rimmt == null ? null : rimmt.GetType("RimMT.AggressiveReachabilityProfilesV17", false);
                MethodInfo drain = t == null ? null : t.GetMethod("ProfileCaptureDrainPostfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (drain != null)
                {
                    harmony.Patch(drain,
                        prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(ReachCaptureDrainPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(ReachCaptureDrainPostfix)) { priority = Priority.Last });
                    reachCapturePatched++;
                }
                List<MethodInfo> methods = AccessTools.GetDeclaredMethods(typeof(Region));
                for (int i = 0; i < methods.Count; i++)
                {
                    MethodInfo m = methods[i];
                    if (m == null || m.Name != "Allows") continue;
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(RegionAllowsPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(RegionAllowsPostfix)) { priority = Priority.Last });
                    reachCapturePatched++;
                }
            }
            catch { installFailures++; }
        }

        public static void ReachCaptureDrainPrefix() { reachCaptureDepth++; }
        public static void ReachCaptureDrainPostfix() { if (reachCaptureDepth > 0) reachCaptureDepth--; }
        public static void RegionAllowsPrefix(ref long __state) { __state = reachCaptureDepth > 0 ? Stopwatch.GetTimestamp() : 0L; }
        public static void RegionAllowsPostfix(MethodBase __originalMethod, long __state) { RecordMethod(ReachCaptureMethods, __originalMethod, __state); }

        private static void PatchKnownWorldCatastrophic(Harmony harmony)
        {
            string[] typeNames = new string[] { "VFEEmpire.WorldComponent_Hierarchy", "Vehicles.WorldVehiclePathGrid" };
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int n = 0; n < typeNames.Length; n++)
                {
                    Type t = null;
                    for (int ai = 0; ai < assemblies.Length && t == null; ai++)
                    {
                        try { t = assemblies[ai].GetType(typeNames[n], false); } catch { }
                    }
                    if (t == null) continue;
                    MethodInfo m = AccessTools.Method(t, "WorldComponentTick");
                    if (m == null) continue;
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(WorldCatPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(DiagnosticsV02), nameof(WorldCatPostfix)) { priority = Priority.Last });
                    worldCatPatched++;
                }
            }
            catch { installFailures++; }
        }

        public static void WorldCatPrefix(ref long __state) { __state = Stopwatch.GetTimestamp(); }
        public static void WorldCatPostfix(MethodBase __originalMethod, long __state) { RecordMethod(WorldCatastrophic, __originalMethod, __state); }

        private static void RecordMethod(Dictionary<string, MethodStat> dict, MethodBase method, long started)
        {
            if (started == 0L) return;
            string name = method == null ? "<unknown>" : ((method.DeclaringType == null ? "<type>" : method.DeclaringType.FullName) + "." + method.Name);
            RecordMethod(dict, name, started);
        }

        private static void RecordMethod(Dictionary<string, MethodStat> dict, string name, long started)
        {
            if (started == 0L) return;
            long us = (Stopwatch.GetTimestamp() - started) * 1000000L / Stopwatch.Frequency;
            if (us < 0L) return;
            MethodStat s;
            if (!dict.TryGetValue(name, out s))
            {
                if (dict.Count >= MaxRows) name = "<other>";
                if (!dict.TryGetValue(name, out s)) s = new MethodStat();
            }
            s.Calls++;
            s.TotalUs += us;
            if (us > s.MaxUs) s.MaxUs = us;
            if (us >= 5000L) s.Over5++;
            if (us >= 20000L) s.Over20++;
            if (us >= 50000L) s.Over50++;
            dict[name] = s;
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(12288);
            sb.AppendLine("[Diagnostics v0.2 targeted probes]");
            sb.Append("PauseResume: gaps>=1s=").Append(pauseGapCount).Append(", maxGapMs=").Append(maxPauseGapMs).Append(", lastGapGameTick=").Append(lastGapGameTick).AppendLine();
            sb.Append("LiveWaitCensus: censuses=").Append(waitCensuses).Append(", pawnSamples=").Append(waitPawnSamples).Append(", activeIdle=").Append(LiveWait.Values.Count(s => s.IdleSince >= 0)).AppendLine();
            sb.AppendLine("LongestCurrentWait=" + LongestCurrentWait());
            AppendTop(sb, "PatherChildren", PatherChildren);
            AppendTop(sb, "WorkGiverSampled", WorkGivers);
            AppendTop(sb, "ReachCaptureRegionAllows", ReachCaptureMethods);
            AppendTop(sb, "WorldCatastrophic", WorldCatastrophic);
            sb.Append("Install: workGiverPatched=").Append(workGiverPatched).Append(", patherChildPatched=").Append(patherChildPatched)
              .Append(", reachCapturePatched=").Append(reachCapturePatched).Append(", worldCatPatched=").Append(worldCatPatched)
              .Append(", failures=").Append(installFailures).AppendLine();
            return sb.ToString();
        }

        internal static void Reset()
        {
            LiveWait.Clear();
            PatherChildren.Clear();
            WorkGivers.Clear();
            WorldCatastrophic.Clear();
            ReachCaptureMethods.Clear();
            pauseGapCount = maxPauseGapMs = waitCensuses = waitPawnSamples = 0L;
            lastGapGameTick = -1;
        }

        private static string LongestCurrentWait()
        {
            int tick;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { tick = -1; }
            return string.Join("; ", LiveWait.Values.Where(s => s.IdleSince >= 0)
                .OrderByDescending(s => tick < 0 ? 0 : tick - s.IdleSince).Take(16)
                .Select(s => s.PawnLabel + "#" + s.PawnId + " job=" + s.LastJob + " durTicks=" + (tick < 0 ? 0 : Math.Max(0, tick - s.IdleSince))).ToArray());
        }

        private static void AppendTop(StringBuilder sb, string label, Dictionary<string, MethodStat> dict)
        {
            if (dict.Count == 0) { sb.AppendLine(label + "=none"); return; }
            string rows = string.Join("; ", dict.OrderByDescending(kv => kv.Value.TotalUs).Take(16).Select(kv =>
                kv.Key + "[calls=" + kv.Value.Calls + ",avgUs=" + (kv.Value.Calls == 0 ? 0.0 : kv.Value.TotalUs / (double)kv.Value.Calls).ToString("F1") +
                ",>5/20/50=" + kv.Value.Over5 + "/" + kv.Value.Over20 + "/" + kv.Value.Over50 + ",maxMs=" + (kv.Value.MaxUs / 1000.0).ToString("F2") + "]").ToArray());
            sb.AppendLine(label + "=" + rows);
        }

        private static bool IsIdle(Job job)
        {
            if (job == null || job.def == null || string.IsNullOrEmpty(job.def.defName)) return false;
            string n = job.def.defName;
            return n == "Wait" || n == "Wait_MaintainPosture" || n == "IPPO_IdleWait" || n.StartsWith("Wait_", StringComparison.Ordinal) || n.EndsWith("IdleWait", StringComparison.Ordinal);
        }

        private static string PawnLabel(Pawn pawn)
        {
            try { if (pawn.Name != null) return pawn.Name.ToStringShort; } catch { }
            return pawn.def == null ? "Pawn" : pawn.def.defName;
        }

        private sealed class LiveWaitState
        {
            internal int PawnId;
            internal string PawnLabel;
            internal string LastJob;
            internal int IdleSince;
            internal int LastSeen;
        }

        private struct MethodStat
        {
            internal long Calls, TotalUs, MaxUs, Over5, Over20, Over50;
        }
    }
}
