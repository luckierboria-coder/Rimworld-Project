using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Lightweight on-demand diagnostics only. Unified Lean does not install any hot-path
    /// profiler Harmony hooks from this class. Deep profiling belongs in optional sidecars.
    /// </summary>
    public static class RimMTDiagnostics
    {
        internal static void LogStartupReport()
        {
            LogRuntimeReport();
        }

        public static void LogRuntimeReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[RimMT] V0.9.2 Unified Lean on-demand report");
            sb.AppendLine("ProgramState=" + Current.ProgramState + ", mainThreadFrames=" + RimMTRuntime.MainThreadFrames);
            sb.AppendLine(RuntimeCompatibility.Summary());

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler != null)
            {
                sb.AppendLine("Scheduler: logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                    ", workers=" + scheduler.WorkerCount +
                    ", pending=" + scheduler.Pending +
                    ", enqueued=" + scheduler.Enqueued +
                    ", completed=" + scheduler.Completed +
                    ", rejected=" + scheduler.Rejected +
                    ", failures=" + scheduler.Failures +
                    ", active=" + scheduler.ActiveWorkers +
                    ", peakActive=" + scheduler.PeakActiveWorkers +
                    ", highWater=" + scheduler.HighWaterPending);
            }

            sb.AppendLine(MainThreadDispatcher.Summary());
            sb.AppendLine("Load pressure: " + AdaptiveLoadBalancer.Pressure +
                ", source=" + AdaptiveLoadBalancer.SampleSource +
                ", samples=" + AdaptiveLoadBalancer.SampleCount +
                ", butterSamples=" + AdaptiveLoadBalancer.ButterFrameSamples +
                ", EMAms=" + AdaptiveLoadBalancer.EmaTickMs.ToString("F3") +
                ", P95ms=" + AdaptiveLoadBalancer.Percentile95().ToString("F3") +
                ", spikes=" + AdaptiveLoadBalancer.SpikeCount);
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine("Production policy: diagnostics=external; PathSnapshot=OFF; WorkPrefilter=OFF; ReachNoCache=OFF; OverlayCache=OFF; S5.1 admission=16ms; S4 tail=32ms; RC2 Stage3 >=128; DoBill=persistent membership index; S5.3 mature pruners; CommonSense=ingredientExpand memo only.");

            Dictionary<string, FeatureGate.FeatureState> states = FeatureGate.Snapshot();
            foreach (KeyValuePair<string, FeatureGate.FeatureState> pair in states)
            {
                FeatureGate.FeatureState state = pair.Value;
                sb.Append(" - ").Append(pair.Key).Append(": ")
                    .Append(state.Enabled && !state.Suppressed ? "ACTIVE" : "OFF");
                if (!string.IsNullOrEmpty(state.Reason)) sb.Append(" (").Append(state.Reason).Append(")");
                sb.AppendLine();
            }

            foreach (string line in CompatibilityGuard.Report)
                sb.AppendLine(" * " + line);

            Log.Message(sb.ToString());
        }

        public static void RunWorkerSelfTest()
        {
            if (!RimMTRuntime.Initialized || RimMTRuntime.Scheduler == null)
            {
                Log.Warning("[RimMT] Worker self-test cannot run because the scheduler is not initialized.");
                return;
            }

            const int end = 4000000;
            long total = 0L;
            object totalSync = new object();
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool accepted = RimMTRuntime.Scheduler.ParallelFor(
                "diagnostics.selfTest",
                0,
                end,
                50000,
                delegate(int from, int to)
                {
                    long local = 0L;
                    for (int i = from; i < to; i++)
                        local += ((long)i * 31L) ^ (i >> 3);
                    lock (totalSync) total += local;
                },
                delegate
                {
                    stopwatch.Stop();
                    JobScheduler currentScheduler = RimMTRuntime.Scheduler;
                    Log.Message("[RimMT] Worker self-test passed: logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                        ", workers=" + currentScheduler.WorkerCount +
                        ", elapsedMs=" + stopwatch.ElapsedMilliseconds +
                        ", checksum=" + total +
                        ", enqueued=" + currentScheduler.Enqueued +
                        ", completed=" + currentScheduler.Completed +
                        ", failures=" + currentScheduler.Failures +
                        ", peakActive=" + currentScheduler.PeakActiveWorkers +
                        ", highWater=" + currentScheduler.HighWaterPending +
                        ", multiWakeCalls=" + currentScheduler.MultiWakeCalls +
                        ", parallelBatches=" + currentScheduler.ParallelBatchesEnqueued);
                },
                JobPriority.High);

            if (!accepted)
                Log.Warning("[RimMT] Worker self-test was not accepted by the bounded scheduler.");
        }
    }
}
