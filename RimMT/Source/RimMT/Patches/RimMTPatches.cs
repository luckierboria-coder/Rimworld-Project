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
            Patch(harmony,"ui.textCache",AccessTools.Method(typeof(Text),"CalcHeight",new Type[]{typeof(string),typeof(float)}),typeof(TextMetricCache),nameof(TextMetricCache.CalcHeightPrefix),nameof(TextMetricCache.CalcHeightPostfix));
            Patch(harmony,"ui.textCache",AccessTools.Method(typeof(Text),"CalcSize",new Type[]{typeof(string)}),typeof(TextMetricCache),nameof(TextMetricCache.CalcSizePrefix),nameof(TextMetricCache.CalcSizePostfix));
            Patch(harmony,"ui.overlayCache",AccessTools.Method(typeof(ThingOverlays),"ThingOverlaysOnGUI"),typeof(ThingOverlayCache),nameof(ThingOverlayCache.Prefix),null);
            Patch(harmony,"ai.reachNoCache",AccessTools.Method(typeof(Reachability),"CanReach",new Type[]{typeof(IntVec3),typeof(LocalTargetInfo),typeof(PathEndMode),typeof(TraverseParms)}),typeof(ReachabilityNoCache),nameof(ReachabilityNoCache.Prefix),nameof(ReachabilityNoCache.Postfix));

            PatchProbe(harmony,AccessTools.Method(typeof(TickManager),"DoSingleTick"),typeof(HotPathPatches),nameof(HotPathPatches.TickPrefix),nameof(HotPathPatches.TickPostfix));
            PatchProbe(harmony,AccessTools.Method(typeof(PathFinder),"FindPath",new Type[]{typeof(IntVec3),typeof(LocalTargetInfo),typeof(TraverseParms),typeof(PathEndMode)}),typeof(HotPathPatches),nameof(HotPathPatches.PathPrefix),nameof(HotPathPatches.PathPostfix));
            Type jobGiverWork = AccessTools.TypeByName("RimWorld.JobGiver_Work");
            PatchProbe(harmony,jobGiverWork == null ? null : AccessTools.Method(jobGiverWork,"TryIssueJobPackage"),typeof(HotPathPatches),nameof(HotPathPatches.JobGiverPrefix),nameof(HotPathPatches.JobGiverPostfix));

            PatchProbe(harmony,AccessTools.Method(typeof(PathGrid),"RecalculatePerceivedPathCostAt"),typeof(PathGridInvalidation),null,nameof(PathGridInvalidation.Postfix));
            PatchProbe(harmony,AccessTools.Method(typeof(PathGrid),"RecalculateAllPerceivedPathCosts"),typeof(PathGridInvalidation),null,nameof(PathGridInvalidation.Postfix));
        }
        private static void Patch(Harmony harmony,string featureId,MethodBase target,Type patchType,string prefixName,string postfixName)
        {
            if (target==null) { FeatureGate.Suppress(featureId,"target method was not found for RimWorld 1.5"); Log.Warning("[RimMT] "+featureId+" target method not found; feature disabled."); return; }
            CompatibilityGuard.RegisterTarget(featureId,target); PatchProbe(harmony,target,patchType,prefixName,postfixName);
        }
        private static void PatchProbe(Harmony harmony,MethodBase target,Type patchType,string prefixName,string postfixName)
        {
            if (target==null) return;
            HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(patchType,prefixName);
            HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(patchType,postfixName);
            harmony.Patch(target,prefix:prefix,postfix:postfix);
        }
    }
}
