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

        // Tick/frame pressure sampling is produced only from RimWorld's main thread. Workers read
        // only pressureValue, so the hot sampling path needs no lock or Interlocked RMW operations.
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
        internal static double EmaTickMs { get { return Volatile.Read(ref emaMs); } }
        internal static double RollingP95Ms { get { return Volatile.Read(ref rollingP95Ms); } }
        internal static double RollingSlowRatio { get { return Volatile.Read(ref rollingSlowRatio); } }
        internal static long SampleCount { get { return Volatile.Read(ref sampleCount); } }
        internal static long SpikeCount { get { return Volatile.Read(ref spikes); } }
        internal static long ButterFrameSamples { get { return Volatile.Read(ref butterFrameSamples); } }
        internal static string SampleSource { get { return RuntimeCompatibility.ButterPlusPlusActive ? "Butter++ TickManagerUpdate slice" : "DoSingleTick"; } }

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
            if (RuntimeCompatibility.ButterPlusPlusActive)
                return;
            RecordSample(startTimestamp);
        }

        internal static void RecordButterFrameSlice(long startTimestamp)
        {
            if (!RuntimeCompatibility.ButterPlusPlusActive || startTimestamp == 0L)
                return;
            butterFrameSamples++;
            RecordSample(startTimestamp);
        }

        private static void RecordSample(long startTimestamp)
        {
            if (startTimestamp == 0L)
                return;

            long end = Stopwatch.GetTimestamp();
            double ms = (end - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            long samples = ++sampleCount;

            TickMs[index] = ms;
            index = (index + 1) % Window;
            if (count < Window) count++;

            emaMs = emaMs <= 0.0 ? ms : (emaMs * 0.92 + ms * 0.08);

            double spikeThreshold = Math.Max(20.0, emaMs * 1.75);
            if (ms >= spikeThreshold) spikes++;

            if ((samples & PressureRefreshMask) == 0 || count < 32)
                RefreshPressure();
        }

        private static void RefreshPressure()
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

            downshiftStreak++;
            if (downshiftStreak < DownshiftWindows)
                return;

            downshiftStreak = 0;
            int next = Math.Max((int)candidate, (int)current - 1);
            Volatile.Write(ref pressureValue, next);
        }

        internal static double Percentile95()
        {
            // Reports/monitor run on the main thread. rollingP95Ms is refreshed every <=16 samples,
            // so the normal report path performs no sort and no allocation.
            double cached = Volatile.Read(ref rollingP95Ms);
            if (cached > 0.0) return cached;
            if (count == 0) return 0.0;

            for (int i = 0; i < count; i++) SortScratch[i] = TickMs[i];
            Array.Sort(SortScratch, 0, count);
            int pos = (int)Math.Ceiling(count * 0.95) - 1;
            if (pos < 0) pos = 0;
            if (pos >= count) pos = count - 1;
            return SortScratch[pos];
        }
    }
}
