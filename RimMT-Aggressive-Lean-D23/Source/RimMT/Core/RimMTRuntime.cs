using System;
using System.Threading;

namespace RimMT
{
    internal static class RimMTRuntime
    {
        private static bool initialized;
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
            scheduler = new JobScheduler(Math.Max(1, Math.Min(detectedProcessorCount - 1, 8)), 100000);

            FeatureGate.Register("runtime.scheduler", true, "Core bounded worker scheduler");
            FeatureGate.Register("runtime.dispatcher", true, "Worker-to-main-thread dispatcher");
            FeatureGate.Register("runtime.adaptiveBurst", true, "Pressure-aware worker budget");
            FeatureGate.Register("diagnostics.selfTest", true, "On-demand worker self-test");
            FeatureGate.Register("ui.textCache", true, "Text metric cache");
            FeatureGate.Register(AggressiveParallelScanner093T34D.FeatureId, true, "T34-D live validator and reachability worker execution");
            ApplySettings(RimMTMod.Settings);
        }

        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings == null) return;
            FeatureGate.SetEnabled("runtime.adaptiveBurst", settings.AdaptiveBurst);
            FeatureGate.SetEnabled("ui.textCache", settings.TextCache);
            FeatureGate.SetEnabled(AggressiveParallelScanner093T34D.FeatureId, settings.WorkScanAcceleration);
        }

        internal static void OnMainThreadFrame()
        {
            if (!initialized) return;
            Interlocked.Increment(ref mainThreadFrames);
            if (scheduler != null) scheduler.SampleProductionConcurrency();

            bool logicalTickBoundary = true;
            if (RuntimeCompatibility.ButterPlusPlusActive)
            {
                bool logicalTickInProgress;
                if (!RuntimeCompatibility.TryGetButterLogicalTickInProgress(out logicalTickInProgress))
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
        }
    }
}
