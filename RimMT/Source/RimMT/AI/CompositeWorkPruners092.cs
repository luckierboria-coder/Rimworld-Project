using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Mature S5.3 candidate pruning integrated into the main assembly without telemetry.
    /// These paths only remove candidates that are provably impossible under reviewed semantics.
    /// Each feature independently fails closed when an unreviewed foreign patch owns authority.
    /// DoBill is intentionally absent here; V0.9.2 uses PersistentDoBillIndex092 instead.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class CompositeWorkPruners092
    {
        private static readonly Thing[] EmptyThings = new Thing[0];
        private static readonly ConditionalWeakTable<Map, TendMapCache> TendCaches = new ConditionalWeakTable<Map, TendMapCache>();
        private static readonly ConditionalWeakTable<Map, SowMapCache> SowCaches = new ConditionalWeakTable<Map, SowMapCache>();

        private static bool buildRoof;
        private static bool tend;
        private static bool harvest;
        private static bool sow;
        private static bool clearSnow;

        static CompositeWorkPruners092()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                InstallBuildRoof(harmony);
                InstallTend(harmony);
                InstallGrowers(harmony);
                InstallClearSnow(harmony);
                if (tend) InstallSharedThingSource(harmony);
                Log.Message("[RimMT] Unified S5.3 pruners: BuildRoof=" + buildRoof + ", Tend=" + tend + ", Harvest=" + harvest + ", Sow=" + sow + ", ClearSnow=" + clearSnow + ". DoBill is handled by the persistent RC2 index.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Unified S5.3 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InstallBuildRoof(Harmony harmony)
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            if (source == null || authority == null || HasUnsafeForeignPatch(authority, null)) return;
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkPruners092), nameof(BuildRoofCellsPostfix)) { priority = Priority.Last });
            buildRoof = true;
        }

        private static void InstallTend(Harmony harmony)
        {
            MethodInfo normal = AccessTools.Method(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            MethodInfo urgent = AccessTools.Method(typeof(WorkGiver_TendOtherUrgent), nameof(WorkGiver_TendOtherUrgent.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            if (normal == null || urgent == null || HasUnsafeForeignPatch(normal, null) || HasUnsafeForeignPatch(urgent, null)) return;
            tend = true;
        }

        private static void InstallGrowers(Harmony harmony)
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            if (source == null) return;

            MethodInfo harvestAuthority = AccessTools.Method(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            if (harvestAuthority != null && !HasUnsafeForeignPatch(harvestAuthority, IsKnownSafeHarvestPatch)) harvest = true;

            MethodInfo sowAuthority = AccessTools.Method(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            MethodInfo wanted = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.CalculateWantedPlantDef), new Type[] { typeof(IntVec3), typeof(Map) });
            if (sowAuthority != null && wanted != null && !HasUnsafeForeignPatch(sowAuthority, null) && !HasUnsafeForeignPatch(wanted, null)) sow = true;

            if (harvest || sow)
                harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkPruners092), nameof(GrowerCellsPostfix)) { priority = Priority.Last });
        }

        private static void InstallClearSnow(Harmony harmony)
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_ClearSnow), nameof(WorkGiver_ClearSnow.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_ClearSnow), nameof(WorkGiver_ClearSnow.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            if (source == null || authority == null || HasUnsafeForeignPatch(authority, null)) return;
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkPruners092), nameof(ClearSnowCellsPostfix)) { priority = Priority.Last });
            clearSnow = true;
        }

        private static void InstallSharedThingSource(Harmony harmony)
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
            if (source == null) { tend = false; return; }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkPruners092), nameof(PotentialWorkThingsGlobalPostfix)) { priority = Priority.Last - 10 });
        }

        public static void BuildRoofCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (!buildRoof || pawn == null || pawn.Map == null || __result == null) return;
            __result = FilterBuildRoof(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterBuildRoof(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 cell in source) if (!cell.Roofed(map)) yield return cell;
        }

        public static void GrowerCellsPostfix(WorkGiver_Grower __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (__instance == null || pawn == null || pawn.Map == null || __result == null) return;
            if (harvest && __instance.GetType() == typeof(WorkGiver_GrowerHarvest))
            {
                __result = FilterHarvest(__result, pawn.Map);
                return;
            }
            if (sow && __instance.GetType() == typeof(WorkGiver_GrowerSow))
                __result = FilterSow(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterHarvest(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 cell in source)
            {
                Plant plant = cell.GetPlant(map);
                if (plant != null && plant.HarvestableNow && plant.LifeStage == PlantLifeStage.Mature && plant.CanYieldNow())
                    yield return cell;
            }
        }

        private static IEnumerable<IntVec3> FilterSow(IEnumerable<IntVec3> source, Map map)
        {
            SowMapCache cache = SowCaches.GetValue(map, delegate(Map m) { return new SowMapCache(); });
            cache.Prepare(CurrentTick());
            foreach (IntVec3 cell in source)
            {
                ThingDef wanted;
                if (!cache.Wanted.TryGetValue(cell, out wanted))
                {
                    wanted = WorkGiver_Grower.CalculateWantedPlantDef(cell, map);
                    cache.Wanted[cell] = wanted;
                }
                if (wanted == null) continue;

                List<Thing> things = cell.GetThingList(map);
                bool samePlant = false;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing != null && thing.def == wanted) { samePlant = true; break; }
                }
                if (!samePlant) yield return cell;
            }
        }

        public static void ClearSnowCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (!clearSnow || pawn == null || pawn.Map == null || __result == null) return;
            __result = FilterSnow(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterSnow(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 cell in source)
                if (map.snowGrid.GetDepth(cell) >= 0.2f) yield return cell;
        }

        public static void PotentialWorkThingsGlobalPostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!tend || __result != null || __instance == null || pawn == null || pawn.Map == null) return;
            WorkGiver_Tend tendGiver = __instance as WorkGiver_Tend;
            if (tendGiver == null) return;
            string typeName = __instance.GetType().Name;
            if (typeName.IndexOf("TendOther", StringComparison.OrdinalIgnoreCase) < 0) return;

            TendMapCache cache = TendCaches.GetValue(pawn.Map, delegate(Map m) { return new TendMapCache(); });
            cache.RefreshIfNeeded(pawn.Map);
            bool urgent = __instance is WorkGiver_TendOtherUrgent || typeName.IndexOf("Urgent", StringComparison.OrdinalIgnoreCase) >= 0;
            bool humanlikeOnly = typeName.IndexOf("Humanlike", StringComparison.OrdinalIgnoreCase) >= 0;
            bool animalOnly = typeName.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasTarget = humanlikeOnly ? (urgent ? cache.UrgentHumanlike : cache.AnyHumanlike)
                : animalOnly ? (urgent ? cache.UrgentAnimal : cache.AnyAnimal)
                : (urgent ? cache.UrgentAny : cache.Any);
            if (!hasTarget) __result = EmptyThings;
        }

        private static bool IsKnownSafeHarvestPatch(Patch patch)
        {
            if (patch == null || patch.PatchMethod == null) return false;
            string typeName = patch.PatchMethod.DeclaringType == null ? string.Empty : patch.PatchMethod.DeclaringType.FullName;
            string methodName = patch.PatchMethod.Name ?? string.Empty;
            return methodName.IndexOf("HasJobOnCellHarvestPostfix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (typeName.IndexOf("AlienRace", StringComparison.OrdinalIgnoreCase) >= 0 && methodName.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasUnsafeForeignPatch(MethodBase target, Func<Patch, bool> safeForeign)
        {
            Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return Check(info.Prefixes, safeForeign) || Check(info.Postfixes, safeForeign) || Check(info.Transpilers, safeForeign) || Check(info.Finalizers, safeForeign);
        }

        private static bool Check(IList<Patch> patches, Func<Patch, bool> safeForeign)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                if (safeForeign != null && safeForeign(patch)) continue;
                return true;
            }
            return false;
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        private sealed class SowMapCache
        {
            private int tick = int.MinValue;
            internal readonly Dictionary<IntVec3, ThingDef> Wanted = new Dictionary<IntVec3, ThingDef>();
            internal void Prepare(int currentTick)
            {
                if (tick == currentTick) return;
                tick = currentTick;
                Wanted.Clear();
            }
        }

        private sealed class TendMapCache
        {
            private int tick = int.MinValue;
            internal bool Any, AnyHumanlike, AnyAnimal, UrgentAny, UrgentHumanlike, UrgentAnimal;

            internal void RefreshIfNeeded(Map map)
            {
                int now = CurrentTick();
                if (tick == now) return;
                tick = now;
                Any = AnyHumanlike = AnyAnimal = UrgentAny = UrgentHumanlike = UrgentAnimal = false;

                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn patient = pawns[i];
                    if (patient == null || patient.Dead) continue;
                    bool normal, urgent;
                    try
                    {
                        normal = HealthAIUtility.ShouldBeTendedNowByPlayer(patient);
                        urgent = normal && HealthAIUtility.ShouldBeTendedNowByPlayerUrgent(patient);
                    }
                    catch { continue; }
                    if (!normal) continue;
                    Any = true;
                    if (patient.RaceProps != null && patient.RaceProps.Humanlike) AnyHumanlike = true;
                    if (patient.RaceProps != null && patient.RaceProps.Animal) AnyAnimal = true;
                    if (!urgent) continue;
                    UrgentAny = true;
                    if (patient.RaceProps != null && patient.RaceProps.Humanlike) UrgentHumanlike = true;
                    if (patient.RaceProps != null && patient.RaceProps.Animal) UrgentAnimal = true;
                }
            }
        }
    }
}
