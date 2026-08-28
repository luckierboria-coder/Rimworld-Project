using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Allen.AnimalGearNullOutfit15
{
    internal static class AnimalGearHumanGuardUtility
    {
        public static bool IsStrictAnimalApparel(ThingDef def)
        {
            return def != null
                && def.apparel != null
                && def.apparel.tags != null
                && def.apparel.tags.Contains("Animal");
        }

        public static bool IsHumanlike(Pawn pawn)
        {
            return pawn != null && pawn.RaceProps != null && pawn.RaceProps.Humanlike;
        }

        public static void PurgeAnimalApparelFromHuman(Pawn pawn)
        {
            if (!IsHumanlike(pawn) || pawn.apparel == null)
                return;

            List<Apparel> worn = pawn.apparel.WornApparel;
            if (worn == null || worn.Count == 0)
                return;

            for (int i = worn.Count - 1; i >= 0; i--)
            {
                Apparel apparel = worn[i];
                if (apparel == null || !IsStrictAnimalApparel(apparel.def))
                    continue;

                pawn.apparel.Remove(apparel);
                if (!apparel.Destroyed)
                    apparel.Destroy(DestroyMode.Vanish);
            }
        }
    }

    // Hard compatibility guard: strict Animal-tagged apparel is never wearable by Humanlike pawns.
    // This is deliberately narrower than AnimalCompatible/shared apparel and therefore does not
    // interfere with equipment intended to be valid for both humans and animals.
    [HarmonyPatch(typeof(ApparelUtility), "HasPartsToWear")]
    public static class ApparelUtility_HasPartsToWear_AnimalOnlyGuard
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn p, ThingDef apparel, ref bool __result)
        {
            if (!AnimalGearHumanGuardUtility.IsHumanlike(p)
                || !AnimalGearHumanGuardUtility.IsStrictAnimalApparel(apparel))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    // Some modded Pawn generators can bypass normal outfit-tag / wearability checks and directly
    // insert apparel. Patch every 1.5 GenerateStartingApparelFor overload defensively and clean
    // strict Animal-tagged apparel after generation has finished.
    [HarmonyPatch]
    public static class PawnApparelGenerator_StartingApparel_AnimalOnlyGuard
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(PawnApparelGenerator).GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name == "GenerateStartingApparelFor")
                    yield return method;
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(object[] __args)
        {
            if (__args == null)
                return;

            Pawn pawn = null;
            for (int i = 0; i < __args.Length; i++)
            {
                pawn = __args[i] as Pawn;
                if (pawn != null)
                    break;
            }

            if (pawn != null)
                AnimalGearHumanGuardUtility.PurgeAnimalApparelFromHuman(pawn);
        }
    }
}
