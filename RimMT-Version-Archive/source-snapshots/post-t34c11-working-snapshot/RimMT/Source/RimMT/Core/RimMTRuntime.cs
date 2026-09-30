using System;
using System.Threading;
using Verse;

namespace RimMT
{
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

        internal static JobScheduler Scheduler { get { return scheduler; } }
        internal static bool Initialized { get { return initialized; } }
        internal static int DetectedProcessorCount { get { return detectedProcessorCount; } }
        internal static long MainThreadFrames { get { return Interlocked.Read(ref mainThreadFrames); } }
        internal static long ButterLogicalTickDrainDeferrals { get { return Interlocked.Read(ref butterLogicalTickDrainDeferrals); } }
        internal static long ButterProbeFailureDrainDeferrals { get { return Interlocked.Read(ref butterProbeFailureDrainDeferrals); } }

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            RuntimeCompatibility.Initialize();

            detectedProcessorCount = Math.Max(1, Environment.ProcessorCount);
            int workers = Math.Max(1, Math.Min(detectedProcessorCount - 1, 8));
            scheduler = new JobScheduler(workers, 100000);

            FeatureGate.Register("runtime.scheduler", true, "Core bounded worker scheduler");
            FeatureGate.Register("runtime.dispatcher", true, "Worker-to-main-thread dispatcher");
            FeatureGate.Register("runtime.adaptiveBurst", true, "Rolling pressure-aware scheduler with hysteresis and worker budgets");
            FeatureGate.Register("diagnostics.selfTest", true, "On-demand pure CPU worker self-test; excluded from production utilization counters");
            FeatureGate.Register("ui.textCache", true, "Text metric result cache");
            FeatureGate.Register("ai.pathTopology", true, "PathGrid topology invalidation generation");
            FeatureGate.Register("parallel.jobScan", true, "Production haul/work scanner accelerator");
            FeatureGate.Register("parallel.haulGlobal", true, "Direct JobGiver_Haul global accelerator");
            FeatureGate.Register("parallel.jobPartition", true, "Legacy synchronous candidate/search helpers retained for fallback paths");
            FeatureGate.Register(CandidateFabric093T34A.FeatureId, true, "T34-A no-wait worker-maintained candidate spatial fabric");
            FeatureGate.Register(ScannerParallelFabric093T34B.FeatureId, true, "T34-B same-package scanner candidate parallel planning");
            FeatureGate.Register(CandidateClassificationFabric093T34C.FeatureId, true, "T34-C primitive-only parallel candidate classification");
            FeatureGate.Register(DoBillParallelReadinessFabric093T34C9.FeatureId, true, "T34-C.9 primitive-only parallel DoBill readiness classification");
            FeatureGate.Register(TargetCountParallelFabric093T34C10.FeatureId, true, "T34-C.10 same-tick primitive target-count product aggregation");
            FeatureGate.Register(HaulToInventoryParallelEligibility093T34C11.FeatureId, true, "T34-C.11 PUAH primitive storage eligibility classification");
            FeatureGate.Register(RootFrameStallCensus093T34C8.FeatureId, true, "Measurement-only Root_Play.Update and outside-root stall census");
            FeatureGate.Register(SimulationEpochCoordinator093T26.FeatureId, true, "T26 DoSingleTick epoch boundary; T26.1 same-call consumer retired");
            FeatureGate.Register(ParallelWorkKernel093T27.FeatureId, false, "T27/T27.1 speculative source reordering retired in T27.2 after behavior-risk evidence");
            FeatureGate.Register(WorkGiverParallelSafety093T27_2.FeatureId, true, "T27.2 one-time WorkGiver safety/API audit; no worker behavior execution");
            FeatureGate.Register(JobGiverSlowSearch0419S.FeatureId, true, "Validated slow-search tail rescue");
            FeatureGate.Register(RetiredRegionFeature, false, "Retired: insufficient production yield");
            FeatureGate.Register("parallel.pawnTick", false, "Unsafe / not implemented");
            FeatureGate.Register("parallel.reservations", false, "Unsafe / not implemented");
            FeatureGate.Register("parallel.thingTick", false, "Not implemented");

