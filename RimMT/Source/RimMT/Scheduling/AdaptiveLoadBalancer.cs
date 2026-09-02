using System;
using System.Diagnostics;
using System.Threading;

namespace RimMT
{
    internal enum LoadPressure { Low, Normal, High, Critical }

    internal static class AdaptiveLoadBalancer
    {
        private const int Window = 256;
        private const int PressureRefreshMask = 15; // refresh rolling pressure every 16 samples
        private const int DownshiftWindows = 4;

        private static readonly object Sync = new object();
        private static readonly double[] TickMs = new double[Window];
        private static readonly double[] SortScratch = new double[Window];

        private static int index;
        private static int count;
        private static int downshiftStreak;
        private static double emaMs;
        private static double rollingP95Ms;
        private static double rollingSlowRatio;
        private static long sampleCount;
        private static long spikes;
        private static long butterFrameSamples;
        private static int pressureValue = (int)LoadPressure.Normal;

        internal static LoadPressure Pressure { get { return (LoadPressure)Volatile.Read(ref pressureValue); } }
        internal static double EmaTickMs { get { lock (Sync) return emaMs; } }
        internal static double RollingP95Ms { get { lock (Sync) return rollingP95Ms; } }
        internal static double RollingSlowRatio { get { lock (Sync) return rollingSlowRatio; } }
        internal static long SampleCount { get { return Interlocked.Read(ref sampleCount); } }
        internal static long SpikeCount { get { return Interlocked.Read(ref spikes); } }
        internal static long ButterFrameSamples { get { return Interlocked.Read(ref butterFrameSamples); } }
        internal static string SampleSource { get { return RuntimeCompatibility.ButterPlusPlusActive ? "Butter++ TickManagerUpdate slice" : "DoSingleTick"; } }

        // Background maintenance is not binary any more. Low pressure may use all workers,
        // Normal uses roughly half, High keeps one lane, and Critical pauses background work.
        // High-priority offloads remain available so a worker-side search/index refresh can help
        // relieve the next main-thread JobGiver/DoBill pass instead of being starved by the burst.
        internal static int BackgroundConcurrencyBudget(int workerCount)
        {
            if (workerCount <= 0) return 0;
            switch (Pressure)
            {
                case LoadPressure.Low: return workerCount;
                case LoadPressure.Normal: return Math.Max(1, (workerCount + 1) / 2);
                case LoadPressure.High: return 1;
                default: return 0;
            }
        }

        internal static JobPriority RecommendedOffloadPriority
        {
            get
            {
                LoadPressure p = Pressure;
                if (p == LoadPressure.High || p == LoadPressure.Critical) return JobPriority.High;
                if (p == LoadPressure.Low) return JobPriority.Background;
                return JobPriority.Normal;
            }
        }

        internal static void RecordTick(long startTimestamp)
        {
            // Butter++ may hold one logical DoSingleTick open across several rendered frames and
            // manually replay other mods' DoSingleTick prefixes/postfixes. Measuring that wall time
            // would include inter-frame time and poison RimMT's pressure model. In Butter++ mode,
            // TickManagerUpdate frame slices are sampled instead.
            if (RuntimeCompatibility.ButterPlusPlusActive)
                return;
            RecordSample(startTimestamp);
        }

        internal static void RecordButterFrameSlice(long startTimestamp)
        {
            if (!RuntimeCompatibility.ButterPlusPlusActive || startTimestamp == 0L)
                return;
            Interlocked.Increment(ref butterFrameSamples);
            RecordSample(startTimestamp);
        }

        private static void RecordSample(long startTimestamp)
        {
            if (startTimestamp == 0L)
                return;

            long end = Stopwatch.GetTimestamp();
            double ms = (end - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            long samples = Interlocked.Increment(ref sampleCount);

            lock (Sync)
            {
                TickMs[index] = ms;
                index = (index + 1) % Window;
                if (count < Window) count++;

                emaMs = emaMs <= 0.0 ? ms : (emaMs * 0.92 + ms * 0.08);

                // Spike count remains an inexpensive lifetime signal. Rolling pressure itself is
                // refreshed from the whole recent window so one good tick can no longer erase a burst.
                double spikeThreshold = Math.Max(20.0, emaMs * 1.75);
                if (ms >= spikeThreshold) Interlocked.Increment(ref spikes);

                if ((samples & PressureRefreshMask) == 0 || count < 32)
                    RefreshPressureLocked();
            }
        }

        private static void RefreshPressureLocked()
        {
            if (count <= 0)
                return;

            for (int i = 0; i < count; i++) SortScratch[i] = TickMs[i];
            Array.Sort(SortScratch, 0, count);
            int p95Pos = (int)Math.Ceiling(count * 0.95) - 1;
            if (p95Pos < 0) p95Pos = 0;
            if (p95Pos >= count) p95Pos = count - 1;
            rollingP95Ms = SortScratch[p95Pos];

            double slowThreshold = Math.Max(20.0, emaMs * 1.35);
            int slow = 0;
            for (int i = 0; i < count; i++)
                if (TickMs[i] >= slowThreshold) slow++;
            rollingSlowRatio = slow / (double)count;

            LoadPressure candidate;
            if (rollingP95Ms >= Math.Max(40.0, emaMs * 2.00) || rollingSlowRatio >= 0.20)
                candidate = LoadPressure.Critical;
            else if (rollingP95Ms >= Math.Max(28.0, emaMs * 1.50) || rollingSlowRatio >= 0.08)
                candidate = LoadPressure.High;
            else if (emaMs < 8.0 && rollingP95Ms < 12.0 && rollingSlowRatio < 0.02)
                candidate = LoadPressure.Low;
            else
                candidate = LoadPressure.Normal;

            LoadPressure current = (LoadPressure)Volatile.Read(ref pressureValue);
            if (candidate > current)
            {
                downshiftStreak = 0;
                Volatile.Write(ref pressureValue, (int)candidate);
                return;
            }

            if (candidate == current)
            {
                downshiftStreak = 0;
                return;
            }

            // Downshift only after several clean rolling windows and only one level at a time.
            // This hysteresis prevents High/Critical from collapsing to Normal on one quiet tick.
            downshiftStreak++;
            if (downshiftStreak < DownshiftWindows)
                return;

            downshiftStreak = 0;
            int next = Math.Max((int)candidate, (int)current - 1);
            Volatile.Write(ref pressureValue, next);
        }

        internal static double Percentile95()
        {
            lock (Sync)
            {
                if (count == 0) return 0.0;
                // The rolling value is refreshed every <=16 samples and avoids allocating on report.
                if (rollingP95Ms > 0.0) return rollingP95Ms;
                for (int i = 0; i < count; i++) SortScratch[i] = TickMs[i];
                Array.Sort(SortScratch, 0, count);
                int pos = (int)Math.Ceiling(count * 0.95) - 1;
                if (pos < 0) pos = 0;
                if (pos >= count) pos = count - 1;
                return SortScratch[pos];
            }
        }
    }
}
