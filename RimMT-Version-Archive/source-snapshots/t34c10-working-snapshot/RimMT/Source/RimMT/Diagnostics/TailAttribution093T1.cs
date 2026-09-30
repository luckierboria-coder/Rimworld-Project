using System;
using System.Diagnostics;
using System.Text;

namespace RimMT
{
    internal enum TailPhase093T1
    {
        TickListNormal = 0,
        TickListRare = 1,
        TickListLong = 2,
        TickListExtra = 3,
        MapPreTick = 4,
        WorldTick = 5,
        StoryWatcher = 6,
        GameEnd = 7,
        Storyteller = 8,
        Tales = 9,
        WorldPostTick = 10,
        MapPostTick = 11,
        History = 12,
        GameComponents = 13,
        Autosaver = 14,
        Scenario = 15,
        DateNotifier = 16,
        Letters = 17,
        Filth = 18,
        Count = 19
    }

    /// <summary>
    /// Top-level attribution only. Hot-path state is fixed-size and main-thread owned.
    /// No allocations, reflection or sorting occur during a game tick.
    /// </summary>
    internal static class TailAttribution093T1
    {
        private const int PhaseCount = (int)TailPhase093T1.Count;
        private const int RecentCapacity = 16;

        private static readonly string[] PhaseNames = new string[]
        {
            "TickListNormal", "TickListRare", "TickListLong", "TickListExtra",
            "MapPreTick", "WorldTick", "StoryWatcher", "GameEnd", "Storyteller",
            "Tales", "WorldPostTick", "MapPostTick", "History", "GameComponents",
            "Autosaver", "Scenario", "DateNotifier", "Letters", "Filth"
        };

        private static readonly long[] Calls = new long[PhaseCount];
        private static readonly long[] TotalUs = new long[PhaseCount];
        private static readonly long[] MaxUs = new long[PhaseCount];
        private static readonly long[] Over5 = new long[PhaseCount];
        private static readonly long[] Over10 = new long[PhaseCount];
        private static readonly long[] Over20 = new long[PhaseCount];
        private static readonly long[] Over50 = new long[PhaseCount];
        private static readonly long[] CurrentPhaseUs = new long[PhaseCount];
        private static readonly TailFrame[] RecentSevere = new TailFrame[RecentCapacity];

        [ThreadStatic] private static bool activeTick;
        [ThreadStatic] private static int tickListOrdinal;
        [ThreadStatic] private static int gc0Start;
        [ThreadStatic] private static int gc1Start;
        [ThreadStatic] private static int gc2Start;

        private static int recentPos;
        private static int recentCount;
        private static long tail20;
        private static long tail50;
        private static long tail50WithGc0;
        private static long tail50WithGc1;
        private static long tail50WithGc2;
        private static long maxUnattributedUs;

        internal static void BeginTick()
        {
            activeTick = true;
            tickListOrdinal = 0;
            Array.Clear(CurrentPhaseUs, 0, CurrentPhaseUs.Length);
            gc0Start = GC.CollectionCount(0);
            gc1Start = GC.CollectionCount(1);
            gc2Start = GC.CollectionCount(2);
        }

