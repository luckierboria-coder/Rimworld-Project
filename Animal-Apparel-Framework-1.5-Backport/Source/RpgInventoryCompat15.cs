using System.Reflection;
using HarmonyLib;
using Verse;

namespace AnimalGear
{
    [StaticConstructorOnStartup]
    public static class RpgInventoryCompat15
    {
        static RpgInventoryCompat15()
        {
            if (!ModsConfig.IsActive("Sandy.RPGStyleInventory.avilmask.Revamped")) return;
            System.Type tabType = AccessTools.TypeByName("Sandy_Detailed_RPG_Inventory.Sandy_Detailed_RPG_GearTab");
            if (tabType == null)
            {
                Log.Warning("[AAF15] RPG Style Inventory detected but gear tab type was not found; compatibility skipped.");
                return;
            }
            MethodInfo getter = AccessTools.PropertyGetter(tabType, "CanControlColonist") ?? AccessTools.Method(tabType, "get_CanControlColonist");
            if (getter == null)
            {
                Log.Warning("[AAF15] RPG Style Inventory detected but CanControlColonist getter was not found; compatibility skipped.");
                return;
            }
            Harmony harmony = new Harmony("Ingendum.AnimalApparelFramework.Backport15.RPGInventory");
            harmony.Patch(getter, postfix: new HarmonyMethod(typeof(RpgInventoryCompat15), nameof(Postfix)));
            Log.Message("[AAF15] RPG Style Inventory compatibility active.");
        }

        public static void Postfix(object __instance, ref bool __result)
        {
            if (__instance == null || __result) return;
            FieldInfo field = AccessTools.Field(__instance.GetType(), "_cachedPawn") ?? AccessTools.Field(__instance.GetType(), "cachedPawn");
            Pawn pawn = field == null ? null : field.GetValue(__instance) as Pawn;
            if (pawn.IsAnimalOfColony()) __result = true;
        }
    }
}
