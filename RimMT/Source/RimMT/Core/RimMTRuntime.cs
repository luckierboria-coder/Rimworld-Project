using System;

namespace RimMT
{
    internal static class RimMTRuntime
    {
        private static bool initialized;

        internal static bool Initialized { get { return initialized; } }
        internal static int DetectedProcessorCount { get { return Math.Max(1, Environment.ProcessorCount); } }

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            FeatureGate.Register("ui.textCache", true, "Validated text measurement cache");
            ApplySettings(RimMTMod.Settings);
        }

        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings == null) return;
            FeatureGate.SetEnabled("ui.textCache", settings.TextCache);
        }
    }
}
