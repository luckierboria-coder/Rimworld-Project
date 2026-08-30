using HarmonyLib;
using RimWorld;
using Verse;

namespace AnimalGear
{
    /// <summary>
    /// RimWorld 1.5 Pawn_ApparelTracker.Wear expects the apparel to no longer
    /// belong to another ThingOwner. Our equip-animal job carries the apparel
    /// to the animal first, so remove it from Pawn_CarryTracker before the
    /// animal apparel tracker takes ownership.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Wear))]
    public static class AAF15_EquipTransferFix
    {
        public static void Prefix(Pawn_ApparelTracker __instance, Apparel newApparel)
        {
            if (__instance == null || newApparel == null)
                return;

            Pawn wearer = __instance.pawn;
            if (wearer == null || !wearer.IsAnimal())
                return;

            if (!AnimalGearHelper.IsAnimalApparel(newApparel.def))
                return;

            Pawn_CarryTracker carryTracker = newApparel.ParentHolder as Pawn_CarryTracker;
            if (carryTracker == null || carryTracker.innerContainer == null)
                return;

            // Detach only; Wear() immediately transfers ownership to the
            // animal's Pawn_ApparelTracker. Do not destroy or drop the thing.
            carryTracker.innerContainer.Remove(newApparel);
        }
    }
}
