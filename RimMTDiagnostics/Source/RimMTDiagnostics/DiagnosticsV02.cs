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
    internal static class DiagnosticsV02
    {
        private const int MaxIdleStates = 512;
        private const int MaxWorkRows = 96;
        private const int MaxTailRows = 24;
        private static readonly Dictionary<int, IdleState> IdleStates = new Dictionary<int, IdleState>();
        private static readonly Dictionary<string, WorkRow> WorkRows = new Dictionary<string, WorkRow>(StringComparer.Ordinal);
        private static readonly Dictionary<string, StageRow> PatherRows = new Dictionary<string, StageRow>(StringComparer.Ordinal);
        private static readonly TailEvent[] Catastrophic = new TailEvent[MaxTailRows];

        [ThreadStatic] private static bool inDetermine;
        [ThreadStatic] private static bool captureDetermine;
        [ThreadStatic] private static long determineStart;
        [ThreadStatic] private static Dictionary<string, WorkRow> currentWorkRows;
        [ThreadStatic] private static bool inReachCapture;
        [ThreadStatic] private static long reachCaptureStart;

        private static long lastTickEndStamp;
        private static long gapEvents;
        private static long maxGapUs;
        private static int afterGapTicks;
        private static int burstPackagesRemaining;
        private static long slowDeterminesCaptured;
        private static long workCallsCaptured;
        private static long workCaptureDropped;
        private static long idleCensusRuns;
        private static long idleObserved;
        private static long idleTransitions;
        private static long idleEnded;
        private static long reachCaptureDrains;
        private static long reachCaptureDrainUs;
        private static long reachCaptureDrainMaxUs;
        private static long regionAllowsInCapture;
        private static long regionAllowsCaptureUs;
        private static long regionAllowsCaptureMaxUs;
        private static long regionAllowsOver2;
        private static long regionAllowsOver5;
        private static long regionAllowsOver20;
        private static long catastrophicPos;
        private static long failures;

        internal static void OnTickBegin()
        {
            long now = Stopwatch.GetTimestamp();
            long last = lastTickEndStamp;
            if (last != 0L)
            {
                long us = ToUs(now - last);
                if (us >= 500000L)
                {
                    gapEvents++;
                    if (us > maxGapUs) maxGapUs = us;
                    afterGapTicks = 8;
                }
            }
        }

        internal static void OnTickEnd()
        {
            try
            {
                int tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
                int cadence = Math.Max(1, RimMTDiagnosticsSettings.SampleEveryTicks);
                if (RimMTDiagnosticsSettings.EnableWaitTrace && tick >= 0 && tick % cadence == 0)
                    CensusIdle(tick);
            }
            catch { failures++; }
            if (afterGapTicks > 0) afterGapTicks--;
            lastTickEndStamp = Stopwatch.GetTimestamp();
        }

        internal static void DetermineBegin()
        {
            inDetermine = true;
            determineStart = Stopwatch.GetTimestamp();
            captureDetermine = DiagnosticsHub.DeepActive || burstPackagesRemaining > 0;
            if (captureDetermine)
                currentWorkRows = new Dictionary<string, WorkRow>(StringComparer.Ordinal);
            else
                currentWorkRows = null;
        }

        internal static void DetermineEnd()
        {
            long started = determineStart;
            long us = started == 0L ? 0L : ToUs(Stopwatch.GetTimestamp() - started);
            bool slow = us >= Math.Max(1, RimMTDiagnosticsSettings.TailThresholdMs) * 1000L;
            if (slow)
            {
                burstPackagesRemaining = Math.Max(burstPackagesRemaining, 24);
                if (captureDetermine && currentWorkRows != null)
                {
                    slowDeterminesCaptured++;
                    MergeWorkRows(currentWorkRows);
                }
                else
                {
                    workCaptureDropped++;
                }
            }
            else if (burstPackagesRemaining > 0)
            {
                burstPackagesRemaining--;
                if (captureDetermine && currentWorkRows != null) MergeWorkRows(currentWorkRows);
            }
            currentWorkRows = null;
            captureDetermine = false;
            inDetermine = false;
            determineStart = 0L;
        }

        internal static long WorkPrefix(object instance, MethodBase original)
        {
            if (!inDetermine || !captureDetermine || currentWorkRows == null || instance == null || original == null) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void WorkPostfix(object instance, MethodBase original, long state)
        {
            if (state == 0L || currentWorkRows == null || instance == null || original == null) return;
            long us = ToUs(Stopwatch.GetTimestamp() - state);
            string key = instance.GetType().FullName + "." + original.Name;
            WorkRow row;
            if (!currentWorkRows.TryGetValue(key, out row)) row = new WorkRow(key);
            row.Calls++;
            row.TotalUs += us;
            if (us > row.MaxUs) row.MaxUs = us;
            currentWorkRows[key] = row;
            workCallsCaptured++;
        }

        internal static long PatherStagePrefix(MethodBase original)
        {
            if (!DiagnosticsHub.DeepActive || original == null) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void PatherStagePostfix(MethodBase original, long state)
        {
            if (state == 0L || original == null) return;
            long us = ToUs(Stopwatch.GetTimestamp() - state);
            string key = original.Name;
            StageRow row;
            if (!PatherRows.TryGetValue(key, out row)) row = new StageRow(key);
            row.Calls++;
            row.TotalUs += us;
            if (us > row.MaxUs) row.MaxUs = us;
            if (us >= 5000L) row.Over5++;
            if (us >= 20000L) row.Over20++;
            PatherRows[key] = row;
        }

        internal static void ReachCaptureBegin()
        {
            inReachCapture = true;
            reachCaptureStart = Stopwatch.GetTimestamp();
        }

        internal static void ReachCaptureEnd()
        {
            long started = reachCaptureStart;
            reachCaptureStart = 0L;
            inReachCapture = false;
            if (started == 0L) return;
            long us = ToUs(Stopwatch.GetTimestamp() - started);
            reachCaptureDrains++;
            reachCaptureDrainUs += us;
            if (us > reachCaptureDrainMaxUs) reachCaptureDrainMaxUs = us;
            if (us >= 20000L) AddCatastrophic("ReachProfileCaptureDrain", us);
        }

        internal static long RegionAllowsPrefix()
        {
            if (!inReachCapture) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void RegionAllowsPostfix(long state)
        {
            if (state == 0L) return;
            long us = ToUs(Stopwatch.GetTimestamp() - state);
            regionAllowsInCapture++;
            regionAllowsCaptureUs += us;
            if (us > regionAllowsCaptureMaxUs) regionAllowsCaptureMaxUs = us;
            if (us >= 2000L) regionAllowsOver2++;
            if (us >= 5000L) regionAllowsOver5++;
            if (us >= 20000L) { regionAllowsOver20++; AddCatastrophic("Region.Allows(profile capture)", us); }
        }

        internal static long CatastrophicPrefix()
        {
            return Stopwatch.GetTimestamp();
        }

        internal static void CatastrophicPostfix(MethodBase original, long state)
        {
            if (state == 0L || original == null) return;
            long us = ToUs(Stopwatch.GetTimestamp() - state);
            if (us >= 100000L)
                AddCatastrophic(original.DeclaringType.FullName + "." + original.Name, us);
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("[Diagnostics v0.2 deep attribution]");
            sb.Append("PauseGap: events>=500ms=").Append(gapEvents)
              .Append(", maxGapMs=").Append((maxGapUs / 1000.0).ToString("F2"))
              .Append(", afterGapTicksRemaining=").Append(afterGapTicks).AppendLine();
            sb.Append("IdleCensus: runs=").Append(idleCensusRuns)
              .Append(", observations=").Append(idleObserved)
              .Append(", transitions=").Append(idleTransitions)
              .Append(", ended=").Append(idleEnded)
              .Append(", tracked=").Append(IdleStates.Count)
              .AppendLine();
            sb.AppendLine("CurrentLongestIdle=" + CurrentLongestIdle());
            sb.Append("WorkGiverBurst: slowDeterminesCaptured=").Append(slowDeterminesCaptured)
              .Append(", callsCaptured=").Append(workCallsCaptured)
              .Append(", droppedSlowDetermines=").Append(workCaptureDropped)
              .Append(", burstRemaining=").Append(burstPackagesRemaining).AppendLine();
            sb.AppendLine("TopWorkGiverMethods=" + TopWorkRows());
            sb.AppendLine("PatherStages=" + TopPatherRows());
            sb.Append("ReachCapture: drains=").Append(reachCaptureDrains)
              .Append(", avgDrainUs=").Append(reachCaptureDrains == 0 ? "0.0" : (reachCaptureDrainUs / (double)reachCaptureDrains).ToString("F1"))
              .Append(", maxDrainMs=").Append((reachCaptureDrainMaxUs / 1000.0).ToString("F2"))
              .Append(", RegionAllows calls=").Append(regionAllowsInCapture)
              .Append(", avgUs=").Append(regionAllowsInCapture == 0 ? "0.0" : (regionAllowsCaptureUs / (double)regionAllowsInCapture).ToString("F1"))
              .Append(", >2/5/20ms=").Append(regionAllowsOver2).Append('/').Append(regionAllowsOver5).Append('/').Append(regionAllowsOver20)
              .Append(", maxMs=").Append((regionAllowsCaptureMaxUs / 1000.0).ToString("F2")).AppendLine();
            sb.AppendLine("Catastrophic>=100ms=" + CatastrophicSummary());
            sb.AppendLine("v0.2 failures=" + failures);
            return sb.ToString();
        }

        internal static void Reset()
        {
            IdleStates.Clear(); WorkRows.Clear(); PatherRows.Clear();
            Array.Clear(Catastrophic, 0, Catastrophic.Length);
            lastTickEndStamp = 0L; gapEvents = maxGapUs = 0L; afterGapTicks = 0;
            burstPackagesRemaining = 0; slowDeterminesCaptured = workCallsCaptured = workCaptureDropped = 0L;
            idleCensusRuns = idleObserved = idleTransitions = idleEnded = 0L;
            reachCaptureDrains = reachCaptureDrainUs = reachCaptureDrainMaxUs = 0L;
            regionAllowsInCapture = regionAllowsCaptureUs = regionAllowsCaptureMaxUs = 0L;
            regionAllowsOver2 = regionAllowsOver5 = regionAllowsOver20 = 0L;
            catastrophicPos = failures = 0L;
        }

        private static void CensusIdle(int tick)
        {
            idleCensusRuns++;
            HashSet<int> seen = new HashSet<int>();
            List<Map> maps = Find.Maps;
            if (maps == null) return;
            for (int m = 0; m < maps.Count; m++)
            {
                Map map = maps[m];
                if (map == null || map.mapPawns == null) continue;
                List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
                if (pawns == null) continue;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];
                    if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) continue;
                    int id = pawn.thingIDNumber;
                    seen.Add(id);
                    Job job = null;
                    try { job = pawn.CurJob; } catch { }
                    string jobName = job == null || job.def == null ? "<none>" : job.def.defName;
                    bool idle = IsIdleName(jobName);
                    IdleState state;
                    if (!IdleStates.TryGetValue(id, out state))
                    {
                        if (IdleStates.Count >= MaxIdleStates) EvictIdleState();
                        state = new IdleState { Id = id, PawnName = SafePawnName(pawn), SinceTick = idle ? tick : -1, LastJob = jobName };
                        if (idle) idleTransitions++;
                    }
                    else
                    {
                        bool wasIdle = state.SinceTick >= 0;
                        if (idle && !wasIdle) { state.SinceTick = tick; idleTransitions++; }
                        if (!idle && wasIdle) { state.SinceTick = -1; idleEnded++; }
                        state.LastJob = jobName;
                        state.PawnName = SafePawnName(pawn);
                    }
                    state.LastSeenTick = tick;
                    IdleStates[id] = state;
                    if (idle) idleObserved++;
                }
            }
            if (IdleStates.Count > 0)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, IdleState> kv in IdleStates)
                {
                    if (!seen.Contains(kv.Key) && tick - kv.Value.LastSeenTick > 6000)
                    {
                        if (stale == null) stale = new List<int>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null) for (int i = 0; i < stale.Count; i++) IdleStates.Remove(stale[i]);
            }
        }

        private static void MergeWorkRows(Dictionary<string, WorkRow> rows)
        {
            foreach (KeyValuePair<string, WorkRow> kv in rows)
            {
                WorkRow existing;
                if (!WorkRows.TryGetValue(kv.Key, out existing))
                {
                    if (WorkRows.Count >= MaxWorkRows) continue;
                    existing = new WorkRow(kv.Key);
                }
                existing.Calls += kv.Value.Calls;
                existing.TotalUs += kv.Value.TotalUs;
                if (kv.Value.MaxUs > existing.MaxUs) existing.MaxUs = kv.Value.MaxUs;
                WorkRows[kv.Key] = existing;
            }
        }

        private static string TopWorkRows()
        {
            if (WorkRows.Count == 0) return "none";
            return string.Join("; ", WorkRows.Values.OrderByDescending(r => r.TotalUs).Take(20)
                .Select(r => r.Name + " calls=" + r.Calls + ",totalMs=" + (r.TotalUs / 1000.0).ToString("F2") + ",maxMs=" + (r.MaxUs / 1000.0).ToString("F2")).ToArray());
        }

        private static string TopPatherRows()
        {
            if (PatherRows.Count == 0) return "none";
            return string.Join("; ", PatherRows.Values.OrderByDescending(r => r.TotalUs).Take(16)
                .Select(r => r.Name + " calls=" + r.Calls + ",avgUs=" + (r.Calls == 0 ? 0.0 : r.TotalUs / (double)r.Calls).ToString("F1") +
                    ",>5/20=" + r.Over5 + "/" + r.Over20 + ",maxMs=" + (r.MaxUs / 1000.0).ToString("F2")).ToArray());
        }

        private static string CurrentLongestIdle()
        {
            int now = -1;
            try { now = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; } catch { }
            return string.Join("; ", IdleStates.Values.Where(s => s.SinceTick >= 0)
                .OrderByDescending(s => now >= 0 ? now - s.SinceTick : 0).Take(20)
                .Select(s => s.PawnName + "#" + s.Id + " job=" + s.LastJob + " durTicks=" + (now >= 0 ? Math.Max(0, now - s.SinceTick) : 0)).ToArray());
        }

        private static string CatastrophicSummary()
        {
            int count = (int)Math.Min(catastrophicPos, MaxTailRows);
            if (count <= 0) return "none";
            List<string> rows = new List<string>();
            long start = catastrophicPos > MaxTailRows ? catastrophicPos - MaxTailRows : 0;
            for (long i = start; i < catastrophicPos; i++)
            {
                TailEvent e = Catastrophic[(int)(i % MaxTailRows)];
                if (e.Name != null) rows.Add("tick=" + e.Tick + "," + e.Name + "=" + (e.Us / 1000.0).ToString("F2") + "ms");
            }
            return string.Join("; ", rows.ToArray());
        }

        private static void AddCatastrophic(string name, long us)
        {
            int tick = -1;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; } catch { }
            long pos = catastrophicPos++;
            Catastrophic[(int)(pos % MaxTailRows)] = new TailEvent { Tick = tick, Name = name, Us = us };
        }

        private static void EvictIdleState()
        {
            if (IdleStates.Count == 0) return;
            int key = IdleStates.OrderBy(kv => kv.Value.LastSeenTick).First().Key;
            IdleStates.Remove(key);
        }

        private static bool IsIdleName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "<none>") return false;
            return name.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafePawnName(Pawn pawn)
        {
            try { if (pawn.Name != null) return pawn.Name.ToStringShort; } catch { }
            return pawn.def == null ? "Pawn" : pawn.def.defName;
        }

        private static long ToUs(long ticks)
        {
            if (ticks <= 0L) return 0L;
            return (long)(ticks * 1000000.0 / Stopwatch.Frequency);
        }

        private struct IdleState
        {
            internal int Id;
            internal string PawnName;
            internal int SinceTick;
            internal int LastSeenTick;
            internal string LastJob;
        }

        internal struct WorkRow
        {
            internal string Name;
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
            internal WorkRow(string name) { Name = name; Calls = TotalUs = MaxUs = 0L; }
        }

        private struct StageRow
        {
            internal string Name;
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
            internal long Over5;
            internal long Over20;
            internal StageRow(string name) { Name = name; Calls = TotalUs = MaxUs = Over5 = Over20 = 0L; }
        }

        private struct TailEvent
        {
            internal int Tick;
            internal string Name;
            internal long Us;
        }
    }

    [StaticConstructorOnStartup]
    internal static class DiagnosticsV02Patches
    {
        private static int patched;
        private static int missing;

        static DiagnosticsV02Patches()
        {
            try
            {
                LongEventHandler.ExecuteWhenFinished(InstallLate);
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT Diagnostics] v0.2 late patch scheduling failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InstallLate()
        {
            Harmony harmony = new Harmony(DiagnosticsBootstrap.HarmonyId);
            PatchPatherStages(harmony);
            PatchWorkGivers(harmony);
            PatchReachCapture(harmony);
            PatchCatastrophic(harmony);
            Log.Message("[RimMT Diagnostics] v0.2 deep patches installed=" + patched + ", missing=" + missing + ".");
        }

        private static void PatchPatherStages(Harmony harmony)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal)
            {
                "TryEnterNextPathCell", "SetupMoveIntoNextCell", "NeedNewPath", "TrySetNewPath",
                "StartPath", "StopDead", "ResetToCurrentPosition"
            };
            List<MethodInfo> methods = AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower));
            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo m = methods[i];
                if (m != null && names.Contains(m.Name))
                    TryPatch(harmony, m, nameof(PatherPrefix), nameof(PatherPostfix));
            }
        }

        private static void PatchWorkGivers(Harmony harmony)
        {
            HashSet<Type> types = new HashSet<Type>();
            try
            {
                List<WorkGiverDef> defs = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    WorkGiverDef def = defs[i];
                    if (def != null && def.giverClass != null && typeof(WorkGiver).IsAssignableFrom(def.giverClass)) types.Add(def.giverClass);
                }
            }
            catch { }
            string[] names = { "ShouldSkip", "NonScanJob", "PotentialWorkThingsGlobal", "HasJobOnThing", "JobOnThing" };
            foreach (Type t in types)
            {
                List<MethodInfo> methods;
                try { methods = AccessTools.GetDeclaredMethods(t); } catch { continue; }
                for (int i = 0; i < methods.Count; i++)
                {
                    MethodInfo m = methods[i];
                    if (m == null || Array.IndexOf(names, m.Name) < 0) continue;
                    TryPatch(harmony, m, nameof(WorkPrefix), nameof(WorkPostfix));
                }
            }
        }

        private static void PatchReachCapture(Harmony harmony)
        {
            Type rimmt = AccessTools.TypeByName("RimMT.AggressiveReachabilityProfilesV17");
            if (rimmt != null)
            {
                MethodInfo drain = AccessTools.Method(rimmt, "ProfileCaptureDrainPostfix");
                if (drain != null) TryPatch(harmony, drain, nameof(ReachCapturePrefix), nameof(ReachCapturePostfix));
                else missing++;
            }
            else missing++;

            MethodInfo allows = AccessTools.Method(typeof(Region), "Allows", new Type[] { typeof(TraverseParms), typeof(bool) });
            if (allows != null) TryPatch(harmony, allows, nameof(RegionAllowsPrefix), nameof(RegionAllowsPostfix));
            else missing++;
        }

        private static void PatchCatastrophic(Harmony harmony)
        {
            Type hierarchy = AccessTools.TypeByName("VFEEmpire.WorldComponent_Hierarchy");
            if (hierarchy != null)
            {
                MethodInfo tick = AccessTools.Method(hierarchy, "WorldComponentTick");
                if (tick != null) TryPatch(harmony, tick, nameof(CatastrophicPrefix), nameof(CatastrophicPostfix));
            }
        }

        private static void TryPatch(Harmony harmony, MethodBase method, string prefix, string postfix)
        {
            if (method == null) { missing++; return; }
            try
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(DiagnosticsV02Patches), prefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(DiagnosticsV02Patches), postfix) { priority = Priority.Last });
                patched++;
            }
            catch { missing++; }
        }

        public static void PatherPrefix(MethodBase __originalMethod, ref long __state) { __state = DiagnosticsV02.PatherStagePrefix(__originalMethod); }
        public static void PatherPostfix(MethodBase __originalMethod, long __state) { DiagnosticsV02.PatherStagePostfix(__originalMethod, __state); }
        public static void WorkPrefix(object __instance, MethodBase __originalMethod, ref long __state) { __state = DiagnosticsV02.WorkPrefix(__instance, __originalMethod); }
        public static void WorkPostfix(object __instance, MethodBase __originalMethod, long __state) { DiagnosticsV02.WorkPostfix(__instance, __originalMethod, __state); }
        public static void ReachCapturePrefix() { DiagnosticsV02.ReachCaptureBegin(); }
        public static void ReachCapturePostfix() { DiagnosticsV02.ReachCaptureEnd(); }
        public static void RegionAllowsPrefix(ref long __state) { __state = DiagnosticsV02.RegionAllowsPrefix(); }
        public static void RegionAllowsPostfix(long __state) { DiagnosticsV02.RegionAllowsPostfix(__state); }
        public static void CatastrophicPrefix(ref long __state) { __state = DiagnosticsV02.CatastrophicPrefix(); }
        public static void CatastrophicPostfix(MethodBase __originalMethod, long __state) { DiagnosticsV02.CatastrophicPostfix(__originalMethod, __state); }
    }
}
