using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimOPT
{
    internal static class WorkSourcePruners
    {
        private static bool roof, harvest, snow;

        internal static string Status { get { return "sources[roof=" + roof + ", harvest=" + harvest + ", snowOrSand=" + snow + "]"; } }

        internal static void Install(Harmony harmony, bool enableRoof, bool enableHarvest, bool enableSnow)
        {
            MethodInfo roofSource = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo roofAuthority = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            if (enableRoof && roofSource != null && roofAuthority != null && !HasForeign(roofAuthority, null))
            {
                harmony.Patch(roofSource, postfix: new HarmonyMethod(typeof(WorkSourcePruners), nameof(RoofPostfix)) { priority = Priority.Last });
                roof = true;
            }

            MethodInfo growSource = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo harvestAuthority = AccessTools.Method(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            if (enableHarvest && growSource != null && harvestAuthority != null && !HasForeign(harvestAuthority, KnownSafeHarvestPatch))
            {
                harmony.Patch(growSource, postfix: new HarmonyMethod(typeof(WorkSourcePruners), nameof(GrowPostfix)) { priority = Priority.Last });
                harvest = true;
            }

            MethodInfo snowSource = AccessTools.Method(typeof(WorkGiver_ClearSnowOrSand), nameof(WorkGiver_ClearSnowOrSand.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo snowAuthority = AccessTools.Method(typeof(WorkGiver_ClearSnowOrSand), nameof(WorkGiver_ClearSnowOrSand.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            if (enableSnow && snowSource != null && snowAuthority != null && !HasForeign(snowAuthority, null))
            {
                harmony.Patch(snowSource, postfix: new HarmonyMethod(typeof(WorkSourcePruners), nameof(SnowPostfix)) { priority = Priority.Last });
                snow = true;
            }
        }

        public static void RoofPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (roof && pawn != null && pawn.Map != null && __result != null) __result = FilterRoof(__result, pawn.Map);
        }
        private static IEnumerable<IntVec3> FilterRoof(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 cell in source) if (!cell.Roofed(map)) yield return cell;
        }

        public static void GrowPostfix(WorkGiver_Grower __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (harvest && __instance != null && __instance.GetType() == typeof(WorkGiver_GrowerHarvest) && pawn != null && pawn.Map != null && __result != null)
                __result = FilterHarvest(__result, pawn.Map);
        }
        private static IEnumerable<IntVec3> FilterHarvest(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 cell in source)
            {
                Plant plant = cell.GetPlant(map);
                if (plant != null && plant.HarvestableNow && plant.LifeStage == PlantLifeStage.Mature && plant.CanYieldNow()) yield return cell;
            }
        }

        public static void SnowPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (snow && pawn != null && pawn.Map != null && __result != null) __result = FilterSnow(__result, pawn.Map);
        }
        private static IEnumerable<IntVec3> FilterSnow(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 cell in source)
                if (map.snowGrid.GetDepth(cell) >= 0.2f || cell.GetSandDepth(map) >= 0.2f) yield return cell;
        }

        private static bool KnownSafeHarvestPatch(Patch patch)
        {
            if (patch == null || patch.PatchMethod == null) return false;
            string type = patch.PatchMethod.DeclaringType == null ? string.Empty : patch.PatchMethod.DeclaringType.FullName;
            string method = patch.PatchMethod.Name ?? string.Empty;
            return method.IndexOf("HasJobOnCellHarvestPostfix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (type.IndexOf("AlienRace", StringComparison.OrdinalIgnoreCase) >= 0 && method.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasForeign(MethodBase target, Func<Patch, bool> allowed)
        {
            Patches info = Harmony.GetPatchInfo(target);
            return info != null && (Unsafe(info.Prefixes, allowed) || Unsafe(info.Postfixes, allowed) || Unsafe(info.Transpilers, allowed) || Unsafe(info.Finalizers, allowed));
        }
        private static bool Unsafe(IEnumerable<Patch> patches, Func<Patch, bool> allowed)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null || patch.owner == Bootstrap.HarmonyId) continue;
                if (allowed != null && allowed(patch)) continue;
                return true;
            }
            return false;
        }
    }
}
