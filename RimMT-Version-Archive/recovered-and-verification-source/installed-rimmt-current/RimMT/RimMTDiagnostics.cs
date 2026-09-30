using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT;

public static class RimMTDiagnostics
{
	internal static void LogStartupReport()
	{
		LogRuntimeReport();
	}

	public static void LogRuntimeReport()
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		StringBuilder stringBuilder = new StringBuilder(8192);
		stringBuilder.AppendLine("[RimMT] V0.9.3-T34C.4 Refuel Eligibility Shadow Census on-demand report");
		stringBuilder.AppendLine("ProgramState=" + ((object)Current.ProgramState/*cast due to .constrained prefix*/).ToString() + ", mainThreadFrames=" + RimMTRuntime.MainThreadFrames);
		stringBuilder.AppendLine(RuntimeCompatibility.Summary());
		AppendScheduler(stringBuilder);
		stringBuilder.AppendLine(MainThreadDispatcher.Summary());
		AppendLoad(stringBuilder);
		stringBuilder.AppendLine(TailObservatory093T0.Summary());
		stringBuilder.AppendLine(TailObservatory093T0.ComponentSummary());
		stringBuilder.AppendLine(TailObservatory093T0.RecentSummary());
		stringBuilder.AppendLine(TailAttribution093T1.Summary());
		stringBuilder.AppendLine(TailAttribution093T1.RecentSevereSummary());
		stringBuilder.AppendLine(TailPawnAttribution093T2.Summary());
		stringBuilder.AppendLine(TailPawnAttribution093T2.RecentSummary());
		stringBuilder.AppendLine(TailPathfinderAttribution093T3.Summary());
		stringBuilder.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
		stringBuilder.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
		stringBuilder.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
		stringBuilder.AppendLine(CarrierMechCheapNegative093T8.Summary());
		stringBuilder.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
		stringBuilder.AppendLine(PawnTickAggregateAttribution093T13.Summary());
		stringBuilder.AppendLine(PawnTickAggregateAttribution093T13.CategorySummary());
		stringBuilder.AppendLine(PawnTickAggregateAttribution093T13.RecentSummary());
		stringBuilder.AppendLine(PawnTickAggregateAttribution093T13.PawnTickHarmonyCensus());
		stringBuilder.AppendLine(PlayerHumanResidualAttribution093T14.Summary());
		stringBuilder.AppendLine(PlayerHumanResidualAttribution093T14.StageSummary());
		stringBuilder.AppendLine(PlayerHumanResidualAttribution093T14.ProbeSummary());
		stringBuilder.AppendLine(PlayerHumanResidualAttribution093T14.RecentSummary());
		stringBuilder.AppendLine(WorkGiverDeepAttribution093T15.Summary());
		stringBuilder.AppendLine(WorkGiverProfiler.Summary(24));
		stringBuilder.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
		stringBuilder.AppendLine(GenClosestDeepAttribution093T16.Summary(24));
		stringBuilder.AppendLine(MobileSourceRescue093T18.Summary());
		stringBuilder.AppendLine(StorytellerDeepAttribution093T18.Summary());
		stringBuilder.AppendLine(QuestDeepAttribution093T19.Summary());
		stringBuilder.AppendLine(JobSearchPackageContext093T28.Summary());
		stringBuilder.AppendLine(ReservationTransaction093T32A.Summary());
		stringBuilder.AppendLine(JobSearchTransaction093T20.Summary());
		stringBuilder.AppendLine(GenClosestTransactionIndex093T22.Summary());
		stringBuilder.AppendLine(SimulationEpochCoordinator093T26.Summary());
		stringBuilder.AppendLine(MemoryUnloadCoalescerD23.Summary());
		stringBuilder.AppendLine("T27/T27.1 speculative work-plan kernel: RETIRED/OFF in T27.2; no source reordering is installed on production JobGiver/GenClosest paths.");
		stringBuilder.AppendLine(WorkGiverParallelSafety093T27_2.Summary());
		stringBuilder.AppendLine(WorkGiverParallelSafety093T27_2.ApiMatrixSummary());
		stringBuilder.AppendLine(WorldTailBoundary093T22.Summary());
		stringBuilder.AppendLine(WorldRootAttribution093T22.Summary());
		stringBuilder.AppendLine(SMFDispatcherCoexistence093T16.Summary());
		stringBuilder.AppendLine(StorytellerCatastrophic093T15.Summary());
		stringBuilder.AppendLine(StorytellerCatastrophic093T15.HarmonyCensus());
		stringBuilder.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
		stringBuilder.AppendLine("Production policy D.2.3: diagnostics=external; T34-A gameplay Prefix=RETIRED; GlobalHaul V0.4.7=RETIRED; T34-B snapshot producer retained; T34-D slow-wait scanner quarantine=8ms; CleanPathfinding glow/region experiments=OFF; DoBill persistent index + package-local false readiness retained; T21/T22/T28/T32, S4 and S5.1 retained; one redundant entry-to-play Unity cleanup may be coalesced; no Job/reservation/priority/cross-package result is created or cached.");
		stringBuilder.AppendLine("--- Production path counters ---");
		stringBuilder.AppendLine(ScannerParallelFabric093T34B.Summary());
		stringBuilder.AppendLine(AggressiveParallelScanner093T34D.Summary());
		stringBuilder.AppendLine(CandidateClassificationFabric093T34C.Summary());
		stringBuilder.AppendLine(CandidateFabric093T34A.Summary());
		stringBuilder.AppendLine(PersistentMapSearchFabric.Summary());
		stringBuilder.AppendLine("T34-C.4 candidate classification=ACTIVE(root-independent SourceSnapshot+Kernel plans indexed by stable SourceIndex + live parity + Refuel/Refuel_Turret measurement-only shadow census + bounded scanner-miss evidence); Refuel facts never reject candidates; invalid/duplicate indices and unknown Harmony owners fail open; T34-B distance planning remains active while the T34-A gameplay consumer is retired.");
		stringBuilder.AppendLine(JobGiverHybridTailS51.Summary());
		stringBuilder.AppendLine(JobGiverSlowSearch0419S.Summary());
		stringBuilder.AppendLine("Stage3 large-set rescue: RETIRED/OFF on T34-A production line.");
		stringBuilder.AppendLine(PersistentDoBillIndex092.Summary());
		stringBuilder.AppendLine(CleanPathfindingGlowCache093T34D3.Summary());
		stringBuilder.AppendLine("Global haul production V0.4.7: RETIRED/OFF in D.2.3 after 6/736 accelerations and zero avoided candidates.");
		stringBuilder.AppendLine("DoBill worker-tail fabric: RETIRED/OFF on T34-A production line.");
		stringBuilder.AppendLine(CommonSenseIngredientExpand092.Summary());
		stringBuilder.AppendLine("ReachProfile: RETIRED/OFF on T34-A production line.");
		stringBuilder.AppendLine("--- Feature gates ---");
		foreach (KeyValuePair<string, FeatureGate.FeatureState> item in FeatureGate.Snapshot())
		{
			FeatureGate.FeatureState value = item.Value;
			stringBuilder.Append(" - ").Append(item.Key).Append(": ")
				.Append((value.Enabled && !value.Suppressed) ? "ACTIVE" : "OFF");
			if (!string.IsNullOrEmpty(value.Reason))
			{
				stringBuilder.Append(" (").Append(value.Reason).Append(")");
			}
			stringBuilder.AppendLine();
		}
		foreach (string item2 in CompatibilityGuard.Report)
		{
			stringBuilder.AppendLine(" * " + item2);
		}
		Log.Message(stringBuilder.ToString());
	}

	internal static string BuildCompactMonitorText()
	{
		StringBuilder stringBuilder = new StringBuilder(4096);
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		stringBuilder.Append("Pressure: ").Append(AdaptiveLoadBalancer.Pressure).Append("  EMA ")
			.Append(AdaptiveLoadBalancer.EmaTickMs.ToString("F1"))
			.Append(" ms")
			.Append("  P95 ")
			.Append(AdaptiveLoadBalancer.Percentile95().ToString("F1"))
			.Append(" ms")
			.Append("  slow ")
			.Append((AdaptiveLoadBalancer.RollingSlowRatio * 100.0).ToString("F1"))
			.Append("%")
			.AppendLine();
		stringBuilder.Append("Samples: ").Append(AdaptiveLoadBalancer.SampleCount).Append("  spikes: ")
			.Append(AdaptiveLoadBalancer.SpikeCount);
		if (scheduler != null)
		{
			stringBuilder.Append("  bgBudget: ").Append(AdaptiveLoadBalancer.BackgroundConcurrencyBudget(scheduler.WorkerCount)).Append('/')
				.Append(scheduler.WorkerCount)
				.AppendLine();
			stringBuilder.Append("Production workers: active ").Append(scheduler.ProductionActiveWorkers).Append("  peak ")
				.Append(scheduler.ProductionPeakActiveWorkers)
				.Append("  busyAvg ")
				.Append(scheduler.ProductionBusyAverageWorkers.ToString("F2"))
				.Append("  busyUtil ")
				.Append(scheduler.ProductionBusyUtilizationPercent.ToString("F1"))
				.Append("%")
				.AppendLine();
			stringBuilder.Append("Production busy: ").Append(scheduler.ProductionBusyMilliseconds.ToString("F0")).Append(" ms worker-time")
				.Append(" / ")
				.Append(scheduler.ProductionBusyWindowMilliseconds.ToString("F0"))
				.Append(" ms wall-window")
				.AppendLine();
			stringBuilder.Append("Production queue: pending ").Append(scheduler.ProductionPending).Append("  highWater ")
				.Append(scheduler.ProductionHighWaterPending)
				.Append("  tasks ")
				.Append(scheduler.ProductionCompleted)
				.Append('/')
				.Append(scheduler.ProductionEnqueued)
				.Append("  rejected ")
				.Append(scheduler.ProductionRejected)
				.Append("  failures ")
				.Append(scheduler.ProductionFailures)
				.AppendLine();
		}
		else
		{
			stringBuilder.AppendLine();
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine(PersistentDoBillIndex092.Summary());
		stringBuilder.AppendLine(CleanPathfindingGlowCache093T34D3.Summary());
		stringBuilder.AppendLine("DoBill worker-tail fabric: RETIRED/OFF on T34-A production line.");
		stringBuilder.AppendLine(JobGiverHybridTailS51.Summary());
		stringBuilder.AppendLine(JobGiverSlowSearch0419S.Summary());
		stringBuilder.AppendLine("Stage3 large-set rescue: RETIRED/OFF on T34-A production line.");
		stringBuilder.AppendLine(CommonSenseIngredientExpand092.Summary());
		return stringBuilder.ToString();
	}

	private static void AppendScheduler(StringBuilder sb)
	{
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler != null)
		{
			sb.AppendLine("Scheduler(all incl. diagnostics): logicalProcessors=" + RimMTRuntime.DetectedProcessorCount + ", workers=" + scheduler.WorkerCount + ", pending=" + scheduler.Pending + ", enqueued=" + scheduler.Enqueued + ", completed=" + scheduler.Completed + ", rejected=" + scheduler.Rejected + ", failures=" + scheduler.Failures + ", active=" + scheduler.ActiveWorkers + ", peakActive=" + scheduler.PeakActiveWorkers + ", highWater=" + scheduler.HighWaterPending);
			sb.AppendLine("Production scheduler(excludes self-test): pending=" + scheduler.ProductionPending + ", enqueued=" + scheduler.ProductionEnqueued + ", completed=" + scheduler.ProductionCompleted + ", rejected=" + scheduler.ProductionRejected + ", failures=" + scheduler.ProductionFailures + ", active=" + scheduler.ProductionActiveWorkers + ", peakActive=" + scheduler.ProductionPeakActiveWorkers + ", highWater=" + scheduler.ProductionHighWaterPending + ", parallelBatches=" + scheduler.ProductionParallelBatches + ", busyWorkerMs=" + scheduler.ProductionBusyMilliseconds.ToString("F2") + ", busyWindowMs=" + scheduler.ProductionBusyWindowMilliseconds.ToString("F2") + ", busyAvgWorkers=" + scheduler.ProductionBusyAverageWorkers.ToString("F3") + ", busyUtilization=" + scheduler.ProductionBusyUtilizationPercent.ToString("F2") + "%, frameSamples=" + scheduler.ProductionConcurrencySamples + ", sampledAvgActive=" + scheduler.ProductionAverageActiveWorkers.ToString("F3") + ", sampledUtilization=" + scheduler.ProductionWorkerUtilizationPercent.ToString("F2") + "%");
		}
	}

	private static void AppendLoad(StringBuilder sb)
	{
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		int num = ((scheduler != null) ? AdaptiveLoadBalancer.BackgroundConcurrencyBudget(scheduler.WorkerCount) : 0);
		sb.AppendLine("Load pressure: " + AdaptiveLoadBalancer.Pressure.ToString() + ", source=" + AdaptiveLoadBalancer.SampleSource + ", samples=" + AdaptiveLoadBalancer.SampleCount + ", butterSamples=" + AdaptiveLoadBalancer.ButterFrameSamples + ", EMAms=" + AdaptiveLoadBalancer.EmaTickMs.ToString("F3") + ", P95ms=" + AdaptiveLoadBalancer.Percentile95().ToString("F3") + ", slowRatio=" + (AdaptiveLoadBalancer.RollingSlowRatio * 100.0).ToString("F2") + "%, spikes=" + AdaptiveLoadBalancer.SpikeCount + ", backgroundBudget=" + num + ", offloadPriority=" + AdaptiveLoadBalancer.RecommendedOffloadPriority);
	}

	public static void RunWorkerSelfTest()
	{
		if (!RimMTRuntime.Initialized || RimMTRuntime.Scheduler == null)
		{
			Log.Warning("[RimMT] Worker self-test cannot run because the scheduler is not initialized.");
			return;
		}
		long total = 0L;
		object totalSync = new object();
		Stopwatch stopwatch = Stopwatch.StartNew();
		if (!RimMTRuntime.Scheduler.ParallelFor("diagnostics.selfTest", 0, 4000000, 50000, delegate(int from, int to)
		{
			long num = 0L;
			for (int i = from; i < to; i++)
			{
				num += ((long)i * 31L) ^ (i >> 3);
			}
			lock (totalSync)
			{
				total += num;
			}
		}, delegate
		{
			stopwatch.Stop();
			JobScheduler scheduler = RimMTRuntime.Scheduler;
			Log.Message("[RimMT] Worker self-test passed: logicalProcessors=" + RimMTRuntime.DetectedProcessorCount + ", workers=" + scheduler.WorkerCount + ", elapsedMs=" + stopwatch.ElapsedMilliseconds + ", checksum=" + total + ", enqueued=" + scheduler.Enqueued + ", completed=" + scheduler.Completed + ", failures=" + scheduler.Failures + ", peakActive=" + scheduler.PeakActiveWorkers + ", highWater=" + scheduler.HighWaterPending + ", multiWakeCalls=" + scheduler.MultiWakeCalls + ", parallelBatches=" + scheduler.ParallelBatchesEnqueued + ". Production scheduler counters intentionally exclude this self-test.");
		}, JobPriority.High))
		{
			Log.Warning("[RimMT] Worker self-test was not accepted by the bounded scheduler.");
		}
	}
}
