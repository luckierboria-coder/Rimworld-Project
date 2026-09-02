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

            FeatureGate.Register("runtime.scheduler", true, "Core bounded worker scheduler");
            FeatureGate.Register("runtime.dispatcher", true, "Worker-to-main-thread dispatcher");
            FeatureGate.Register("runtime.adaptiveBurst", true, "Pressure-aware scheduler");
            FeatureGate.Register("diagnostics.selfTest", true, "On-demand pure CPU worker self-test; no resident hooks");
            FeatureGate.Register("ui.textCache", true, "Text metric result cache");
            FeatureGate.Register("ai.pathTopology", true, "PathGrid topology invalidation generation");
            FeatureGate.Register("parallel.jobScan", true, "Production haul/work scanner accelerator");
            FeatureGate.Register("parallel.haulGlobal", true, "Direct JobGiver_Haul global accelerator");
            FeatureGate.Register("parallel.jobPartition", true, "Persistent-map search fabric / candidate partition production path");
            FeatureGate.Register(JobGiverSlowSearch0419S.FeatureId, true, "Validated slow-search tail rescue");
            FeatureGate.Register(AggressiveReachabilityProfiles.FeatureId, true, "ReachProfile with rolling mismatch fuse");
            FeatureGate.Register(ParallelRegionConnectivity.FeatureId, false, "Retired: insufficient production yield");
            FeatureGate.Register("parallel.pawnTick", false, "Unsafe / not implemented");
            FeatureGate.Register("parallel.reservations", false, "Unsafe / not implemented");
            FeatureGate.Register("parallel.thingTick", false, "Not implemented");

            // Explicitly register retired instrumentation as OFF so optional reports can explain
            // why it is absent, without ever installing its Harmony hooks.
            FeatureGate.Register("diagnostics.hotPaths", false, "External diagnostic layer only in Unified Lean");
            FeatureGate.Register("diagnostics.pathFinder", false, "External diagnostic layer only");
            FeatureGate.Register("diagnostics.jobGiver", false, "External diagnostic layer only");
            FeatureGate.Register("diagnostics.jobGiverDetail", false, "External diagnostic layer only");
            FeatureGate.Register("parallel.pathSnapshot", false, "Retired from production: validation-only shadow path");
            FeatureGate.Register(ParallelWorkPrefilter.FeatureId, false, "Retired from production: measured negative ROI");
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
            FeatureGate.SetEnabled(JobGiverSlowSearch0419S.FeatureId, work);
            JobGiverSlowSearch0419S.SetEnabled(work);
            FeatureGate.SetEnabled(AggressiveReachabilityProfiles.FeatureId, work);

            // Production-retired modules are hard OFF regardless of legacy settings files.
            FeatureGate.SetEnabled("diagnostics.hotPaths", false);
            FeatureGate.SetEnabled("diagnostics.pathFinder", false);
            FeatureGate.SetEnabled("diagnostics.jobGiver", false);
            FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
            FeatureGate.SetEnabled("parallel.pathSnapshot", false);
            FeatureGate.SetEnabled(ParallelWorkPrefilter.FeatureId, false);
            FeatureGate.SetEnabled("ui.overlayCache", false);
            FeatureGate.SetEnabled("ai.reachNoCache", false);
            FeatureGate.SetEnabled(ParallelRegionConnectivity.FeatureId, false);
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

            if (!compatibilityChecked && Current.ProgramState == ProgramState.Playing &&
                (logicalTickBoundary || (RuntimeCompatibility.ButterPlusPlusActive && !butterProbeReadable)))
            {
                compatibilityChecked = true;
                CompatibilityGuard.RunBaselineScan();
                HaulWorkAccelerator.MarkCompatibilityReady();
                GlobalHaulAccelerator.MarkCompatibilityReady();
                AdaptiveGenClosestAssist.MarkCompatibilityReady();
                AggressiveReachabilityProfiles.MarkCompatibilityReady();
                Log.Message("[RimMT] Unified Lean compatibility scan complete. Runtime profiling remains external/on-demand.");
            }
        }
    }
}
