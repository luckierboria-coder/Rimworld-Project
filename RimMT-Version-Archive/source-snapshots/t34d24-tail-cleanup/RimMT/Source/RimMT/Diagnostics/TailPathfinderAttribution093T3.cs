using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    internal static class TailPathfinderAttribution093T3
    {
        private const int RecentCapacity = 16;
        private static readonly PathFrame[] Recent = new PathFrame[RecentCapacity];

        [ThreadStatic] private static bool active;
        [ThreadStatic] private static long currentUs;
        [ThreadStatic] private static long currentMaxCallUs;
        [ThreadStatic] private static int currentCalls;

        private static long calls;
        private static long totalUs;
        private static long maxUs;
        private static long over5;
        private static long over10;
        private static long over20;
        private static long over50;
        private static int recentPos;
        private static int recentCount;

        internal static void BeginTick()
        {
            active = true;
            currentUs = 0L;
            currentMaxCallUs = 0L;
            currentCalls = 0;
        }

        internal static long BeginCall()
        {
            if (!active || !TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndCall(long started)
        {
            if (started == 0L || !active) return;
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            if (elapsedTicks <= 0L) return;
            long us = TicksToUs(elapsedTicks);
            calls++;
            totalUs += us;
            currentUs += us;
            currentCalls++;
            if (us > currentMaxCallUs) currentMaxCallUs = us;
            if (us > maxUs) maxUs = us;
            if (us >= 5000L) over5++;
            if (us >= 10000L) over10++;
            if (us >= 20000L) over20++;
            if (us >= 50000L) over50++;
        }

        internal static void EndTick(long tickUs)
        {
            if (!active) return;
            active = false;
            if (tickUs < 20000L || currentCalls <= 0) return;

            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }
            Recent[recentPos] = new PathFrame(RimMTRuntime.MainThreadFrames, gameTick, tickUs, currentCalls, currentUs, currentMaxCallUs);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            double avg = calls == 0L ? 0.0 : totalUs / (double)calls;
            return "T3 PathFinder deep attribution: calls=" + calls +
                   ", avgUs=" + avg.ToString("F1") +
                   ", >5/10/20/50=" + over5 + "/" + over10 + "/" + over20 + "/" + over50 +
                   ", maxMs=" + (maxUs / 1000.0).ToString("F2") + ".";
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "T3 recent deep >=20ms ticks with FindPath: none.";
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T3 recent deep >=20ms ticks with FindPath (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                PathFrame e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",total=").Append((e.TickUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",findCalls=").Append(e.Calls)
                    .Append(",findTotal=").Append((e.FindUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",findMax=").Append((e.MaxCallUs / 1000.0).ToString("F2")).Append("ms");
            }
            return sb.ToString();
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct PathFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TickUs;
            internal readonly int Calls;
            internal readonly long FindUs;
            internal readonly long MaxCallUs;

            internal PathFrame(long frame, int gameTick, long tickUs, int calls, long findUs, long maxCallUs)
            {
                Frame = frame;
                GameTick = gameTick;
                TickUs = tickUs;
                Calls = calls;
                FindUs = findUs;
                MaxCallUs = maxCallUs;
            }
        }
    }
}
