using System;
using System.Reflection;
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

    [HarmonyPatch]
    public static class EnoughAvailablePatch
    {
        private static readonly Type StockUpUtilityType = AccessTools.TypeByName("SmartMedicine.StockUpUtility");
        private static readonly Type SmartMedicineModType = AccessTools.TypeByName("SmartMedicine.Mod");
        private static readonly MethodInfo StockUpCountMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "StockUpCount", new[] { typeof(Pawn), typeof(ThingDef) });
        private static readonly MethodInfo HasItemCountMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "HasItemCount", new[] { typeof(Pawn), typeof(ThingDef) });
        private static readonly FieldInfo SettingsField = SmartMedicineModType == null ? null : AccessTools.Field(SmartMedicineModType, "settings");

        public static MethodBase TargetMethod()
        {
            return StockUpUtilityType == null
                ? null
                : AccessTools.Method(StockUpUtilityType, "EnoughAvailable", new[] { typeof(ThingDef), typeof(Map) });
        }

        public static bool Prepare()
        {
            if (TargetMethod() == null || StockUpCountMethod == null || HasItemCountMethod == null || SettingsField == null)
            {
                Log.Error("[Smart Medicine StockUp Resource Fix 1.5] Smart Medicine API not found; patch not applied.");
                return false;
            }
            return true;
        }

        public static bool Prefix(ThingDef thingDef, Map map, ref bool __result)
        {
            if (thingDef == null || map == null)
                return true;

            // Keep Smart Medicine's original ResourceCounter path for defs RimWorld actually counts.
            if (thingDef.CountAsResource && thingDef.resourceReadoutPriority != ResourceCountPriority.Uncounted)
                return true;

            object settings = SettingsField.GetValue(null);
            if (settings == null)
                return true;

            FieldInfo stockUpEnoughField = AccessTools.Field(settings.GetType(), "stockUpEnough");
            if (stockUpEnoughField == null)
                return true;

            float enough = (float)stockUpEnoughField.GetValue(settings);
            if (enough == 0f)
            {
                __result = true;
                return false;
            }

            long available = 0L;

            // Match ResourceCounter's "stored resources" meaning without requiring CountAsResource.
            // LWM Deep Storage keeps its contents in normal storage SlotGroups, so HeldThings covers it.
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

                requested += (int)StockUpCountMethod.Invoke(null, new object[] { pawn, thingDef });
                available += (int)HasItemCountMethod.Invoke(null, new object[] { pawn, thingDef });
            }

            __result = available >= requested * (double)enough;
            return false;
        }
    }
}
