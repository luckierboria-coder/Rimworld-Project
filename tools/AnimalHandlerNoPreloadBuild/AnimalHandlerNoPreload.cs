using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.AnimalHandlerNoPreload
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("allen.animalhandlernopreload").PatchAll();
            Log.Message("[AnimalHandlerNoPreload] Loaded: animal-handler food preload disabled.");
        }
    }

    internal static class AnimalFeedJobUtility
    {
        public static int ImmediateInteractionCount(int vanillaPreloadCount)
        {
            if (vanillaPreloadCount <= 0) return vanillaPreloadCount;
            return Math.Max(1, (vanillaPreloadCount + 3) / 4);
        }

        public static void ConvertPreloadToInteraction(ref Job result, JobDef interactionDef, Thing animal)
        {
            if (result == null || result.def != JobDefOf.TakeInventory) return;

            Thing food = result.targetA.Thing;
            if (food == null || animal == null) return;

            Job interaction = new Job(interactionDef, animal);
            interaction.SetTarget(TargetIndex.C, food);
            interaction.count = ImmediateInteractionCount(result.count);
            interaction.playerForced = result.playerForced;
            result = interaction;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Train), nameof(WorkGiver_Train.JobOnThing))]
    public static class Patch_WorkGiver_Train_JobOnThing
    {
        public static void Postfix(Thing t, ref Job __result)
        {
            AnimalFeedJobUtility.ConvertPreloadToInteraction(ref __result, JobDefOf.Train, t);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Tame), nameof(WorkGiver_Tame.JobOnThing))]
    public static class Patch_WorkGiver_Tame_JobOnThing
    {
        public static void Postfix(Thing t, ref Job __result)
        {
            AnimalFeedJobUtility.ConvertPreloadToInteraction(ref __result, JobDefOf.Tame, t);
        }
    }

    [HarmonyPatch(typeof(JobDriver_InteractAnimal), nameof(JobDriver_InteractAnimal.TryMakePreToilReservations))]
    public static class Patch_InteractAnimal_Reservations
    {
        public static void Postfix(JobDriver_InteractAnimal __instance, bool errorOnFailed, ref bool __result)
        {
            if (!__result) return;

            Job job = __instance.job;
            LocalTargetInfo food = job.GetTarget(TargetIndex.C);
            if (!food.HasThing) return;

            __result = __instance.pawn.Reserve(food, job, 1, job.count, null, errorOnFailed);
        }
    }

    [HarmonyPatch(typeof(JobDriver_InteractAnimal), "MakeNewToils")]
    public static class Patch_InteractAnimal_MakeNewToils
    {
        public static IEnumerable<Toil> Postfix(IEnumerable<Toil> __result, JobDriver_InteractAnimal __instance)
        {
            Job job = __instance.job;
            if (job.GetTarget(TargetIndex.C).HasThing)
            {
                yield return Toils_Goto.GotoThing(TargetIndex.C, PathEndMode.ClosestTouch)
                    .FailOnDespawnedNullOrForbidden(TargetIndex.C);
                yield return Toils_Haul.TakeToInventory(TargetIndex.C, job.count);
            }

            foreach (Toil toil in __result)
                yield return toil;
        }
    }
}