            FeatureGate.Register("diagnostics.hotPaths", false, "External diagnostic layer only in Unified Lean");
            FeatureGate.Register("diagnostics.pathFinder", false, "External diagnostic layer only");
            FeatureGate.Register("diagnostics.jobGiver", false, "External diagnostic layer only");
            FeatureGate.Register("diagnostics.jobGiverDetail", false, "External diagnostic layer only");
            FeatureGate.Register("parallel.pathSnapshot", false, "Retired from production: validation-only shadow path");
            FeatureGate.Register(RetiredWorkPrefilterFeature, false, "Retired from production: measured negative ROI");
            FeatureGate.Register("ui.overlayCache", false, "Retired from Unified Lean production path");
            FeatureGate.Register("ai.reachNoCache", false, "Retired; ReachProfile is the production reachability accelerator");

            ApplySettings(RimMTMod.Settings);
        }

        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings == null) return;
            FeatureGate.SetEnabled("runtime.adaptiveBurst", settings.AdaptiveBurst);
            FeatureGate.SetEnabled("ui.textCache", settings.TextCache);

            bool work = settings.WorkScanAcceleration;
            FeatureGate.SetEnabled("parallel.jobScan", work);
            FeatureGate.SetEnabled("parallel.haulGlobal", work);
            FeatureGate.SetEnabled("parallel.jobPartition", work);
            FeatureGate.SetEnabled(CandidateFabric093T34A.FeatureId, work);
            FeatureGate.SetEnabled(ScannerParallelFabric093T34B.FeatureId, work);
            FeatureGate.SetEnabled(CandidateClassificationFabric093T34C.FeatureId, work);
            FeatureGate.SetEnabled(DoBillParallelReadinessFabric093T34C9.FeatureId, work);
            FeatureGate.SetEnabled(TargetCountParallelFabric093T34C10.FeatureId, work);
            FeatureGate.SetEnabled(HaulToInventoryParallelEligibility093T34C11.FeatureId, work);
            FeatureGate.SetEnabled(SimulationEpochCoordinator093T26.FeatureId, work);
            FeatureGate.SetEnabled(ParallelWorkKernel093T27.FeatureId, false);
            FeatureGate.SetEnabled(WorkGiverParallelSafety093T27_2.FeatureId, work);
            FeatureGate.SetEnabled(JobGiverSlowSearch0419S.FeatureId, work);
            JobGiverSlowSearch0419S.SetEnabled(work);

            FeatureGate.SetEnabled("diagnostics.hotPaths", false);
            FeatureGate.SetEnabled("diagnostics.pathFinder", false);
            FeatureGate.SetEnabled("diagnostics.jobGiver", false);
            FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
            FeatureGate.SetEnabled("parallel.pathSnapshot", false);
            FeatureGate.SetEnabled(RetiredWorkPrefilterFeature, false);
            FeatureGate.SetEnabled("ui.overlayCache", false);
            FeatureGate.SetEnabled("ai.reachNoCache", false);
            FeatureGate.SetEnabled(RetiredRegionFeature, false);
        }

        internal static void OnMainThreadFrame()
        {
            if (!initialized) return;
            Interlocked.Increment(ref mainThreadFrames);
            if (scheduler != null) scheduler.SampleProductionConcurrency();
            StorytellerDeepAttribution093T18.OnMainThreadFrame();

            bool logicalTickBoundary = true;
            bool butterProbeReadable = true;
            if (RuntimeCompatibility.ButterPlusPlusActive)
            {
                bool logicalTickInProgress;
                butterProbeReadable = RuntimeCompatibility.TryGetButterLogicalTickInProgress(out logicalTickInProgress);
                if (!butterProbeReadable)
                {
                    logicalTickBoundary = false;
                    Interlocked.Increment(ref butterProbeFailureDrainDeferrals);
                }
                else if (logicalTickInProgress)
                {
                    logicalTickBoundary = false;
                    Interlocked.Increment(ref butterLogicalTickDrainDeferrals);
                }
            }

            if (logicalTickBoundary && FeatureGate.IsEnabled("runtime.dispatcher"))
                MainThreadDispatcher.Drain(256);

            if (!compatibilityChecked && Current.ProgramState == ProgramState.Playing &&
                (logicalTickBoundary || (RuntimeCompatibility.ButterPlusPlusActive && !butterProbeReadable)))
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
}












