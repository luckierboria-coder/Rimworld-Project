using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

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
            // AdaptiveBurst is production behavior, not diagnostics. Keep one minimal DoSingleTick
            // observer so the scheduler has a real pressure signal when Butter++ is absent. This is
            // intentionally separate from the retired HotPathPatches profiler.
            TryPatchAdaptiveTickSampler(harmony);

            SafeFeaturePatch(harmony, "ui.textCache",
                () => AccessTools.Method(typeof(Text), "CalcHeight", new Type[] { typeof(string), typeof(float) }),
                typeof(TextMetricCache), nameof(TextMetricCache.CalcHeightPrefix), nameof(TextMetricCache.CalcHeightPostfix));

            SafeFeaturePatch(harmony, "ui.textCache",
                () => AccessTools.Method(typeof(Text), "CalcSize", new Type[] { typeof(string) }),
                typeof(TextMetricCache), nameof(TextMetricCache.CalcSizePrefix), nameof(TextMetricCache.CalcSizePostfix));

            int topologyTargets = 0;
            topologyTargets += PatchAllNamed(harmony, "ai.pathTopology", typeof(PathGrid),
                "RecalculatePerceivedPathCostAt", typeof(PathGridInvalidation), null, nameof(PathGridInvalidation.Postfix));
            topologyTargets += PatchAllNamed(harmony, "ai.pathTopology", typeof(PathGrid),
                "RecalculateAllPerceivedPathCosts", typeof(PathGridInvalidation), null, nameof(PathGridInvalidation.Postfix));

            if (topologyTargets == 0)
                FeatureGate.Suppress("ai.pathTopology", "no compatible PathGrid invalidation targets were patched");
        }

        private static void TryPatchAdaptiveTickSampler(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(TickManager), "DoSingleTick");
                if (target == null)
                {
                    FeatureGate.Suppress("runtime.adaptiveBurst", "TickManager.DoSingleTick was not found");
                    return;
                }

                // Observational only: do not register this target with CompatibilityGuard. Foreign
                // prefixes/postfixes/transpilers remain authoritative; RimMT only measures elapsed time.
                HarmonyMethod prefix = new HarmonyMethod(typeof(RimMTPatches), nameof(AdaptiveTickPrefix))
                {
                    priority = Priority.First
                };
                HarmonyMethod postfix = new HarmonyMethod(typeof(RimMTPatches), nameof(AdaptiveTickPostfix))
                {
                    priority = Priority.Last
                };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.adaptiveBurst", "DoSingleTick pressure sampler install failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] runtime.adaptiveBurst pressure sampler disabled: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void AdaptiveTickPrefix(ref long __state)
        {
            __state = 0L;
            if (!FeatureGate.IsEnabled("runtime.adaptiveBurst") || RuntimeCompatibility.ButterPlusPlusActive)
                return;
            __state = Stopwatch.GetTimestamp();
        }

        public static void AdaptiveTickPostfix(long __state)
        {
            if (__state != 0L)
                AdaptiveLoadBalancer.RecordTick(__state);
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

                CompatibilityGuard.RegisterTarget(featureId, target);
                Patch(harmony, target, patchType, prefixName, postfixName);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(featureId, "patch installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] " + featureId + " disabled: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int PatchAllNamed(Harmony harmony, string featureId, Type targetType, string methodName,
            Type patchType, string prefixName, string postfixName)
        {
            if (targetType == null) return 0;
            int patched = 0;
            try
            {
                MethodInfo[] methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.Name != methodName) continue;
                    Patch(harmony, method, patchType, prefixName, postfixName);
                    CompatibilityGuard.RegisterTarget(featureId, method);
                    patched++;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] " + featureId + " partial install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return patched;
        }

        private static void Patch(Harmony harmony, MethodBase target, Type patchType, string prefixName, string postfixName)
        {
            HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(patchType, prefixName);
            HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(patchType, postfixName);
            harmony.Patch(target, prefix: prefix, postfix: postfix);
        }
    }
}
