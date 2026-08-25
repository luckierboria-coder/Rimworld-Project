using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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

            // Path diagnostics run first so foreign prefixes that short-circuit vanilla FindPath
            // cannot hide real path requests from RimMT telemetry.
            int pathFinderTargets = PatchAllNamedProbe(harmony, "diagnostics.pathFinder", typeof(PathFinder), "FindPath", typeof(HotPathPatches), nameof(HotPathPatches.PathPrefix), nameof(HotPathPatches.PathPostfix), true, Priority.First);
            if (pathFinderTargets == 0)
            {
                FeatureGate.Suppress("diagnostics.pathFinder", "no compatible PathFinder.FindPath overloads were patched");
                Log.Warning("[RimMT] diagnostics.pathFinder disabled: no compatible PathFinder.FindPath overloads were patched.");
            }

            Type jobGiverWork = null;
            try
            {
                jobGiverWork = AccessTools.TypeByName("RimWorld.JobGiver_Work");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] diagnostics.jobGiver type lookup failed; JobGiver profiling disabled. " + ex.GetType().Name + ": " + ex.Message);
            }

            int jobGiverTargets = PatchAllNamedProbe(harmony, "diagnostics.jobGiver", jobGiverWork, "TryIssueJobPackage", typeof(HotPathPatches), nameof(HotPathPatches.JobGiverPrefix), nameof(HotPathPatches.JobGiverPostfix), true);
            if (jobGiverTargets == 0)
            {
                FeatureGate.Suppress("diagnostics.jobGiver", "no compatible JobGiver_Work.TryIssueJobPackage overloads were patched");
                Log.Warning("[RimMT] diagnostics.jobGiver disabled: no compatible TryIssueJobPackage overloads were patched.");
            }

            int topologyTargets = 0;
            topologyTargets += PatchAllNamedProbe(harmony, "ai.pathTopology", typeof(PathGrid), "RecalculatePerceivedPathCostAt", typeof(PathGridInvalidation), null, nameof(PathGridInvalidation.Postfix), true);
            topologyTargets += PatchAllNamedProbe(harmony, "ai.pathTopology", typeof(PathGrid), "RecalculateAllPerceivedPathCosts", typeof(PathGridInvalidation), null, nameof(PathGridInvalidation.Postfix), true);
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

        private static int PatchAllNamedProbe(Harmony harmony, string label, Type targetType, string methodName, Type patchType, string prefixName, string postfixName, bool logSignatures, int prefixPriority = Priority.Normal)
        {
            if (targetType == null)
            {
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
                if (logSignatures)
                    Log.Message("[RimMT] " + label + " target overload: " + method);

                try
                {
                    PatchProbe(harmony, method, patchType, prefixName, postfixName, prefixPriority);
                    patched++;
                    if (label == "diagnostics.pathFinder")
                        Log.Message("[RimMT] diagnostics.pathFinder Harmony chain: " + method + " :: " + DescribeHarmonyChain(method));
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

        private static void PatchProbe(Harmony harmony, MethodBase target, Type patchType, string prefixName, string postfixName, int prefixPriority = Priority.Normal)
        {
            if (target == null)
                return;

            HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(patchType, prefixName);
            if (prefix != null)
                prefix.priority = prefixPriority;
            HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(patchType, postfixName);
            harmony.Patch(target, prefix: prefix, postfix: postfix);
        }

        private static string DescribeHarmonyChain(MethodBase target)
        {
            Patches patches = Harmony.GetPatchInfo(target);
            if (patches == null)
                return "no Harmony patches";

            StringBuilder sb = new StringBuilder();
            AppendPatchGroup(sb, "PRE", patches.Prefixes);
            AppendPatchGroup(sb, "POST", patches.Postfixes);
            AppendPatchGroup(sb, "TRANS", patches.Transpilers);
            AppendPatchGroup(sb, "FINAL", patches.Finalizers);
            return sb.Length == 0 ? "no Harmony patches" : sb.ToString();
        }

        private static void AppendPatchGroup(StringBuilder sb, string label, IList<Patch> patches)
        {
            if (patches == null || patches.Count == 0)
                return;
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(label).Append(": ");
            for (int i = 0; i < patches.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Patch patch = patches[i];
                sb.Append(patch.owner).Append("@").Append(patch.priority);
            }
        }
    }
}
