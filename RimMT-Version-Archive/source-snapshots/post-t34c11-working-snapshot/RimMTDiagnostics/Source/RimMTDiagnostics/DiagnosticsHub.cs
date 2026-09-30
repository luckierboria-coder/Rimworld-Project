using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    internal enum DiagPhase
    {
        PawnTick,
        JobTracker,
        DetermineNextJob,
        Pather,
        GenClosest,
        Reachability,
        MapPostTick,
        WorldTick,
        Storyteller,
        Count
    }

    internal static class DiagnosticsHub
    {
        private const int HistogramBuckets = 251; // 0..249 ms, 250 => >=250ms
        private const int RecentTailCapacity = 32;
        private const int MaxPawnStates = 512;
        private const int MaxSourceNames = 64;

        private static readonly long[] TickHistogram = new long[HistogramBuckets];
        private static readonly PhaseStats[] Phases = CreatePhaseStats();
        private static readonly TailFrame[] RecentTails = new TailFrame[RecentTailCapacity];
        private static readonly Dictionary<int, WaitState> WaitStates = new Dictionary<int, WaitState>();
        private static readonly Dictionary<string, long> WaitSources = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly FieldInfo JobTrackerPawnField = AccessToolsCompat.Field(typeof(Pawn_JobTracker), "pawn");

        [ThreadStatic] private static bool deepActive;
        [ThreadStatic] private static long tickStart;
        [ThreadStatic] private static int currentTick;
        [ThreadStatic] private static Pawn topPawn;
        [ThreadStatic] private static long topPawnUs;

        private static long ticks;
        private static long tickTotalUs;
        private static long tickMaxUs;
        private static long tickOver20;
        private static long tickOver50;
        private static long tickOver100;
        private static int burstTicksRemaining;
        private static int recentTailPos;
        private static int recentTailCount;

        private static long determineObserved;
        private static long playerHumanlikeDetermine;
        private static long currentIdleDetermine;
        private static long resultIdle;
        private static long resultNonIdle;
        private static long resultNoJob;
        private static long currentIdleResultIdle;
        private static long currentIdleResultNonIdle;
        private static long currentIdleResultNoJob;
        private static long waitStateEvictions;
        private static long failures;

        internal static bool DeepActive { get { return deepActive; } }

        internal static void BeginTick()
        {
            tickStart = Stopwatch.GetTimestamp();
            try { currentTick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { currentTick = -1; }

            int cadence = RimMTDiagnosticsSettings.SampleEveryTicks;
            bool periodic = cadence <= 1 || (currentTick >= 0 && currentTick % cadence == 0);
            bool burst = burstTicksRemaining > 0;
            if (burstTicksRemaining > 0) burstTicksRemaining--;
            deepActive = periodic || burst;
            topPawn = null;
            topPawnUs = 0L;
        }

        internal static void EndTick()
        {
            long start = tickStart;
            tickStart = 0L;
            long us = ElapsedUs(start);
            if (us <= 0L)
            {
                deepActive = false;
                return;
            }

            ticks++;
            tickTotalUs += us;
            if (us > tickMaxUs) tickMaxUs = us;
            if (us >= 20000L) tickOver20++;
            if (us >= 50000L) tickOver50++;
            if (us >= 100000L) tickOver100++;
            int msBucket = (int)(us / 1000L);
            if (msBucket >= HistogramBuckets - 1) msBucket = HistogramBuckets - 1;
            if (msBucket < 0) msBucket = 0;
            TickHistogram[msBucket]++;

            int thresholdUs = RimMTDiagnosticsSettings.TailThresholdMs * 1000;
            if (us >= thresholdUs)
            {
                if (us >= 50000L && burstTicksRemaining < RimMTDiagnosticsSettings.PostSpikeBurstTicks)
                    burstTicksRemaining = RimMTDiagnosticsSettings.PostSpikeBurstTicks;
                RecentTails[recentTailPos] = new TailFrame(currentTick, us, topPawn, topPawnUs);
                recentTailPos = (recentTailPos + 1) % RecentTailCapacity;
                if (recentTailCount < RecentTailCapacity) recentTailCount++;
            }
            deepActive = false;
        }

        internal static long BeginPhase()
        {
            if (!deepActive) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndPhase(long started, DiagPhase phase)
        {
            if (started == 0L) return;
            long us = ElapsedUs(started);
            if (us <= 0L) return;
            PhaseStats stat = Phases[(int)phase];
            stat.Calls++;
            stat.TotalUs += us;
            if (us > stat.MaxUs) stat.MaxUs = us;
            if (us >= 5000L) stat.Over5++;
            if (us >= 20000L) stat.Over20++;
            if (us >= 50000L) stat.Over50++;
        }

        internal static void EndPawn(long started, Pawn pawn)
        {
            if (started == 0L) return;
            long us = ElapsedUs(started);
            if (us <= 0L) return;
            PhaseStats stat = Phases[(int)DiagPhase.PawnTick];
            stat.Calls++;
            stat.TotalUs += us;
            if (us > stat.MaxUs) stat.MaxUs = us;
            if (us >= 5000L) stat.Over5++;
            if (us >= 20000L) stat.Over20++;
            if (us >= 50000L) stat.Over50++;
            if (us > topPawnUs)
            {
                topPawnUs = us;
                topPawn = pawn;
            }
        }

        internal static void ObserveDetermine(Pawn_JobTracker tracker, ThinkResult result)
        {
            if (!RimMTDiagnosticsSettings.EnableWaitTrace) return;
            determineObserved++;
            if (tracker == null || JobTrackerPawnField == null) return;

            Pawn pawn;
            try { pawn = JobTrackerPawnField.GetValue(tracker) as Pawn; }
            catch { failures++; return; }
            if (pawn == null || pawn.Destroyed || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.Faction != Faction.OfPlayer)
                return;

            playerHumanlikeDetermine++;
            Job current = null;
            try { current = pawn.CurJob; }
            catch { }
            bool currentIdle = IsIdle(current);
            if (currentIdle) currentIdleDetermine++;

            Job next = null;
            ThinkNode source = null;
            try
            {
                next = result.Job;
                source = result.SourceNode;
            }
            catch { }

            string sourceName = source == null ? "<null>" : source.GetType().FullName;
            if (string.IsNullOrEmpty(sourceName)) sourceName = "<unnamed>";
            Outcome outcome;
            if (next == null)
            {
                resultNoJob++;
                if (currentIdle) currentIdleResultNoJob++;
                outcome = Outcome.NoJob;
            }
            else if (IsIdle(next))
            {
                resultIdle++;
                if (currentIdle) currentIdleResultIdle++;
                outcome = Outcome.Idle;
                IncrementSource(sourceName);
            }
            else
            {
                resultNonIdle++;
                if (currentIdle) currentIdleResultNonIdle++;
                outcome = Outcome.NonIdle;
            }

            int tick = -1;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { }
            UpdateWaitState(pawn, currentIdle, outcome, sourceName, tick);
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(8192);
            long count = ticks;
            double avgMs = count == 0 ? 0.0 : tickTotalUs / 1000.0 / count;
            sb.AppendLine("[RimMT Diagnostics v0.1]");
            sb.Append("TickTail: samples=").Append(count)
              .Append(", avgMs=").Append(avgMs.ToString("F3"))
              .Append(", P50/P95/P99/P99.9=").Append(Percentile(0.50).ToString("F1")).Append('/')
              .Append(Percentile(0.95).ToString("F1")).Append('/')
              .Append(Percentile(0.99).ToString("F1")).Append('/')
              .Append(Percentile(0.999).ToString("F1"))
              .Append(", >20/50/100=").Append(tickOver20).Append('/').Append(tickOver50).Append('/').Append(tickOver100)
              .Append(", maxMs=").Append((tickMaxUs / 1000.0).ToString("F2")).AppendLine();

            for (int i = 0; i < (int)DiagPhase.Count; i++)
            {
                PhaseStats s = Phases[i];
                sb.Append("Phase ").Append(((DiagPhase)i).ToString()).Append(": calls=").Append(s.Calls)
                  .Append(", avgUs=").Append(s.Calls == 0 ? "0.0" : (s.TotalUs / (double)s.Calls).ToString("F1"))
                  .Append(", >5/20/50ms=").Append(s.Over5).Append('/').Append(s.Over20).Append('/').Append(s.Over50)
                  .Append(", maxMs=").Append((s.MaxUs / 1000.0).ToString("F2")).AppendLine();
            }

            sb.Append("WaitTrace: determineObserved=").Append(determineObserved)
              .Append(", playerHumanlike=").Append(playerHumanlikeDetermine)
              .Append(", currentIdleCalls=").Append(currentIdleDetermine)
              .Append(", result[idle/nonIdle/noJob]=").Append(resultIdle).Append('/').Append(resultNonIdle).Append('/').Append(resultNoJob)
              .Append(", currentIdle->result[idle/nonIdle/noJob]=").Append(currentIdleResultIdle).Append('/').Append(currentIdleResultNonIdle).Append('/').Append(currentIdleResultNoJob)
              .Append(", stateEvictions=").Append(waitStateEvictions).Append(", failures=").Append(failures).AppendLine();

            sb.AppendLine("TopWaitSources=" + TopWaitSources());
            sb.AppendLine("LongestIdle=" + LongestIdle());
            sb.AppendLine("RecentTailTicks=" + RecentTailSummary());
            return sb.ToString();
        }

        internal static void Reset()
        {
            Array.Clear(TickHistogram, 0, TickHistogram.Length);
            for (int i = 0; i < Phases.Length; i++) Phases[i].Reset();
            Array.Clear(RecentTails, 0, RecentTails.Length);
            WaitStates.Clear();
            WaitSources.Clear();
            ticks = tickTotalUs = tickMaxUs = tickOver20 = tickOver50 = tickOver100 = 0L;
            determineObserved = playerHumanlikeDetermine = currentIdleDetermine = 0L;
            resultIdle = resultNonIdle = resultNoJob = 0L;
            currentIdleResultIdle = currentIdleResultNonIdle = currentIdleResultNoJob = 0L;
            waitStateEvictions = failures = 0L;
            recentTailPos = recentTailCount = burstTicksRemaining = 0;
        }

        private static void UpdateWaitState(Pawn pawn, bool currentIdle, Outcome outcome, string source, int tick)
        {
            int id = pawn.thingIDNumber;
            WaitState state;
            if (!WaitStates.TryGetValue(id, out state))
            {
                if (WaitStates.Count >= MaxPawnStates)
                {
                    int remove = WaitStates.Keys.FirstOrDefault();
                    WaitStates.Remove(remove);
                    waitStateEvictions++;
                }
                state = new WaitState { PawnId = id, PawnDef = pawn.def == null ? "Pawn" : pawn.def.defName, IdleSinceTick = currentIdle ? tick : -1 };
            }
            if (!currentIdle)
            {
                state.IdleSinceTick = -1;
                state.ConsecutiveIdleResults = 0;
                state.NoJobWhileIdle = 0;
            }
            else
            {
                if (state.IdleSinceTick < 0) state.IdleSinceTick = tick;
                if (outcome == Outcome.Idle) state.ConsecutiveIdleResults++;
                if (outcome == Outcome.NoJob) state.NoJobWhileIdle++;
            }
            state.LastTick = tick;
            state.LastOutcome = outcome;
            state.LastSource = source;
            WaitStates[id] = state;
        }

        private static bool IsIdle(Job job)
        {
            if (job == null || job.def == null) return false;
            string n = job.def.defName;
            if (string.IsNullOrEmpty(n)) return false;
            return n == "Wait" || n == "Wait_MaintainPosture" || n.StartsWith("Wait_", StringComparison.Ordinal);
        }

        private static void IncrementSource(string source)
        {
            long value;
            if (WaitSources.TryGetValue(source, out value)) WaitSources[source] = value + 1L;
            else if (WaitSources.Count < MaxSourceNames) WaitSources[source] = 1L;
            else WaitSources["<other>"] = WaitSources.ContainsKey("<other>") ? WaitSources["<other>"] + 1L : 1L;
        }

        private static string TopWaitSources()
        {
            if (WaitSources.Count == 0) return "none";
            return string.Join("; ", WaitSources.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key + "=" + kv.Value));
        }

        private static string LongestIdle()
        {
            int now = -1;
            try { now = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { }
            var rows = WaitStates.Values.Where(s => s.IdleSinceTick >= 0)
                .OrderByDescending(s => now >= 0 && s.IdleSinceTick >= 0 ? now - s.IdleSinceTick : 0)
                .Take(12)
                .Select(s => s.PawnDef + "#" + s.PawnId + " durTicks=" + (now >= 0 ? Math.Max(0, now - s.IdleSinceTick) : 0) +
                    " idleResults=" + s.ConsecutiveIdleResults + " noJob=" + s.NoJobWhileIdle +
                    " last=" + s.LastOutcome + " source=" + (s.LastSource ?? "<none>"));
            return string.Join("; ", rows.ToArray());
        }

        private static string RecentTailSummary()
        {
            if (recentTailCount == 0) return "none";
            List<string> rows = new List<string>();
            int start = recentTailCount == RecentTailCapacity ? recentTailPos : 0;
            for (int i = 0; i < recentTailCount; i++)
            {
                TailFrame e = RecentTails[(start + i) % RecentTailCapacity];
                string pawn = e.TopPawn == null ? "none" : (e.TopPawn.def == null ? "Pawn" : e.TopPawn.def.defName) + "#" + e.TopPawn.thingIDNumber;
                rows.Add("tick=" + e.Tick + ",total=" + (e.TotalUs / 1000.0).ToString("F2") + "ms,topPawn=" + pawn + ":" + (e.TopPawnUs / 1000.0).ToString("F2") + "ms");
            }
            return string.Join("; ", rows.ToArray());
        }

        private static double Percentile(double p)
        {
            long total = ticks;
            if (total <= 0) return 0.0;
            long target = (long)Math.Ceiling(total * p);
            long accum = 0;
            for (int i = 0; i < TickHistogram.Length; i++)
            {
                accum += TickHistogram[i];
                if (accum >= target) return i;
            }
            return TickHistogram.Length - 1;
        }

        private static long ElapsedUs(long started)
        {
            if (started == 0L) return 0L;
            long dt = Stopwatch.GetTimestamp() - started;
            return dt <= 0 ? 0L : (long)(dt * (1000000.0 / Stopwatch.Frequency));
        }

        private static PhaseStats[] CreatePhaseStats()
        {
            PhaseStats[] arr = new PhaseStats[(int)DiagPhase.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = new PhaseStats();
            return arr;
        }

        private sealed class PhaseStats
        {
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
            internal long Over5;
            internal long Over20;
            internal long Over50;
            internal void Reset() { Calls = TotalUs = MaxUs = Over5 = Over20 = Over50 = 0L; }
        }

        private struct TailFrame
        {
            internal readonly int Tick;
            internal readonly long TotalUs;
            internal readonly Pawn TopPawn;
            internal readonly long TopPawnUs;
            internal TailFrame(int tick, long totalUs, Pawn topPawn, long topPawnUs)
            { Tick = tick; TotalUs = totalUs; TopPawn = topPawn; TopPawnUs = topPawnUs; }
        }

        private struct WaitState
        {
            internal int PawnId;
            internal string PawnDef;
            internal int IdleSinceTick;
            internal int LastTick;
            internal long ConsecutiveIdleResults;
            internal long NoJobWhileIdle;
            internal Outcome LastOutcome;
            internal string LastSource;
        }

        private enum Outcome { NoJob, Idle, NonIdle }
    }

    internal static class AccessToolsCompat
    {
        internal static FieldInfo Field(Type type, string name)
        {
            try { return HarmonyLib.AccessTools.Field(type, name); }
            catch { return null; }
        }
    }
}
