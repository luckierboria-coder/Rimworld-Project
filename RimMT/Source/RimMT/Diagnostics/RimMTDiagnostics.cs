using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Lightweight on-demand diagnostics only. Unified Lean does not install hot-path profilers;
    /// production modules expose aggregate counters that are read only when a report/window asks.
    /// </summary>
    public static class RimMTDiagnostics
    {
        internal static void LogStartupReport()
        {
            LogRuntimeReport();
        }

        public static void LogRuntimeReport()
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.AppendLine("[RimMT] V0.9.2 Unified Lean on-demand report");
            sb.AppendLine("ProgramState=" + Current.ProgramState + ", mainThreadFrames=" + RimMTRuntime.MainThreadFrames);
            sb.AppendLine(RuntimeCompatibility.Summary());

            AppendScheduler(sb);
            sb.AppendLine(MainThreadDispatcher.Summary());
            AppendLoad(sb);
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine("Production policy: diagnostics=external; PathSnapshot=OFF; WorkPrefilter=OFF; ReachNoCache=OFF; OverlayCache=OFF; S5.1 admission=16ms; S4 tail=32ms; RC2 Stage3 >=128; DoBill=persistent membership + live readiness + worker-tail fabric; S5.3 mature pruners; CommonSense=ingredientExpand memo only.");

            sb.AppendLine("--- Production path counters ---");
            sb.AppendLine(PersistentMapSearchFabric.Summary());
            sb.AppendLine(AdaptiveGenClosestAssist.Summary());
            sb.AppendLine(JobGiverHybridTailS51.Summary());
            sb.AppendLine(JobGiverSlowSearch0419S.Summary());
            sb.AppendLine(LargeSetTailRescue092.Summary());
            sb.AppendLine(PersistentDoBillIndex092.Summary());
            sb.AppendLine(DoBillTailFabric092.Summary());
            sb.AppendLine(CommonSenseIngredientExpand092.Summary());
            sb.AppendLine(AggressiveReachabilityProfiles.Summary());

            sb.AppendLine("--- Feature gates ---");
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

        internal static string BuildCompactMonitorText()
        {
            StringBuilder sb = new StringBuilder(4096);
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            sb.Append("Pressure: ").Append(AdaptiveLoadBalancer.Pressure)
                .Append("  EMA ").Append(AdaptiveLoadBalancer.EmaTickMs.ToString("F1")).Append(" ms")
                .Append("  P95 ").Append(AdaptiveLoadBalancer.Percentile95().ToString("F1")).Append(" ms")
                .Append("  slow ").Append((AdaptiveLoadBalancer.RollingSlowRatio * 100.0).ToString("F1")).Append("%")
                .AppendLine();
            sb.Append("Samples: ").Append(AdaptiveLoadBalancer.SampleCount)
                .Append("  spikes: ").Append(AdaptiveLoadBalancer.SpikeCount);
            if (scheduler != null)
            {
                sb.Append("  bgBudget: ").Append(AdaptiveLoadBalancer.BackgroundConcurrencyBudget(scheduler.WorkerCount))
                    .Append('/').Append(scheduler.WorkerCount).AppendLine();
                sb.Append("Production workers: active ").Append(scheduler.ProductionActiveWorkers)
                    .Append("  peak ").Append(scheduler.ProductionPeakActiveWorkers)
                    .Append("  avg ").Append(scheduler.ProductionAverageActiveWorkers.ToString("F2"))
                    .Append("  util ").Append(scheduler.ProductionWorkerUtilizationPercent.ToString("F1")).Append("%")
                    .AppendLine();
                sb.Append("Production queue: pending ").Append(scheduler.ProductionPending)
                    .Append("  highWater ").Append(scheduler.ProductionHighWaterPending)
                    .Append("  tasks ").Append(scheduler.ProductionCompleted).Append('/').Append(scheduler.ProductionEnqueued)
                    .Append("  rejected ").Append(scheduler.ProductionRejected)
                    .Append("  failures ").Append(scheduler.ProductionFailures).AppendLine();
            }
            else
            {
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine(PersistentDoBillIndex092.Summary());
            sb.AppendLine(DoBillTailFabric092.Summary());
            sb.AppendLine(JobGiverHybridTailS51.Summary());
            sb.AppendLine(JobGiverSlowSearch0419S.Summary());
            sb.AppendLine(LargeSetTailRescue092.Summary());
            sb.AppendLine(CommonSenseIngredientExpand092.Summary());
            return sb.ToString();
        }

        private static void AppendScheduler(StringBuilder sb)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null) return;

            sb.AppendLine("Scheduler(all incl. diagnostics): logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                ", workers=" + scheduler.WorkerCount +
                ", pending=" + scheduler.Pending +
                ", enqueued=" + scheduler.Enqueued +
                ", completed=" + scheduler.Completed +
                ", rejected=" + scheduler.Rejected +
                ", failures=" + scheduler.Failures +
                ", active=" + scheduler.ActiveWorkers +
                ", peakActive=" + scheduler.PeakActiveWorkers +
                ", highWater=" + scheduler.HighWaterPending);

            sb.AppendLine("Production scheduler(excludes self-test): pending=" + scheduler.ProductionPending +
                ", enqueued=" + scheduler.ProductionEnqueued +
                ", completed=" + scheduler.ProductionCompleted +
                ", rejected=" + scheduler.ProductionRejected +
                ", failures=" + scheduler.ProductionFailures +
                ", active=" + scheduler.ProductionActiveWorkers +
                ", peakActive=" + scheduler.ProductionPeakActiveWorkers +
                ", highWater=" + scheduler.ProductionHighWaterPending +
                ", parallelBatches=" + scheduler.ProductionParallelBatches +
                ", concurrencySamples=" + scheduler.ProductionConcurrencySamples +
                ", avgActive=" + scheduler.ProductionAverageActiveWorkers.ToString("F3") +
                ", sampledUtilization=" + scheduler.ProductionWorkerUtilizationPercent.ToString("F2") + "%");
        }

        private static void AppendLoad(StringBuilder sb)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            int budget = scheduler == null ? 0 : AdaptiveLoadBalancer.BackgroundConcurrencyBudget(scheduler.WorkerCount);
            sb.AppendLine("Load pressure: " + AdaptiveLoadBalancer.Pressure +
                ", source=" + AdaptiveLoadBalancer.SampleSource +
                ", samples=" + AdaptiveLoadBalancer.SampleCount +
                ", butterSamples=" + AdaptiveLoadBalancer.ButterFrameSamples +
                ", EMAms=" + AdaptiveLoadBalancer.EmaTickMs.ToString("F3") +
                ", P95ms=" + AdaptiveLoadBalancer.Percentile95().ToString("F3") +
                ", slowRatio=" + (AdaptiveLoadBalancer.RollingSlowRatio * 100.0).ToString("F2") + "%" +
                ", spikes=" + AdaptiveLoadBalancer.SpikeCount +
                ", backgroundBudget=" + budget +
                ", offloadPriority=" + AdaptiveLoadBalancer.RecommendedOffloadPriority);
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
                        ", parallelBatches=" + currentScheduler.ParallelBatchesEnqueued +
                        ". Production scheduler counters intentionally exclude this self-test.");
                },
                JobPriority.High);

            if (!accepted)
                Log.Warning("[RimMT] Worker self-test was not accepted by the bounded scheduler.");
        }
    }
}
