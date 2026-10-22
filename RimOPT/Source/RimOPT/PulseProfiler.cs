#if RIMWORLD17
using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimOPT
{
    // Opt-in, observational only. No asynchronous pawn work and no per-event logging.
    // Sampling is disabled by default: normal gameplay does not install these hooks.
    internal static class PulseProfiler
    {
        // Histogram buckets in microseconds, chosen to resolve the 16-33ms stutter band.
        private static readonly long[] Limits = { 100, 250, 500, 1000, 2000, 4000, 8000, 12000,
            16000, 20000, 25000, 33000, 50000, 75000, 100000, 250000, 500000 };
        private static readonly Series InterFrame = new Series();
        private static readonly Series Update = new Series();
        private static readonly Series Tick = new Series();
        private static readonly Series Work = new Series();
        private static long lastFrameStart;
        private static int installed;
        private static string frameStatus = "未安装";
        private static string tickStatus = "未安装";
        private static readonly double MicrosecondsPerStopwatchTick = 1000000.0 / Stopwatch.Frequency;

        internal static string Status => "frame=" + frameStatus + ", tick=" + tickStatus +
            ", work=" + (Volatile.Read(ref installed) != 0 && PackageScope.Installed ? "attached" : "not-installed");
        internal static void Install(Harmony harmony)
        {
            if (Interlocked.Exchange(ref installed, 1) != 0) return;
            var frame = AccessTools.Method(typeof(Root_Play), "Update", Type.EmptyTypes);
            if (frame != null)
            {
                try
                {
                    harmony.Patch(frame,
                        prefix: new HarmonyMethod(typeof(PulseProfiler), nameof(FramePrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(PulseProfiler), nameof(FramePostfix)) { priority = Priority.Last });
                    frameStatus = "attached";
                }
                catch (Exception ex)
                {
                    frameStatus = "unavailable/" + ex.GetType().Name;
                    Log.Warning("[RimOPT/1.7] frame sampling not attached: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            else frameStatus = "target absent";

            var tick = AccessTools.Method(typeof(TickManager), "DoSingleTick", Type.EmptyTypes);
            if (tick != null)
            {
                try
                {
                    harmony.Patch(tick,
                        prefix: new HarmonyMethod(typeof(PulseProfiler), nameof(TickPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(PulseProfiler), nameof(TickPostfix)) { priority = Priority.Last });
                    tickStatus = "attached";
                }
                catch (Exception ex)
                {
                    tickStatus = "unavailable/" + ex.GetType().Name;
                    Log.Warning("[RimOPT/1.7] tick sampling not attached: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            else tickStatus = "target absent";
            Log.Message("[RimOPT/1.7] opt-in pulse sampler: " + Status);
        }

        // No new arrays, lists, lambdas, I/O, or work deferral in patched hot paths.
        public static void FramePrefix(ref long __state)
        {
            long now = Stopwatch.GetTimestamp();
            __state = now;
            long last = Interlocked.Exchange(ref lastFrameStart, now);
            if (last == 0) return;
            long elapsed = now - last;
            // Ignore window focus changes / loading stalls over five seconds.
            if (elapsed > 0 && elapsed < Stopwatch.Frequency * 5L) InterFrame.Add(elapsed);
        }
        public static void FramePostfix(long __state)
        {
            if (__state > 0) Update.Add(Stopwatch.GetTimestamp() - __state);
        }
        public static void TickPrefix(ref long __state) { __state = Stopwatch.GetTimestamp(); }
        public static void TickPostfix(long __state)
        {
            if (__state > 0) Tick.Add(Stopwatch.GetTimestamp() - __state);
        }
        internal static void RecordWork(long elapsedTicks) { if (Volatile.Read(ref installed) != 0) Work.Add(elapsedTicks); }
        internal static string Summary()
        {
            return "帧间隔 (含渲染等待): " + InterFrame.Summary() + "\n" +
                "Root_Play.Update CPU: " + Update.Summary() + "\n" +
                "DoSingleTick CPU: " + Tick.Summary() + "\n" +
                "单次 JobGiver_Work 搜索: " + Work.Summary() + "\n" +
                "模块: " + Status;
        }
        internal static void Reset()
        {
            InterFrame.Reset(); Update.Reset(); Tick.Reset(); Work.Reset();
            Interlocked.Exchange(ref lastFrameStart, 0L);
        }

        private sealed class Series
        {
            private readonly long[] buckets = new long[Limits.Length + 1];
            private long count, total, max;
            internal void Add(long ticks)
            {
                if (ticks < 0) return;
                long us = (long)(ticks * MicrosecondsPerStopwatchTick);
                int bucket = 0;
                while (bucket < Limits.Length && us > Limits[bucket]) bucket++;
                Interlocked.Increment(ref buckets[bucket]);
                Interlocked.Increment(ref count);
                Interlocked.Add(ref total, us);
                long old = Volatile.Read(ref max);
                while (us > old)
                {
                    long prior = Interlocked.CompareExchange(ref max, us, old);
                    if (prior == old) break;
                    old = prior;
                }
            }
            internal string Summary()
            {
                long n = Interlocked.Read(ref count);
                if (n <= 0) return "无样本";
                long percentile95 = Math.Max(1, (long)Math.Ceiling(n * 0.95));
                long percentile99 = Math.Max(1, (long)Math.Ceiling(n * 0.99));
                long scan = 0, p95 = -1, p99 = -1;
                for (int i = 0; i < buckets.Length; i++)
                {
                    scan += Interlocked.Read(ref buckets[i]);
                    long upper = i < Limits.Length ? Limits[i] : 500000;
                    if (p95 < 0 && scan >= percentile95) p95 = upper;
                    if (p99 < 0 && scan >= percentile99) p99 = upper;
                }
                return "n=" + n + ", avg=" + (Interlocked.Read(ref total) / (1000.0 * n)).ToString("F2") +
                    "ms, p95≤" + (p95 / 1000.0).ToString("F1") + "ms, p99≤" +
                    (p99 / 1000.0).ToString("F1") + "ms, max=" +
                    (Interlocked.Read(ref max) / 1000.0).ToString("F1") + "ms";
            }
            internal void Reset()
            {
                for (int i = 0; i < buckets.Length; i++) Interlocked.Exchange(ref buckets[i], 0L);
                Interlocked.Exchange(ref count, 0L);
                Interlocked.Exchange(ref total, 0L);
                Interlocked.Exchange(ref max, 0L);
            }
        }
    }
}
#endif
