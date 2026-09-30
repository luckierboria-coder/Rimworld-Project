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
		stringBuilder.AppendLine("T27/T27.1 speculative work-plan kernel: RETIRED/OFF in T27.2; no source reordering is installed on production JobGiver/GenClosest paths.");
		stringBuilder.AppendLine(WorkGiverParallelSafety093T27_2.Summary());
		stringBuilder.AppendLine(WorkGiverParallelSafety093T27_2.ApiMatrixSummary());
		stringBuilder.AppendLine(WorldTailBoundary093T22.Summary());
		stringBuilder.AppendLine(WorldRootAttribution093T22.Summary());
		stringBuilder.AppendLine(SMFDispatcherCoexistence093T16.Summary());
		stringBuilder.AppendLine(StorytellerCatastrophic093T15.Summary());
		stringBuilder.AppendLine(StorytellerCatastrophic093T15.HarmonyCensus());
		stringBuilder.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
		stringBuilder.AppendLine("Production policy: diagnostics=external; PathSnapshot=OFF; WorkPrefilter=OFF; ReachNoCache=OFF; OverlayCache=OFF; S5.1 admission=16ms; V0.9.3-T34C.4 Refuel Eligibility Shadow Census; baseline=V0.9.3 Consolidated Stable; T0 histogram + T1 top-level + T2 bounded Pawn/AI + T3 bounded FindPath attribution; T4 package-local HaulMerge partner index; T5 Harmony authority census=measurement-only; T8 production behavior retained + T13/T14 attribution retained + T15 one-shot bounded lifecycle + T16 caller/source-shape attribution retained; T17 HaulUrgently package-local memo disabled after runtime evidence showed cacheHits=0; T18 reachable mobile rescue and T19 diagnostics retained; T20 generic validator transaction retained; T21 package-local CanReach chain-aware replay retained; T22 Foundation III repeated-IList GenClosest and Root_Play.Update SMF bridge retained; T24.1 generic-Def safety retained; T26.1 zero-wait/FightFires behavior retained; T27.4 retains the T27.2/T27.3 behavior resets, but removes the T27.3 Wait tracer from RimMT.dll. New profiling belongs to optional allen.rimmt.diagnostics; legacy observability remains only where older production paths still consume it. The WorkGiver safety registry remains audit-only; FullParallel stays hard-OFF; T25 next-call async candidate cache/ThinkNode root attribution are absent; any foreign transpiler/finalizer, __runOriginal-reading postfix, unknown ordering, parity mismatch or fingerprint mutation fails open; no Job/JobOnThing/reservation/priority/cross-package result is cached; SMF dispatcher drain is bridged at Root_Play.Update and no longer claims TickManagerUpdate; Storyteller >=100ms recorder reuses T1 elapsed time; no T9/T10/T11/T12 probe chain; T6 HaulMerge coexistence=ABSENT; S4 generic early rescue=OFF + targeted-safe early rescue=8ms; S4 tail=32ms + proven-heavy early rescue=OFF + true-validator attribution + authority-safe corpse/holding/feed/visit pruners; HaulMerge partner-index negative proof active; V25 validator memo=OFF; RC2 Stage3=OFF(T34-A); DoBill=persistent incremental membership + T28 package-local false readiness proof; DoBillWorkerTail=OFF(T34-A); ReachProfile=OFF(T34-A); global fuse=OFF; S5.3 mature pruners; CommonSense=ingredientExpand memo only.");
		stringBuilder.AppendLine("--- Production path counters ---");
		stringBuilder.AppendLine(ScannerParallelFabric093T34B.Summary());
		stringBuilder.AppendLine(AggressiveParallelScanner093T34D.Summary());
		stringBuilder.AppendLine(CleanPathfindingGlowCache093T34D3.Summary());
		stringBuilder.AppendLine(CandidateClassificationFabric093T34C.Summary());
		stringBuilder.AppendLine(CandidateFabric093T34A.Summary());
		stringBuilder.AppendLine(PersistentMapSearchFabric.Summary());
		stringBuilder.AppendLine("T34-C.4 candidate classification=ACTIVE(root-independent SourceSnapshot+Kernel plans indexed by stable SourceIndex + live parity + Refuel/Refuel_Turret measurement-only shadow census + bounded scanner-miss evidence); Refuel facts never reject candidates; invalid/duplicate indices and unknown Harmony owners fail open; T34-B distance planning and T34-A synchronous fallback remain active.");
		stringBuilder.AppendLine(JobGiverHybridTailS51.Summary());
		stringBuilder.AppendLine(JobGiverSlowSearch0419S.Summary());
		stringBuilder.AppendLine("Stage3 large-set rescue: RETIRED/OFF on T34-A production line.");
		stringBuilder.AppendLine(PersistentDoBillIndex092.Summary());
		stringBuilder.AppendLine(CleanPathfindingGlowCache093T34D3.Summary());
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
