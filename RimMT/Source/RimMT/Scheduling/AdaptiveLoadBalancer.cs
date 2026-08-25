using System;
using System.Diagnostics;
using System.Threading;

namespace RimMT
{
    internal enum LoadPressure { Low, Normal, High, Critical }

    internal static class AdaptiveLoadBalancer
    {
        private const int Window = 256;
        private static readonly object Sync = new object();
        private static readonly double[] TickMs = new double[Window];
        private static int index;
        private static int count;
        private static double emaMs;
        private static long spikes;
        private static int pressureValue = (int)LoadPressure.Normal;

        internal static LoadPressure Pressure { get { return (LoadPressure)Volatile.Read(ref pressureValue); } }
        internal static bool AllowBackground { get { LoadPressure p = Pressure; return p == LoadPressure.Low || p == LoadPressure.Normal; } }
        internal static double EmaTickMs { get { lock (Sync) return emaMs; } }
        internal static long SpikeCount { get { return Interlocked.Read(ref spikes); } }

        internal static void RecordTick(long startTimestamp)
        {
            long end = Stopwatch.GetTimestamp();
            double ms = (end - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            lock (Sync)
            {
                TickMs[index] = ms;
                index = (index + 1) % Window;
                if (count < Window) count++;
                emaMs = emaMs <= 0.0 ? ms : (emaMs * 0.92 + ms * 0.08);
                double high = Math.Max(10.0, emaMs * 1.75);
                double critical = Math.Max(20.0, emaMs * 2.75);
                LoadPressure next = ms >= critical ? LoadPressure.Critical : (ms >= high ? LoadPressure.High : (ms < Math.Max(3.0, emaMs * 0.75) ? LoadPressure.Low : LoadPressure.Normal));
                if (next == LoadPressure.High || next == LoadPressure.Critical) Interlocked.Increment(ref spikes);
                Volatile.Write(ref pressureValue, (int)next);
            }
        }

        internal static double Percentile95()
        {
            lock (Sync)
            {
                if (count == 0) return 0.0;
                double[] copy = new double[count];
                for (int i = 0; i < count; i++) copy[i] = TickMs[i];
                Array.Sort(copy);
                int pos = (int)Math.Ceiling(copy.Length * 0.95) - 1;
                if (pos < 0) pos = 0;
                if (pos >= copy.Length) pos = copy.Length - 1;
                return copy[pos];
            }
        }
    }
}