        internal static long BeginPhase()
        {
            if (!activeTick || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndPhase(long started, TailPhase093T1 phase)
        {
            if (started == 0L || !activeTick) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = TicksToUs(elapsed);
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;
            if (phase == TailPhase093T1.Storyteller && us >= 100000L)
                StorytellerCatastrophic093T15.Observe(us);

            Calls[p]++;
            TotalUs[p] += us;
            CurrentPhaseUs[p] += us;
            if (us > MaxUs[p]) MaxUs[p] = us;
            if (us >= 5000L) Over5[p]++;
            if (us >= 10000L) Over10[p]++;
            if (us >= 20000L) Over20[p]++;
            if (us >= 50000L) Over50[p]++;
        }

        internal static void EndTickList(long started)
        {
            int ordinal = tickListOrdinal++;
            TailPhase093T1 phase = ordinal == 0 ? TailPhase093T1.TickListNormal :
                ordinal == 1 ? TailPhase093T1.TickListRare :
                ordinal == 2 ? TailPhase093T1.TickListLong : TailPhase093T1.TickListExtra;
            EndPhase(started, phase);
        }

        internal static void EndTick(long totalUs)
        {
            if (!activeTick) return;

            int gc0 = Math.Max(0, GC.CollectionCount(0) - gc0Start);
            int gc1 = Math.Max(0, GC.CollectionCount(1) - gc1Start);
            int gc2 = Math.Max(0, GC.CollectionCount(2) - gc2Start);
            activeTick = false;

            if (totalUs >= 20000L) tail20++;
            if (totalUs < 50000L) return;

            tail50++;
            if (gc0 > 0) tail50WithGc0++;
            if (gc1 > 0) tail50WithGc1++;
            if (gc2 > 0) tail50WithGc2++;

            long phaseSum = 0L;
            int top1 = -1, top2 = -1, top3 = -1;
            long top1Us = 0L, top2Us = 0L, top3Us = 0L;
            for (int i = 0; i < PhaseCount; i++)
            {
                long value = CurrentPhaseUs[i];
                phaseSum += value;
                if (value > top1Us)
                {
                    top3 = top2; top3Us = top2Us;
                    top2 = top1; top2Us = top1Us;
                    top1 = i; top1Us = value;
                }
                else if (value > top2Us)
                {
                    top3 = top2; top3Us = top2Us;
                    top2 = i; top2Us = value;
                }
                else if (value > top3Us)
                {
                    top3 = i; top3Us = value;
                }
            }

            long unattributed = totalUs - phaseSum;
            if (unattributed < 0L) unattributed = 0L;
            if (unattributed > maxUnattributedUs) maxUnattributedUs = unattributed;

            int gameTick = -1;
            try
            {
                if (Verse.Find.TickManager != null) gameTick = Verse.Find.TickManager.TicksGame;
            }
            catch { }

            RecentSevere[recentPos] = new TailFrame(
                RimMTRuntime.MainThreadFrames, gameTick, totalUs, phaseSum, unattributed,
                top1, top1Us, top2, top2Us, top3, top3Us, gc0, gc1, gc2);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T1 top-level attribution: tail20=").Append(tail20)
                .Append(", tail50=").Append(tail50)
                .Append(", tail50WithGC[0/1/2]=").Append(tail50WithGc0).Append('/')
                .Append(tail50WithGc1).Append('/').Append(tail50WithGc2)
                .Append(", maxUnattributedMs=").Append((maxUnattributedUs / 1000.0).ToString("F2"))
                .Append(". Phase stats: ");

            for (int i = 0; i < PhaseCount; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = Calls[i];
                double avgUs = calls == 0L ? 0.0 : TotalUs[i] / (double)calls;
                sb.Append(PhaseNames[i]).Append("(calls=").Append(calls)
                    .Append(",avgUs=").Append(avgUs.ToString("F1"))
                    .Append(",>5/10/20/50=").Append(Over5[i]).Append('/')
                    .Append(Over10[i]).Append('/').Append(Over20[i]).Append('/').Append(Over50[i])
                    .Append(",maxMs=").Append((MaxUs[i] / 1000.0).ToString("F2")).Append(')');
            }
            return sb.ToString();
        }

        internal static string RecentSevereSummary()
        {
            if (recentCount <= 0) return "T1 recent >=50ms tails: none.";
            StringBuilder sb = new StringBuilder(3072);
            sb.Append("T1 recent >=50ms tails (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                TailFrame e = RecentSevere[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",total=").Append((e.TotalUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",covered=").Append((e.PhaseSumUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",other=").Append((e.UnattributedUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",top=").Append(PhaseName(e.Top1)).Append(':').Append((e.Top1Us / 1000.0).ToString("F2"))
                    .Append('/').Append(PhaseName(e.Top2)).Append(':').Append((e.Top2Us / 1000.0).ToString("F2"))
                    .Append('/').Append(PhaseName(e.Top3)).Append(':').Append((e.Top3Us / 1000.0).ToString("F2"))
                    .Append(",gc=").Append(e.Gc0).Append('/').Append(e.Gc1).Append('/').Append(e.Gc2);
            }
            return sb.ToString();
        }

        private static string PhaseName(int index)
        {
            return index >= 0 && index < PhaseNames.Length ? PhaseNames[index] : "None";
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct TailFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TotalUs;
            internal readonly long PhaseSumUs;
            internal readonly long UnattributedUs;
            internal readonly int Top1;
            internal readonly long Top1Us;
            internal readonly int Top2;
            internal readonly long Top2Us;
            internal readonly int Top3;
            internal readonly long Top3Us;
            internal readonly int Gc0;
            internal readonly int Gc1;
            internal readonly int Gc2;

            internal TailFrame(long frame, int gameTick, long totalUs, long phaseSumUs, long unattributedUs,
                int top1, long top1Us, int top2, long top2Us, int top3, long top3Us,
                int gc0, int gc1, int gc2)
            {
                Frame = frame; GameTick = gameTick; TotalUs = totalUs; PhaseSumUs = phaseSumUs;
                UnattributedUs = unattributedUs; Top1 = top1; Top1Us = top1Us;
                Top2 = top2; Top2Us = top2Us; Top3 = top3; Top3Us = top3Us;
                Gc0 = gc0; Gc1 = gc1; Gc2 = gc2;
            }
        }
    }
}

