using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimMTS53Composite
{
    internal static partial class CompositeOptimizerS53
    {
        private static readonly ConditionalWeakTable<Map, SowMapCache> SowCaches = new ConditionalWeakTable<Map, SowMapCache>();

        public static void BuildRoofCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (!BuildRoof.Enabled || pawn == null || pawn.Map == null || __result == null) return;
            if (BuildRoof.ShouldParityBypass()) return;
            __result = FilterBuildRoof(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterBuildRoof(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 c in source)
            {
                BuildRoof.Seen();
                if (!c.Roofed(map))
                {
                    BuildRoof.Kept();
                    yield return c;
                }
                else BuildRoof.Pruned();
            }
        }

        public static void GrowerCellsPostfix(WorkGiver_Grower __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (__instance == null || pawn == null || pawn.Map == null || __result == null) return;
            if (__instance.GetType() == typeof(WorkGiver_GrowerHarvest) && Harvest.Enabled)
            {
                if (!Harvest.ShouldParityBypass()) __result = FilterHarvest(__result, pawn.Map);
                return;
            }
            if (__instance.GetType() == typeof(WorkGiver_GrowerSow) && Sow.Enabled)
            {
                if (!Sow.ShouldParityBypass()) __result = FilterSow(__result, pawn.Map);
            }
        }

        private static IEnumerable<IntVec3> FilterHarvest(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 c in source)
            {
                Harvest.Seen();
                Plant plant = c.GetPlant(map);
                if (plant != null && plant.HarvestableNow && plant.LifeStage == PlantLifeStage.Mature && plant.CanYieldNow())
                {
                    Harvest.Kept();
                    yield return c;
                }
                else Harvest.Pruned();
            }
        }

        private static IEnumerable<IntVec3> FilterSow(IEnumerable<IntVec3> source, Map map)
        {
            SowMapCache cache = SowCaches.GetValue(map, delegate(Map m) { return new SowMapCache(); });
            cache.Prepare(CurrentTick());
            foreach (IntVec3 c in source)
            {
                Sow.Seen();
                ThingDef wanted;
                if (!cache.Wanted.TryGetValue(c, out wanted))
                {
                    wanted = WorkGiver_Grower.CalculateWantedPlantDef(c, map);
                    cache.Wanted[c] = wanted;
                    Sow.IndexBuild();
                }
                if (wanted == null)
                {
                    Sow.Pruned();
                    continue;
                }

                List<Thing> things = c.GetThingList(map);
                bool samePlantPresent = false;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing != null && thing.def == wanted)
                    {
                        samePlantPresent = true;
                        break;
                    }
                }
                if (samePlantPresent)
                {
                    Sow.Pruned();
                    continue;
                }

                Sow.Kept();
                yield return c;
            }
        }

        public static void ClearSnowCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (!ClearSnow.Enabled || pawn == null || pawn.Map == null || __result == null) return;
            if (ClearSnow.ShouldParityBypass()) return;
            __result = FilterSnow(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterSnow(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 c in source)
            {
                ClearSnow.Seen();
                if (map.snowGrid.GetDepth(c) >= 0.2f)
                {
                    ClearSnow.Kept();
                    yield return c;
                }
                else ClearSnow.Pruned();
            }
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
    }
}
