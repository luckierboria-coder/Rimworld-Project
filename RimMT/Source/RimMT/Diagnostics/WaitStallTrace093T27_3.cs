using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T27.3 low-duty-cycle correctness telemetry for repeated idle/Wait outcomes.
    /// Reuses the already-installed T2 DetermineNextJob postfix; installs no Harmony patches.
    /// It never re-runs a think tree and never mutates jobs, pawn state, reservations or priorities.
    /// </summary>
    internal static class WaitStallTrace093T27_3
    {
        private const int MaxRecentSources = 24;
        private const int MaxPawnStates = 256;

        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
        private static readonly Dictionary<int, PawnState> PawnStates = new Dictionary<int, PawnState>();
        private static readonly Dictionary<string, long> WaitSources = new Dictionary<string, long>(StringComparer.Ordinal);

        private static long observed;
        private static long playerHumanlike;
        private static long currentIdleCalls;
        private static long resultIdle;
        private static long resultNonIdle;
        private static long resultNoJob;
        private static long currentIdleResultIdle;
        private static long currentIdleResultNonIdle;
        private static long currentIdleResultNoJob;
        private static long stateEvictions;
        private static long failures;

        internal static void Observe(Pawn_JobTracker tracker, ThinkResult result)
        {
            observed++;
            if (tracker == null || PawnField == null || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            Pawn pawn;
            try { pawn = PawnField.GetValue(tracker) as Pawn; }
            catch { failures++; return; }
            if (pawn == null || pawn.Destroyed || pawn.Faction != Faction.OfPlayer || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
                return;

            playerHumanlike++;
            int tick = -1;
            try { if (Find.TickManager != null) tick = Find.TickManager.TicksGame; }
            catch { }

            Job currentJob = null;
            try { currentJob = pawn.CurJob; }
            catch { }
            bool currentIdle = IsIdle(currentJob);
            if (currentIdle) currentIdleCalls++;

            Job next = null;
            ThinkNode source = null;
            try
            {
                next = result.Job;
                source = result.SourceNode;
            }
            catch { }

            Outcome outcome;
            if (next == null)
            {
                outcome = Outcome.NoJob;
                resultNoJob++;
                if (currentIdle) currentIdleResultNoJob++;
            }
            else if (IsIdle(next))
            {
                outcome = Outcome.Idle;
                resultIdle++;
                if (currentIdle) currentIdleResultIdle++;
                CountSource(SourceName(source, next));
            }
            else
            {
                outcome = Outcome.NonIdle;
                resultNonIdle++;
                if (currentIdle) currentIdleResultNonIdle++;
            }

            PawnState state;
            if (!PawnStates.TryGetValue(pawn.thingIDNumber, out state))
            {
                if (PawnStates.Count >= MaxPawnStates) EvictOldest();
                state = new PawnState(pawn);
                PawnStates[pawn.thingIDNumber] = state;
            }

            state.LastTick = tick;
            state.LastCurrent = currentJob == null || currentJob.def == null ? "none" : currentJob.def.defName;
            state.LastResult = next == null || next.def == null ? "NoJob" : next.def.defName;
            state.LastSource = SourceName(source, next);
            state.DetermineCalls++;

            if (outcome == Outcome.Idle || (outcome == Outcome.NoJob && currentIdle))
            {
                if (!state.ActiveIdle)
                {
                    state.ActiveIdle = true;
                    state.IdleSinceTick = tick;
                    state.IdleSelections = 0;
                    state.NoJobWhileIdle = 0;
                }
                if (outcome == Outcome.Idle) state.IdleSelections++;
                else state.NoJobWhileIdle++;
            }
            else if (outcome == Outcome.NonIdle)
            {
                state.ActiveIdle = false;
                state.IdleSinceTick = -1;
                state.IdleSelections = 0;
                state.NoJobWhileIdle = 0;
            }
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T27.3 Wait-stall trace: observed=").Append(observed)
                .Append(", playerHumanlike=").Append(playerHumanlike)
                .Append(", currentIdleCalls=").Append(currentIdleCalls)
                .Append(", result[idle/nonIdle/noJob]=").Append(resultIdle).Append('/').Append(resultNonIdle).Append('/').Append(resultNoJob)
                .Append(", currentIdle->result[idle/nonIdle/noJob]=").Append(currentIdleResultIdle).Append('/').Append(currentIdleResultNonIdle).Append('/').Append(currentIdleResultNoJob)
                .Append(", trackedPawns=").Append(PawnStates.Count)
                .Append(", evictions=").Append(stateEvictions)
                .Append(", failures=").Append(failures)
                .Append(". TopWaitSources=");

            List<KeyValuePair<string, long>> sources = new List<KeyValuePair<string, long>>(WaitSources);
            sources.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            int sourceTake = Math.Min(10, sources.Count);
            if (sourceTake == 0) sb.Append("<none>");
            for (int i = 0; i < sourceTake; i++)
            {
                if (i != 0) sb.Append(';');
                sb.Append(sources[i].Key).Append('=').Append(sources[i].Value);
            }

            int now = -1;
            try { if (Find.TickManager != null) now = Find.TickManager.TicksGame; }
            catch { }
            List<PawnState> active = new List<PawnState>();
            foreach (PawnState state in PawnStates.Values)
                if (state != null && state.ActiveIdle) active.Add(state);
            active.Sort(delegate(PawnState a, PawnState b)
            {
                int ad = Duration(now, a.IdleSinceTick);
                int bd = Duration(now, b.IdleSinceTick);
                int cmp = bd.CompareTo(ad);
                if (cmp != 0) return cmp;
                return b.IdleSelections.CompareTo(a.IdleSelections);
            });

            sb.Append(". ActiveLongest=");
            int take = Math.Min(12, active.Count);
            if (take == 0) sb.Append("<none>");
            for (int i = 0; i < take; i++)
            {
                if (i != 0) sb.Append(';');
                PawnState s = active[i];
                sb.Append(PawnText(s.Pawn)).Append("{durTicks=").Append(Duration(now, s.IdleSinceTick))
                    .Append(",idleResults=").Append(s.IdleSelections)
                    .Append(",noJobWhileIdle=").Append(s.NoJobWhileIdle)
                    .Append(",lastCurrent=").Append(s.LastCurrent)
                    .Append(",lastResult=").Append(s.LastResult)
                    .Append(",source=").Append(s.LastSource).Append('}');
            }
            return sb.ToString();
        }

        private static bool IsIdle(Job job)
        {
            if (job == null || job.def == null) return false;
            if (job.def == JobDefOf.Wait) return true;
            if (job.def.isIdle) return true;
            string name = job.def.defName;
            return name != null && name.StartsWith("Wait", StringComparison.Ordinal);
        }

        private static string SourceName(ThinkNode source, Job job)
        {
            string node = source == null ? "<nullNode>" : source.GetType().FullName;
            string def = job == null || job.def == null ? "NoJob" : job.def.defName;
            return node + "->" + def;
        }

        private static void CountSource(string source)
        {
            if (string.IsNullOrEmpty(source)) source = "<unknown>";
            long count;
            WaitSources.TryGetValue(source, out count);
            WaitSources[source] = count + 1;
            if (WaitSources.Count <= MaxRecentSources) return;

            string minKey = null;
            long min = long.MaxValue;
            foreach (KeyValuePair<string, long> pair in WaitSources)
            {
                if (pair.Value < min)
                {
                    min = pair.Value;
                    minKey = pair.Key;
                }
            }
            if (minKey != null) WaitSources.Remove(minKey);
        }

        private static void EvictOldest()
        {
            int key = -1;
            int oldest = int.MaxValue;
            foreach (KeyValuePair<int, PawnState> pair in PawnStates)
            {
                int tick = pair.Value == null ? int.MinValue : pair.Value.LastTick;
                if (tick < oldest)
                {
                    oldest = tick;
                    key = pair.Key;
                }
            }
            if (key >= 0)
            {
                PawnStates.Remove(key);
                stateEvictions++;
            }
        }

        private static int Duration(int now, int since)
        {
            if (now < 0 || since < 0 || now < since) return 0;
            return now - since;
        }

        private static string PawnText(Pawn pawn)
        {
            if (pawn == null) return "Pawn#?";
            string def = pawn.def == null ? "Pawn" : pawn.def.defName;
            return def + "#" + pawn.thingIDNumber;
        }

        private enum Outcome
        {
            NoJob,
            Idle,
            NonIdle
        }

        private sealed class PawnState
        {
            internal readonly Pawn Pawn;
            internal int LastTick = -1;
            internal int IdleSinceTick = -1;
            internal int IdleSelections;
            internal int NoJobWhileIdle;
            internal int DetermineCalls;
            internal bool ActiveIdle;
            internal string LastCurrent = "none";
            internal string LastResult = "NoJob";
            internal string LastSource = "<none>";

            internal PawnState(Pawn pawn) { Pawn = pawn; }
        }
    }
}
