using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class RimMTPatches
    {
        internal static void Apply(Harmony harmony)
        {
            SafeFeaturePatch(harmony, "ui.textCache",
                () => AccessTools.Method(typeof(Text), "CalcHeight", new Type[] { typeof(string), typeof(float) }),
                typeof(TextMetricCache), nameof(TextMetricCache.CalcHeightPrefix), nameof(TextMetricCache.CalcHeightPostfix));

            SafeFeaturePatch(harmony, "ui.textCache",
                () => AccessTools.Method(typeof(Text), "CalcSize", new Type[] { typeof(string) }),
                typeof(TextMetricCache), nameof(TextMetricCache.CalcSizePrefix), nameof(TextMetricCache.CalcSizePostfix));
        }

        private static void SafeFeaturePatch(Harmony harmony, string featureId, Func<MethodBase> resolver,
            Type patchType, string prefixName, string postfixName)
        {
            try
            {
                MethodBase target = resolver == null ? null : resolver();
                if (target == null)
                {
                    FeatureGate.Suppress(featureId, "target method was not found for RimWorld 1.5");
                    return;
                }

                HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(patchType, prefixName);
                HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(patchType, postfixName);
                harmony.Patch(target, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(featureId, "patch installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] " + featureId + " disabled: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
