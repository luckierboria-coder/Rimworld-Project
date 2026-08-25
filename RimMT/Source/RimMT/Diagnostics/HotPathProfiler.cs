using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RimMT
{
    internal static class HotPathProfiler
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();
        internal static long Begin() { return Stopwatch.GetTimestamp(); }
        internal static void End(string id, long start)
        {
            if (start == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            lock (Sync)
            {
                Stat stat;
                if (!Stats.TryGetValue(id, out stat)) { stat = new Stat(); Stats.Add(id, stat); }
                stat.Count++; stat.TotalTicks += elapsed; if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed; stat.Buckets[BucketFor(elapsed)]++;
            }
        }
        internal static string Summary(string id)
        {
            lock (Sync)
            {
                Stat stat;
                if (!Stats.TryGetValue(id, out stat) || stat.Count == 0) return id + ": calls=0";
                double avgMs = stat.TotalTicks * 1000.0 / Stopwatch.Frequency / stat.Count;
                double maxMs = stat.MaxTicks * 1000.0 / Stopwatch.Frequency;
                return id + ": calls=" + stat.Count + ", avgMs=" + avgMs.ToString("F3") + ", p95Ms~=" + P95(stat).ToString("F3") + ", maxMs=" + maxMs.ToString("F3");
            }
        }
        private static int BucketFor(long ticks)
        {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            if (ms < 0.25) return 0; if (ms < 0.5) return 1; if (ms < 1.0) return 2; if (ms < 2.0) return 3; if (ms < 4.0) return 4; if (ms < 8.0) return 5; if (ms < 16.0) return 6; if (ms < 32.0) return 7; if (ms < 64.0) return 8; return 9;
        }
        private static double P95(Stat stat)
        {
            long target = (long)Math.Ceiling(stat.Count * 0.95); long cumulative = 0; double[] upper = { 0.25,0.5,1,2,4,8,16,32,64,128 };
            for (int i = 0; i < stat.Buckets.Length; i++) { cumulative += stat.Buckets[i]; if (cumulative >= target) return upper[i]; }
            return 128.0;
        }
        private sealed class Stat { internal long Count; internal long TotalTicks; internal long MaxTicks; internal readonly long[] Buckets = new long[10]; }
    }
}
