using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Production-only Harmony entry points for V0.9.2 Unified Lean.
    /// Per-call profilers, PathSnapshot shadow validation, overlay cache and the retired
    /// Reachability negative cache are intentionally not installed here. Diagnostics live in
    /// optional external modules so the production DLL pays no profiling detour cost.
    /// </summary>
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

                Patch(harmony, target, patchType, prefixName, postfixName);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(featureId, "patch installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] " + featureId + " disabled: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Patch(Harmony harmony, MethodBase target, Type patchType, string prefixName, string postfixName)
        {
            HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(patchType, prefixName);
            HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(patchType, postfixName);
            harmony.Patch(target, prefix: prefix, postfix: postfix);
        }
    }
}


