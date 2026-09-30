using System;
using System.Threading;
using Verse;

namespace RimMT;

internal static class RimMTRuntime
{
	private const string RetiredRegionFeature = "parallel.regionHint";

	private const string RetiredWorkPrefilterFeature = "parallel.workPrefilter";

	private static bool initialized;

	private static bool compatibilityChecked;

	private static JobScheduler scheduler;

	private static long mainThreadFrames;

	private static long butterLogicalTickDrainDeferrals;

	private static long butterProbeFailureDrainDeferrals;

	private static int detectedProcessorCount;

	internal static JobScheduler Scheduler => scheduler;

	internal static bool Initialized => initialized;

	internal static int DetectedProcessorCount => detectedProcessorCount;

	internal static long MainThreadFrames => Interlocked.Read(ref mainThreadFrames);

	internal static long ButterLogicalTickDrainDeferrals => Interlocked.Read(ref butterLogicalTickDrainDeferrals);

	internal static long ButterProbeFailureDrainDeferrals => Interlocked.Read(ref butterProbeFailureDrainDeferrals);

	internal static void Initialize()
	{
		if (!initialized)
		{
			initialized = true;
			RuntimeCompatibility.Initialize();
			detectedProcessorCount = Math.Max(1, Environment.ProcessorCount);
			scheduler = new JobScheduler(Math.Max(1, Math.Min(detectedProcessorCount - 1, 8)), 100000);
			FeatureGate.Register("runtime.scheduler", enabledByDefault: true, "Core bounded worker scheduler");
			FeatureGate.Register("runtime.dispatcher", enabledByDefault: true, "Worker-to-main-thread dispatcher");
			FeatureGate.Register("runtime.adaptiveBurst", enabledByDefault: true, "Rolling pressure-aware scheduler with hysteresis and worker budgets");
			FeatureGate.Register("diagnostics.selfTest", enabledByDefault: true, "On-demand pure CPU worker self-test; excluded from production utilization counters");
			FeatureGate.Register("ui.textCache", enabledByDefault: true, "Text metric result cache");
			FeatureGate.Register("ai.pathTopology", enabledByDefault: true, "PathGrid topology invalidation generation");
			FeatureGate.Register("parallel.jobScan", enabledByDefault: true, "Production haul/work scanner accelerator");
			FeatureGate.Register("parallel.haulGlobal", enabledByDefault: true, "Direct JobGiver_Haul global accelerator");
			FeatureGate.Register("parallel.jobPartition", enabledByDefault: true, "Legacy synchronous candidate/search helpers retained for fallback paths");
			FeatureGate.Register("parallel.candidateFabric", enabledByDefault: true, "T34-A no-wait worker-maintained candidate spatial fabric");
			FeatureGate.Register("parallel.scannerFabric", enabledByDefault: true, "T34-B same-package scanner candidate parallel planning");
			FeatureGate.Register("parallel.candidateClassification", enabledByDefault: true, "T34-C primitive-only parallel candidate classification");
			FeatureGate.Register("parallel.aggressiveScanner", enabledByDefault: true, "T34-D live WorkGiver validator and Reachability worker execution");
			FeatureGate.Register("parallel.engineStage", enabledByDefault: true, "T26 DoSingleTick epoch boundary; T26.1 same-call consumer retired");
			FeatureGate.Register("parallel.workKernel", enabledByDefault: false, "T27/T27.1 speculative source reordering retired in T27.2 after behavior-risk evidence");
			FeatureGate.Register("parallel.workSafety", enabledByDefault: true, "T27.2 one-time WorkGiver safety/API audit; no worker behavior execution");
			FeatureGate.Register("ai.jobSlowSearch", enabledByDefault: true, "Validated slow-search tail rescue");
			FeatureGate.Register("parallel.regionHint", enabledByDefault: false, "Retired: insufficient production yield");
			FeatureGate.Register("parallel.pawnTick", enabledByDefault: false, "Unsafe / not implemented");
			FeatureGate.Register("parallel.reservations", enabledByDefault: false, "Unsafe / not implemented");
			FeatureGate.Register("parallel.thingTick", enabledByDefault: false, "Not implemented");
			FeatureGate.Register("diagnostics.hotPaths", enabledByDefault: false, "External diagnostic layer only in Unified Lean");
			FeatureGate.Register("diagnostics.pathFinder", enabledByDefault: false, "External diagnostic layer only");
			FeatureGate.Register("diagnostics.jobGiver", enabledByDefault: false, "External diagnostic layer only");
			FeatureGate.Register("diagnostics.jobGiverDetail", enabledByDefault: false, "External diagnostic layer only");
			FeatureGate.Register("parallel.pathSnapshot", enabledByDefault: false, "Retired from production: validation-only shadow path");
			FeatureGate.Register("parallel.workPrefilter", enabledByDefault: false, "Retired from production: measured negative ROI");
			FeatureGate.Register("ui.overlayCache", enabledByDefault: false, "Retired from Unified Lean production path");
			FeatureGate.Register("ai.reachNoCache", enabledByDefault: false, "Retired; ReachProfile is the production reachability accelerator");
			ApplySettings(RimMTMod.Settings);
		}
	}

