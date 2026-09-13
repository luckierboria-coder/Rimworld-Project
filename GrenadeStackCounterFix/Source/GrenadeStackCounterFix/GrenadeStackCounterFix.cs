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
            Log.Message("[Grenade Stack Counter Fix] Active for stackable weapons. Map bill counts now sum stackCount instead of Thing instances when product IsWeapon && stackLimit > 1.");
        }
    }

    [HarmonyPatch(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountValidThings))]
    internal static class RecipeWorkerCounter_CountValidThings_Patch
    {
        public static bool Prefix(RecipeWorkerCounter __instance, List<Thing> things, Bill_Production bill, ThingDef def, ref int __result)
        {
            // RimWorld 1.5 assumes non-resource weapons are non-stackable and CountValidThings()
            // therefore increments once per Thing instance. Mods can legitimately make grenades
            // and other weapons stackable, so only that semantic mismatch is corrected here.
            if (__instance == null || bill == null || def == null || !def.IsWeapon || def.stackLimit <= 1)
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
