using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Allen.SmartMedicineStockUpResourceFix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                new Harmony("allen.smartmedicine.stockup.resourcefix15").PatchAll();
                Log.Message("[Smart Medicine StockUp Resource Fix 1.5] Active. Uncounted/non-resource Stock Up items use real stored Thing stacks instead of ResourceCounter.");
            }
            catch (Exception ex)
            {
                Log.Error("[Smart Medicine StockUp Resource Fix 1.5] Failed to install: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(SmartMedicine.StockUpUtility), nameof(SmartMedicine.StockUpUtility.EnoughAvailable), new[] { typeof(ThingDef), typeof(Map) })]
    public static class EnoughAvailablePatch
    {
        public static bool Prefix(ThingDef thingDef, Map map, ref bool __result)
        {
            if (thingDef == null || map == null)
                return true;

            // ResourceCounter is correct for defs RimWorld actually registers as resources.
            // Preserve Smart Medicine's original path for those defs.
            if (thingDef.CountAsResource && thingDef.resourceReadoutPriority != ResourceCountPriority.Uncounted)
                return true;

            float enough = SmartMedicine.Mod.settings.stockUpEnough;
            if (enough == 0f)
            {
                __result = true;
                return false;
            }

            long available = 0L;

            // Match ResourceCounter's storage semantics, but without requiring CountAsResource.
            // SlotGroup.HeldThings includes normal shelves and LWM Deep Storage storage cells.
            var groups = map.haulDestinationManager?.AllGroupsListForReading;
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    SlotGroup group = groups[i];
                    if (group == null)
                        continue;

                    foreach (Thing heldThing in group.HeldThings)
                    {
                        if (heldThing == null)
                            continue;

                        Thing countedThing = heldThing.GetInnerIfMinified();
                        if (countedThing != null && countedThing.def == thingDef && !countedThing.IsNotFresh())
                            available += countedThing.stackCount;
                    }
                }
            }

            long requested = 0L;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn == null || pawn.inventory == null)
                    continue;

                requested += pawn.StockUpCount(thingDef);
                available += pawn.HasItemCount(thingDef);
            }

            __result = available >= requested * (double)enough;
            return false;
        }
    }
}
