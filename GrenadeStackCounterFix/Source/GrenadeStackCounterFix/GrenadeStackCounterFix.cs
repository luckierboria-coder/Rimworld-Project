using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GrenadeStackCounterFix
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string HarmonyId = "allen.grenadestackcounterfix";

        static Bootstrap()
        {
            new Harmony(HarmonyId).PatchAll();
            Log.Message("[Grenade Stack Counter Fix] Active for 4 medieval grenade defs. Map bill counts now sum stackCount instead of Thing instances.");
        }
    }

    [HarmonyPatch(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountValidThings))]
    internal static class RecipeWorkerCounter_CountValidThings_Patch
    {
        private static readonly HashSet<string> TargetDefs = new HashSet<string>
        {
            "DankPyon_Weapon_PotFire",
            "DankPyon_Weapon_PotFlash",
            "DankPyon_Weapon_PotFlashSmoke",
            "DankPyon_Weapon_AcidFlask"
        };

        public static bool Prefix(RecipeWorkerCounter __instance, List<Thing> things, Bill_Production bill, ThingDef def, ref int __result)
        {
            if (__instance == null || bill == null || def == null || !TargetDefs.Contains(def.defName))
                return true;

            if (things == null || things.Count == 0)
            {
                __result = 0;
                return false;
            }

            int count = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing != null && __instance.CountValidThing(thing, bill, def))
                    count += thing.stackCount;
            }

            __result = count;
            return false;
        }
    }
}
