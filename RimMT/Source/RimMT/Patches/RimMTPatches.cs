using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class RimMTPatches
    {
        internal static void Apply(Harmony harmony)
        {
            SafeFeaturePatch(harmony, "ui.textCache", () => AccessTools.Method(typeof(Text), "CalcHeight", new Type[] { typeof(string), typeof(float) }), typeof(TextMetricCache), nameof(TextMetricCache.CalcHeightPrefix), nameof(TextMetricCache.CalcHeightPostfix));
            SafeFeaturePatch(harmony, "ui.textCache", () => AccessTools.Method(typeof(Text), "CalcSize", new Type[] { typeof(string) }), typeof(TextMetricCache), nameof(TextMetricCache.CalcSizePrefix), nameof(TextMetricCache.CalcSizePostfix));
            SafeFeaturePatch(harmony, "ui.overlayCache", () => AccessTools.Method(typeof(ThingOverlays), "ThingOverlaysOnGUI"), typeof(ThingOverlayCache), nameof(ThingOverlayCache.Prefix), null);
            SafeFeaturePatch(harmony, "ai.reachNoCache", () => AccessTools.Method(typeof(Reachability), "CanReach", new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) }), typeof(ReachabilityNoCache), nameof(ReachabilityNoCache.Prefix), nameof(ReachabilityNoCache.Postfix));

            SafeProbe(harmony, "diagnostics.tick", () => AccessTools.Method(typeof(TickManager), "DoSingleTick"), typeof(HotPathPatches), nameof(HotPathPatches.TickPrefix), nameof(HotPathPatches.TickPostfix));
            SafeProbe(harmony, "diagnostics.pathFinder", () => AccessTools.Method(typeof(PathFinder), "FindPath", new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) }), typeof(HotPathPatches), nameof(HotPathPatches.PathPrefix), nameof(HotPathPatches.PathPostfix));

            Type jobGiverWork = null;
            try
            {
                jobGiverWork = AccessTools.TypeByName("RimWorld.JobGiver_Work");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] diagnostics.jobGiver type lookup failed; JobGiver profiling disabled. " + ex.GetType().Name + ": " + ex.Message);
            }
            PatchAllNamedProbe(harmony, "diagnostics.jobGiver", null, jobGiverWork, "TryIssueJobPackage", typeof(HotPathPatches), nameof(HotPathPatches.JobGiverPrefix), nameof(HotPathPatches.JobGiverPostfix));

            // RimWorld 1.5 has more than one RecalculatePerceivedPathCostAt overload in some builds/modded runtimes.
            // Enumerating declared methods avoids AccessTools.Method(name)-only AmbiguousMatchException and keeps this probe additive.
            int topologyTargets = 0;
            topologyTargets += PatchAllNamedProbe(harmony, "ai.pathTopology", "ai.pathTopology", typeof(PathGrid), "RecalculatePerceivedPathCostAt", typeof(PathGridInvalidation), null, nameof(PathGridInvalidation.Postfix));
            topologyTargets += PatchAllNamedProbe(harmony, "ai.pathTopology", "ai.pathTopology", typeof(PathGrid), "RecalculateAllPerceivedPathCosts", typeof(PathGridInvalidation), null, nameof(PathGridInvalidation.Postfix));
            if (topologyTargets == 0)
            {
                FeatureGate.Suppress("ai.pathTopology", "no compatible PathGrid invalidation targets were patched");
                Log.Warning("[RimMT] ai.pathTopology disabled: no compatible PathGrid invalidation targets were patched.");
            }
        }

        private static void SafeFeaturePatch(Harmony harmony, string featureId, Func<MethodBase> resolver, Type patchType, string prefixName, string postfixName)
        {
            try
            {
                MethodBase target = resolver == null ? null : resolver();
                if (target == null)
                {
                    FeatureGate.Suppress(featureId, "target method was not found for RimWorld 1.5");
                    Log.Warning("[RimMT] " + featureId + " target method not found; feature disabled.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(featureId, target);
                PatchProbe(harmony, target, patchType, prefixName, postfixName);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(featureId, "patch installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] " + featureId + " patch failed; only this feature is disabled. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SafeProbe(Harmony harmony, string label, Func<MethodBase> resolver, Type patchType, string prefixName, string postfixName)
        {
            try
            {
                MethodBase target = resolver == null ? null : resolver();
                if (target == null)
                {
                    Log.Warning("[RimMT] " + label + " target method not found; probe disabled.");
                    return;
                }
                PatchProbe(harmony, target, patchType, prefixName, postfixName);
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] " + label + " probe patch failed; core runtime remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static int PatchAllNamedProbe(Harmony harmony, string label, string featureId, Type targetType, string methodName, Type patchType, string prefixName, string postfixName)
        {
            if (targetType == null)
            {
                if (!string.IsNullOrEmpty(featureId))
                    FeatureGate.Suppress(featureId, "target type was not found");
                Log.Warning("[RimMT] " + label + " target type not found; probe disabled.");
                return 0;
            }

            MethodInfo[] methods;
            try
            {
                methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(featureId))
                    FeatureGate.Suppress(featureId, "target enumeration failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] " + label + " target enumeration failed; core runtime remains active. " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }

            int found = 0;
            int patched = 0;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != methodName)
                    continue;

                found++;
                try
                {
                    PatchProbe(harmony, method, patchType, prefixName, postfixName);
                    patched++;
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimMT] " + label + " could not patch overload " + method + ". Skipping this overload only. " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (found == 0)
                Log.Warning("[RimMT] " + label + " found no method named " + targetType.FullName + "." + methodName + ".");
            else
                Log.Message("[RimMT] " + label + " patched " + patched + "/" + found + " overload(s) of " + targetType.FullName + "." + methodName + ".");

            return patched;
        }

        private static void PatchProbe(Harmony harmony, MethodBase target, Type patchType, string prefixName, string postfixName)
        {
            if (target == null)
                return;

            HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(patchType, prefixName);
            HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(patchType, postfixName);
            harmony.Patch(target, prefix: prefix, postfix: postfix);
        }
    }
}
