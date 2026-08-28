using System;
using System.Threading;
using Verse;

namespace RimMT
{
    internal static class RimMTRuntime
    {
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

            FeatureGate.Register("runtime.scheduler", true, "Core bounded worker scheduler; semaphore work credits preserve ParallelFor fan-out");
            FeatureGate.Register("runtime.dispatcher", true, "Worker-to-main-thread dispatcher; TickManagerUpdate bracket owns frame-boundary commits");
            FeatureGate.Register("runtime.adaptiveBurst", true, "Pressure-aware scheduler; samples Butter++ TickManagerUpdate slices when Butter++ is active");
            FeatureGate.Register("diagnostics.selfTest", true, "Pure CPU worker self-test");
            FeatureGate.Register("diagnostics.hotPaths", true, "PathFinder / JobGiver / tick hot-path profiler");
            FeatureGate.Register("diagnostics.pathFinder", true, "PathFinder.FindPath overload probes");
            FeatureGate.Register("diagnostics.jobGiver", true, "JobGiver_Work.TryIssueJobPackage probes");
            FeatureGate.Register("diagnostics.jobGiverDetail", false, "Temporary on-demand per-WorkGiver phase capture; no resident detail detours during normal play");
            FeatureGate.Register("ui.textCache", true, "Text metric result cache");
            FeatureGate.Register("ui.overlayCache", true, "Visible Thing overlay scan cache");
            FeatureGate.Register("ai.reachNoCache", false, "Topology-aware short-lived negative reachability cache");
            FeatureGate.Register("ai.pathTopology", true, "PathGrid topology invalidation hooks for reachability generations");
            FeatureGate.Register("parallel.pathSnapshot", true, "Bounded worker-side immutable path parity validation; Vanilla authoritative");
            FeatureGate.Register("parallel.jobScan", true, "V0.4.6 Work scanner accelerator: worker-built hauling spatial index plus main-thread revalidation");
            FeatureGate.Register("parallel.haulGlobal", true, "V0.4.7 direct JobGiver_Haul accelerator for exact ListerHaulables global searches");
            FeatureGate.Register("parallel.jobPartition", true, "V0.4.14 persistent-map-fabric GenClosest accelerator; Vanilla live validation/final authority retained");
            FeatureGate.Register(JobPackageLocalSearch041912.FeatureId, true, "V0.4.19-JS1.2 Lean Pool per-JobPackage pooled HasJobOnThing memo + original JS1 nearest-order reuse");
            FeatureGate.Register(AggressiveReachabilityProfiles.FeatureId, true, "V0.4.16 sampled per-Pawn Region connectivity profiles; bounded-risk CanReach bypass with parity fuse");
            FeatureGate.Register(ParallelRegionConnectivity.FeatureId, false, "RETIRED in JS1.1+: near-zero-yield V0.4.15 RegionHint worker graph");
            FeatureGate.Register(ParallelWorkPrefilter.FeatureId, true, "V0.4.17 worker-side read-only Grower/Harvest/BuildRoof negative prefilter with sampled false-negative fuse");
            FeatureGate.Register("parallel.pawnTick", false, "Unsafe by default; not implemented");
            FeatureGate.Register("parallel.reservations", false, "Unsafe by default; not implemented");
            FeatureGate.Register("parallel.thingTick", false, "Whitelist module not implemented");
            ApplySettings(RimMTMod.Settings);
        }

        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings == null) return;
            FeatureGate.SetEnabled("runtime.adaptiveBurst", settings.AdaptiveBurst);
            FeatureGate.SetEnabled("diagnostics.hotPaths", settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("diagnostics.pathFinder", settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("diagnostics.jobGiver", settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("ui.textCache", settings.TextCache);
            FeatureGate.SetEnabled("ui.overlayCache", settings.OverlayCache);
            FeatureGate.SetEnabled("ai.reachNoCache", settings.ReachNoCache);
            FeatureGate.SetEnabled("parallel.pathSnapshot", settings.PathSnapshotWorker);
            FeatureGate.SetEnabled("parallel.jobScan", settings.WorkScanAcceleration);
            FeatureGate.SetEnabled("parallel.haulGlobal", settings.WorkScanAcceleration);
            FeatureGate.SetEnabled("parallel.jobPartition", settings.WorkScanAcceleration);
            FeatureGate.SetEnabled(JobPackageLocalSearch041912.FeatureId, settings.WorkScanAcceleration);
            FeatureGate.SetEnabled(AggressiveReachabilityProfiles.FeatureId, settings.WorkScanAcceleration);
            FeatureGate.SetEnabled(ParallelRegionConnectivity.FeatureId, false);
            FeatureGate.SetEnabled(ParallelWorkPrefilter.FeatureId, settings.WorkScanAcceleration);
        }

        internal static void OnMainThreadFrame()
        {
            if (!initialized) return;
            Interlocked.Increment(ref mainThreadFrames);

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

            WorkGiverDetailPatches.OnMainThreadFrame();

            bool mayDiagnoseProbeFailure = RuntimeCompatibility.ButterPlusPlusActive && !butterProbeReadable;
            if (!compatibilityChecked && Current.ProgramState == ProgramState.Playing && (logicalTickBoundary || mayDiagnoseProbeFailure))
            {
                compatibilityChecked = true;
                CompatibilityGuard.RunBaselineScan();
                HaulWorkAccelerator.MarkCompatibilityReady();
                GlobalHaulAccelerator.MarkCompatibilityReady();
                AdaptiveGenClosestAssist.MarkCompatibilityReady();
                AggressiveReachabilityProfiles.MarkCompatibilityReady();
                // RegionHint is intentionally retired/off in JS1.1+.
                ParallelWorkPrefilter.MarkCompatibilityReady();
                RimMTDiagnostics.LogStartupReport();
            }
        }
    }
}