	internal static void ApplySettings(RimMTSettings settings)
	{
		if (initialized && settings != null)
		{
			FeatureGate.SetEnabled("runtime.adaptiveBurst", settings.AdaptiveBurst);
			FeatureGate.SetEnabled("ui.textCache", settings.TextCache);
			bool workScanAcceleration = settings.WorkScanAcceleration;
			FeatureGate.SetEnabled("parallel.jobScan", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.haulGlobal", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.jobPartition", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.candidateFabric", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.scannerFabric", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.candidateClassification", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.aggressiveScanner", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.engineStage", workScanAcceleration);
			FeatureGate.SetEnabled("parallel.workKernel", enabled: false);
			FeatureGate.SetEnabled("parallel.workSafety", workScanAcceleration);
			FeatureGate.SetEnabled("ai.jobSlowSearch", workScanAcceleration);
			JobGiverSlowSearch0419S.SetEnabled(workScanAcceleration);
			FeatureGate.SetEnabled("diagnostics.hotPaths", enabled: false);
			FeatureGate.SetEnabled("diagnostics.pathFinder", enabled: false);
			FeatureGate.SetEnabled("diagnostics.jobGiver", enabled: false);
			FeatureGate.SetEnabled("diagnostics.jobGiverDetail", enabled: false);
			FeatureGate.SetEnabled("parallel.pathSnapshot", enabled: false);
			FeatureGate.SetEnabled("parallel.workPrefilter", enabled: false);
			FeatureGate.SetEnabled("ui.overlayCache", enabled: false);
			FeatureGate.SetEnabled("ai.reachNoCache", enabled: false);
			FeatureGate.SetEnabled("parallel.regionHint", enabled: false);
		}
	}

	internal static void OnMainThreadFrame()
	{
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Invalid comparison between Unknown and I4
		if (!initialized)
		{
			return;
		}
		Interlocked.Increment(ref mainThreadFrames);
		if (scheduler != null)
		{
			scheduler.SampleProductionConcurrency();
		}
		StorytellerDeepAttribution093T18.OnMainThreadFrame();
		bool flag = true;
		bool flag2 = true;
		if (RuntimeCompatibility.ButterPlusPlusActive)
		{
			flag2 = RuntimeCompatibility.TryGetButterLogicalTickInProgress(out var inProgress);
			if (!flag2)
			{
				flag = false;
				Interlocked.Increment(ref butterProbeFailureDrainDeferrals);
			}
			else if (inProgress)
			{
				flag = false;
				Interlocked.Increment(ref butterLogicalTickDrainDeferrals);
			}
		}
		if (flag && FeatureGate.IsEnabled("runtime.dispatcher"))
		{
			MainThreadDispatcher.Drain(256);
		}
		if (!compatibilityChecked && (int)Current.ProgramState == 2 && (flag || (RuntimeCompatibility.ButterPlusPlusActive && !flag2)))
		{
			compatibilityChecked = true;
			CompatibilityGuard.RunBaselineScan();
			HaulWorkAccelerator.MarkCompatibilityReady();
			GlobalHaulAccelerator.MarkCompatibilityReady();
			CandidateFabric093T34A.MarkCompatibilityReady();
			AdaptiveGenClosestAssist.MarkCompatibilityReady();
			Log.Message("[RimMT] Unified Lean compatibility scan complete. Runtime profiling remains external/on-demand.");
			RimMTDiagnostics.LogRuntimeReport();
		}
	}
}
