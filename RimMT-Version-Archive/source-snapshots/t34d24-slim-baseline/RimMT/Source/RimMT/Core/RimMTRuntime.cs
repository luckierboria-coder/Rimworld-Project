using System;

namespace RimMT
{
    internal static class RimMTRuntime
    {
        private static bool initialized;
        private static int detectedProcessorCount;

        internal static bool Initialized { get { return initialized; } }
        internal static int DetectedProcessorCount { get { return detectedProcessorCount; } }
        internal static long MainThreadFrames { get { return 0L; } }
        internal static long ButterLogicalTickDrainDeferrals { get { return 0L; } }
        internal static long ButterProbeFailureDrainDeferrals { get { return 0L; } }

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            detectedProcessorCount = Math.Max(1, Environment.ProcessorCount);

            FeatureGate.Register("ui.textCache", true, "Text metric result cache");
            FeatureGate.Register(JobGiverSlowSearch0419S.FeatureId, true,
                "Targeted package-local slow-search negative filter");

            ApplySettings(RimMTMod.Settings);
        }

        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings == null) return;
            FeatureGate.SetEnabled("ui.textCache", settings.TextCache);
            FeatureGate.SetEnabled(JobGiverSlowSearch0419S.FeatureId, settings.WorkScanAcceleration);
            JobGiverSlowSearch0419S.SetEnabled(settings.WorkScanAcceleration);
        }

        // Legacy call surface retained for optional diagnostics compiled against older RimMT.
        internal static void OnMainThreadFrame()
        {
        }
    }
}
