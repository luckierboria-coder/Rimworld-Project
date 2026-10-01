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
            sb.AppendLine("[RimMT] V0.9.3-T34C.4 Refuel Eligibility Shadow Census on-demand report");
            sb.AppendLine("ProgramState=" + Current.ProgramState + ", mainThreadFrames=" + RimMTRuntime.MainThreadFrames);
            sb.AppendLine(RuntimeCompatibility.Summary());

            AppendScheduler(sb);
            sb.AppendLine(MainThreadDispatcher.Summary());
            AppendLoad(sb);
            sb.AppendLine(TailObservatory093T0.Summary());
            sb.AppendLine(TailObservatory093T0.ComponentSummary());
            sb.AppendLine(TailObservatory093T0.RecentSummary());
            sb.AppendLine(TailAttribution093T1.Summary());
            sb.AppendLine(TailAttribution093T1.RecentSevereSummary());
            sb.AppendLine(TailPawnAttribution093T2.Summary());
            sb.AppendLine(TailPawnAttribution093T2.RecentSummary());
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.Summary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.CategorySummary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.RecentSummary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.PawnTickHarmonyCensus());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.Summary());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.StageSummary());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.ProbeSummary());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.RecentSummary());
            sb.AppendLine(WorkGiverDeepAttribution093T15.Summary());
            sb.AppendLine(WorkGiverProfiler.Summary(24));
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
            sb.AppendLine(GenClosestDeepAttribution093T16.Summary(24));
            sb.AppendLine(MobileSourceRescue093T18.Summary());
            sb.AppendLine(StorytellerDeepAttribution093T18.Summary());
            sb.AppendLine(QuestDeepAttribution093T19.Summary());
            sb.AppendLine(JobSearchPackageContext093T28.Summary());
            sb.AppendLine("Idle safety: T34-D worker validators, T20/T21 validator/reach replay, T22 result index, T32 reservation replay and T26 epoch hooks are absent from this assembly.");
            sb.AppendLine(WorldTailBoundary093T22.Summary());
            sb.AppendLine(WorldRootAttribution093T22.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
            sb.AppendLine(StorytellerCatastrophic093T15.Summary());
            sb.AppendLine(StorytellerCatastrophic093T15.HarmonyCensus());
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine("Production policy D.2.3 Clean Idle: T34-B snapshot planning retained; all WorkGiver validators, Reachability and reservations execute live on the main thread; no gameplay result replay; CleanPathfinding experiments absent.");

            sb.AppendLine("--- Production path counters ---");
            sb.AppendLine(ScannerParallelFabric093T34B.Summary());
            sb.AppendLine(CandidateClassificationFabric093T34C.Summary());
            sb.AppendLine(CandidateFabric093T34A.Summary());
            sb.AppendLine(PersistentMapSearchFabric.Summary());
            sb.AppendLine("T34-C.4 candidate classification=ACTIVE(root-independent SourceSnapshot+Kernel plans indexed by stable SourceIndex + live parity + Refuel/Refuel_Turret measurement-only shadow census + bounded scanner-miss evidence); Refuel facts never reject candidates; invalid/duplicate indices and unknown Harmony owners fail open; T34-B distance planning remains active while the T34-A gameplay consumer is retired.");
            sb.AppendLine(JobGiverHybridTailS51.Summary());
            sb.AppendLine(JobGiverSlowSearch0419S.Summary());
            sb.AppendLine("Stage3 large-set rescue: RETIRED/OFF on T34-A production line.");
            sb.AppendLine(PersistentDoBillIndex092.Summary());
            sb.AppendLine(CleanPathfindingGlowCache093T34D3.Summary());
            sb.AppendLine("Global haul production V0.4.7: RETIRED/OFF in D.2.3 after 6/736 accelerations and zero avoided candidates.");
            sb.AppendLine("DoBill worker-tail fabric: RETIRED/OFF on T34-A production line.");
            sb.AppendLine(CommonSenseIngredientExpand092.Summary());
            sb.AppendLine("ReachProfile: RETIRED/OFF on T34-A production line.");

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
                    .Append("  busyAvg ").Append(scheduler.ProductionBusyAverageWorkers.ToString("F2"))
                    .Append("  busyUtil ").Append(scheduler.ProductionBusyUtilizationPercent.ToString("F1")).Append("%")
                    .AppendLine();
                sb.Append("Production busy: ").Append(scheduler.ProductionBusyMilliseconds.ToString("F0")).Append(" ms worker-time")
                    .Append(" / ").Append(scheduler.ProductionBusyWindowMilliseconds.ToString("F0")).Append(" ms wall-window")
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
            sb.AppendLine(CleanPathfindingGlowCache093T34D3.Summary());
            sb.AppendLine("DoBill worker-tail fabric: RETIRED/OFF on T34-A production line.");
            sb.AppendLine(JobGiverHybridTailS51.Summary());
            sb.AppendLine(JobGiverSlowSearch0419S.Summary());
            sb.AppendLine("Stage3 large-set rescue: RETIRED/OFF on T34-A production line.");
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
                ", busyWorkerMs=" + scheduler.ProductionBusyMilliseconds.ToString("F2") +
                ", busyWindowMs=" + scheduler.ProductionBusyWindowMilliseconds.ToString("F2") +
                ", busyAvgWorkers=" + scheduler.ProductionBusyAverageWorkers.ToString("F3") +
                ", busyUtilization=" + scheduler.ProductionBusyUtilizationPercent.ToString("F2") + "%" +
                ", frameSamples=" + scheduler.ProductionConcurrencySamples +
                ", sampledAvgActive=" + scheduler.ProductionAverageActiveWorkers.ToString("F3") +
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








































