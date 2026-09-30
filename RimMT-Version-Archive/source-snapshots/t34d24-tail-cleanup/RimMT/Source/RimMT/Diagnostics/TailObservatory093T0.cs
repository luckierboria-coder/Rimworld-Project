using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    [Flags]
    internal enum TailSignal093T0
    {
        None = 0,
        ReachQuery = 1,
        ReachCapture = 2,
        ReachTopologySlice = 4,
        S4HeavyValidator = 8
    }

    /// <summary>
    /// Measurement-only long-tail observer. The hot path is main-thread owned, allocation-free and
    /// lock-free: one fixed histogram increment, a few threshold counters and (only for >=20 ms
    /// ticks) one write into a fixed recent-tail ring. It never changes scheduling or game state.
    /// </summary>
    internal static class TailObservatory093T0
    {
        private const int BucketUs = 250;
        private const int HistogramCeilingUs = 250000;
        private const int HistogramBuckets = HistogramCeilingUs / BucketUs + 2; // final bucket is overflow
        private const int RecentCapacity = 16;

        private static readonly long FiveMsTicks = Math.Max(1L, Stopwatch.Frequency * 5L / 1000L);
        private static readonly long TenMsTicks = Math.Max(1L, Stopwatch.Frequency * 10L / 1000L);
        private static readonly long TwentyMsTicks = Math.Max(1L, Stopwatch.Frequency * 20L / 1000L);

        private static readonly long[] Histogram = new long[HistogramBuckets];
        private static readonly TailFrame[] Recent = new TailFrame[RecentCapacity];

        private static long samples;
        private static long totalUs;
        private static long maxUs;
        private static long over20;
        private static long over30;
        private static long over50;
        private static long over100;
        private static int recentPos;
        private static int recentCount;

        private static long reachQueryOver5;
        private static long reachQueryOver10;
        private static long reachQueryOver20;
        private static long reachQueryMaxUs;
        private static long reachCaptureOver5;
        private static long reachCaptureOver10;
        private static long reachCaptureOver20;
        private static long reachCaptureMaxUs;
        private static long topologyOver5;
        private static long topologyOver10;
        private static long topologyOver20;
        private static long topologyMaxUs;
        private static long s4HeavyEvents;
        private static int s4MaxRejects;

        [ThreadStatic] private static TailSignal093T0 currentSignals;
        [ThreadStatic] private static long currentReachQueryMaxUs;
        [ThreadStatic] private static long currentReachCaptureMaxUs;
        [ThreadStatic] private static long currentTopologyMaxUs;
        [ThreadStatic] private static int currentS4MaxRejects;

        internal static void BeginTick()
        {
            TailAttribution093T1.BeginTick();
            TailPawnAttribution093T2.BeginTick();
            currentSignals = TailSignal093T0.None;
            currentReachQueryMaxUs = 0L;
            currentReachCaptureMaxUs = 0L;
            currentTopologyMaxUs = 0L;
            currentS4MaxRejects = 0;
        }

        internal static void RecordTick(long startTimestamp, long endTimestamp)
        {
            long elapsedTicks = endTimestamp - startTimestamp;
            if (elapsedTicks <= 0L) return;

            long us = TicksToUs(elapsedTicks);
            TailAttribution093T1.EndTick(us);
            TailPawnAttribution093T2.EndTick(us);
            samples++;
            totalUs += us;
            if (us > maxUs) maxUs = us;

            int bucket = (int)(us / BucketUs);
            if (bucket >= HistogramBuckets - 1) bucket = HistogramBuckets - 1;
            Histogram[bucket]++;

            if (us >= 20000L) over20++;
            if (us >= 30000L) over30++;
            if (us >= 50000L) over50++;
            if (us >= 100000L) over100++;

            if (us < 20000L) return;

            int gameTick = -1;
            try
            {
                if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame;
            }
            catch { }

            Recent[recentPos] = new TailFrame(
                RimMTRuntime.MainThreadFrames,
                gameTick,
                us,
                currentSignals,
                currentReachQueryMaxUs,
                currentReachCaptureMaxUs,
                currentTopologyMaxUs,
                currentS4MaxRejects);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static void NoteReachQueryTicks(long elapsedTicks)
        {
            if (elapsedTicks < FiveMsTicks) return;
            long us = TicksToUs(elapsedTicks);
            currentSignals |= TailSignal093T0.ReachQuery;
            if (us > currentReachQueryMaxUs) currentReachQueryMaxUs = us;
            reachQueryOver5++;
            if (elapsedTicks >= TenMsTicks) reachQueryOver10++;
            if (elapsedTicks >= TwentyMsTicks) reachQueryOver20++;
            if (us > reachQueryMaxUs) reachQueryMaxUs = us;
        }

        internal static void NoteReachCaptureTicks(long elapsedTicks)
        {
            if (elapsedTicks < FiveMsTicks) return;
            long us = TicksToUs(elapsedTicks);
            currentSignals |= TailSignal093T0.ReachCapture;
            if (us > currentReachCaptureMaxUs) currentReachCaptureMaxUs = us;
            reachCaptureOver5++;
            if (elapsedTicks >= TenMsTicks) reachCaptureOver10++;
            if (elapsedTicks >= TwentyMsTicks) reachCaptureOver20++;
            if (us > reachCaptureMaxUs) reachCaptureMaxUs = us;
        }

        internal static void NoteTopologySliceTicks(long elapsedTicks)
        {
            if (elapsedTicks < FiveMsTicks) return;
            long us = TicksToUs(elapsedTicks);
            currentSignals |= TailSignal093T0.ReachTopologySlice;
            if (us > currentTopologyMaxUs) currentTopologyMaxUs = us;
            topologyOver5++;
            if (elapsedTicks >= TenMsTicks) topologyOver10++;
            if (elapsedTicks >= TwentyMsTicks) topologyOver20++;
            if (us > topologyMaxUs) topologyMaxUs = us;
        }

        internal static void NoteS4HeavyValidator(int rejects)
        {
            if (rejects <= 0) return;
            currentSignals |= TailSignal093T0.S4HeavyValidator;
            if (rejects > currentS4MaxRejects) currentS4MaxRejects = rejects;
            s4HeavyEvents++;
            if (rejects > s4MaxRejects) s4MaxRejects = rejects;
        }

        internal static string Summary()
        {
            long n = samples;
            double avgMs = n == 0L ? 0.0 : totalUs / (double)n / 1000.0;
            return "Tail Observatory V0.9.3-T0: samples=" + n +
                ", avgMs=" + avgMs.ToString("F3") +
                ", P50ms=" + (PercentileUs(0.50) / 1000.0).ToString("F3") +
                ", P95ms=" + (PercentileUs(0.95) / 1000.0).ToString("F3") +
                ", P99ms=" + (PercentileUs(0.99) / 1000.0).ToString("F3") +
                ", P99.9ms=" + (PercentileUs(0.999) / 1000.0).ToString("F3") +
                ", >20ms=" + over20 +
                ", >30ms=" + over30 +
                ", >50ms=" + over50 +
                ", >100ms=" + over100 +
                ", maxMs=" + (maxUs / 1000.0).ToString("F3") +
                ", spike20Per10k=" + RatePer10k(over20, n).ToString("F2") +
                ", spike50Per10k=" + RatePer10k(over50, n).ToString("F2") +
                ". Histogram resolution=" + BucketUs + "us; final bucket >=" + HistogramCeilingUs + "us.";
        }

        internal static string ComponentSummary()
        {
            return "Tail component signals (measurement-only): ReachQuery >5/>10/>20ms=" +
                reachQueryOver5 + "/" + reachQueryOver10 + "/" + reachQueryOver20 +
                ", maxMs=" + (reachQueryMaxUs / 1000.0).ToString("F3") +
                "; ReachCapture=" + reachCaptureOver5 + "/" + reachCaptureOver10 + "/" + reachCaptureOver20 +
                ", maxMs=" + (reachCaptureMaxUs / 1000.0).ToString("F3") +
                "; TopologySlice=" + topologyOver5 + "/" + topologyOver10 + "/" + topologyOver20 +
                ", maxMs=" + (topologyMaxUs / 1000.0).ToString("F3") +
                "; S4HeavyValidatorEvents=" + s4HeavyEvents + ", maxRejects=" + s4MaxRejects + ".";
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "Recent >=20ms tail frames: none.";
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("Recent >=20ms tail frames (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                int idx = (start + i) % RecentCapacity;
                TailFrame e = Recent[idx];
                if (i != 0) sb.Append("; ");
                sb.Append("frame=").Append(e.Frame)
                    .Append(",tick=").Append(e.GameTick)
                    .Append(",ms=").Append((e.DurationUs / 1000.0).ToString("F2"))
                    .Append(",signals=").Append(e.Signals);
                if (e.ReachQueryMaxUs > 0) sb.Append(",reachQ=").Append((e.ReachQueryMaxUs / 1000.0).ToString("F2")).Append("ms");
                if (e.ReachCaptureMaxUs > 0) sb.Append(",capture=").Append((e.ReachCaptureMaxUs / 1000.0).ToString("F2")).Append("ms");
                if (e.TopologyMaxUs > 0) sb.Append(",topology=").Append((e.TopologyMaxUs / 1000.0).ToString("F2")).Append("ms");
                if (e.S4MaxRejects > 0) sb.Append(",s4Rejects=").Append(e.S4MaxRejects);
            }
            return sb.ToString();
        }

        private static long PercentileUs(double percentile)
        {
            long n = samples;
            if (n <= 0L) return 0L;
            long target = (long)Math.Ceiling(n * percentile);
            if (target < 1L) target = 1L;
            long cumulative = 0L;
            for (int i = 0; i < Histogram.Length; i++)
            {
                cumulative += Histogram[i];
                if (cumulative >= target)
                {
                    if (i >= Histogram.Length - 1) return Math.Max(HistogramCeilingUs, maxUs);
                    return (long)i * BucketUs + BucketUs / 2;
                }
            }
            return maxUs;
        }

        private static double RatePer10k(long value, long n)
        {
            return n <= 0L ? 0.0 : value * 10000.0 / n;
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct TailFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long DurationUs;
            internal readonly TailSignal093T0 Signals;
            internal readonly long ReachQueryMaxUs;
            internal readonly long ReachCaptureMaxUs;
            internal readonly long TopologyMaxUs;
            internal readonly int S4MaxRejects;

            internal TailFrame(long frame, int gameTick, long durationUs, TailSignal093T0 signals,
                long reachQueryMaxUs, long reachCaptureMaxUs, long topologyMaxUs, int s4MaxRejects)
            {
                Frame = frame;
                GameTick = gameTick;
                DurationUs = durationUs;
                Signals = signals;
                ReachQueryMaxUs = reachQueryMaxUs;
                ReachCaptureMaxUs = reachCaptureMaxUs;
                TopologyMaxUs = topologyMaxUs;
                S4MaxRejects = s4MaxRejects;
            }
        }
    }
}


