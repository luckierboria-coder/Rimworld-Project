using System;
using System.Reflection;
using DubsBadHygiene;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.DBHAnimalWaterSourceFix
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("Allen.DBH.AnimalWaterSourceFix").PatchAll();
            Log.Message("[DBH Animal Water Source Fix] Loaded for RimWorld 1.5.");
        }
    }

    [HarmonyPatch(typeof(JobGiver_DrinkWater), "TryGiveJob", typeof(Pawn))]
    public static class JobGiverDrinkWaterPatch
    {
        private static readonly Type ClosestSanitationType = AccessTools.TypeByName("DubsBadHygiene.ClosestSanitation");
        private static readonly MethodInfo FindBestDrinkMethod = ClosestSanitationType == null
            ? null
            : AccessTools.Method(
                ClosestSanitationType,
                "FindBestDrink",
                new[] { typeof(Pawn), typeof(Pawn), typeof(bool), typeof(float), typeof(int) });

        private static bool reflectionFailed;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            // Narrow fast-path: only animals, only when DBH has already selected an inventory ingest job.
            if (reflectionFailed || pawn == null || pawn.Map == null || pawn.RaceProps == null || !pawn.RaceProps.Animal)
                return;

            if (__result == null || __result.def != JobDefOf.Ingest || !__result.targetA.HasThing)
                return;

            Thing carriedWater = __result.targetA.Thing;
            if (carriedWater == null || pawn.inventory == null || !pawn.inventory.innerContainer.Contains(carriedWater))
                return;

            WaterExt waterExt = carriedWater.def?.GetModExtension<WaterExt>();
            if (waterExt == null || !waterExt.SeekForThirst)
                return;

            Need_Thirst thirst = pawn.needs?.TryGetNeed<Need_Thirst>();
            if (thirst == null || FindBestDrinkMethod == null)
                return;

            try
            {
                bool urgent = thirst.CurLevel <= 0f || pawn.health.hediffSet.HasHediff(DubDef.DBHDehydration, true);
                LocomotionUrgency urgency = urgent ? LocomotionUrgency.Sprint : LocomotionUrgency.Jog;
                float range = GetSearchRange(thirst.CurLevel);

                object result = FindBestDrinkMethod.Invoke(null, new object[] { pawn, pawn, urgent, range, 300 });
                if (!(result is LocalTargetInfo target) || !target.HasThing)
                    return;

                Thing drinkTarget = target.Thing;
                if (drinkTarget == null || drinkTarget.Destroyed || drinkTarget.Map != pawn.Map)
                    return;

                // Mirror DBH's own branch: a target with WaterExt is another ingestible water source;
                // a Thing without WaterExt is treated by DBH as a basin/sanitation drinking fixture.
                if (drinkTarget.def?.GetModExtension<WaterExt>() != null)
                    return;

                Job basinJob = JobMaker.MakeJob(DubDef.DBHDrinkFromBasin, drinkTarget);
                basinJob.locomotionUrgency = urgency;
                __result = basinJob;
            }
            catch (Exception ex)
            {
                // Fail open: keep DBH's original bottle job and disable further reflection attempts.
                reflectionFailed = true;
                Log.ErrorOnce("[DBH Animal Water Source Fix] Could not query DBH FindBestDrink; patch will fail open for this session. " + ex, 194726531);
            }
        }

        private static float GetSearchRange(float thirstLevel)
        {
            if (thirstLevel <= 0.1f)
                return 9999f;
            if (thirstLevel <= 0.2f)
                return 40f;
            if (thirstLevel <= 0.3f)
                return 30f;
            return 20f;
        }
    }
}
