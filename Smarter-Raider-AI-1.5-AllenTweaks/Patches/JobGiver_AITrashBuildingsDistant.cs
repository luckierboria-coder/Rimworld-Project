using HarmonyLib;
using RimWorld;
using System.Linq;
using Verse;
using Verse.AI;
using static PogoAI.Patches.JobGiver_AISapper;

namespace PogoAI.Patches
{
    [HarmonyPatch(typeof(RimWorld.JobGiver_AITrashBuildingsDistant), "TryGiveJob")]
    static class JobGiver_AITrashBuildingsDistant_TryGiveJob_Patch
    {
        static bool Prefix(Pawn pawn, ref Job __result)
        {
            // Allen job-loop fix:
            // Never inject a sapper/follow job into a pawn while Giddy-Up (or another
            // riding system using the same JobDef name) is in the middle of Mount.
            // The log showed Follow being started while Mount was still active.
            if (JobLoopGuard.IsMountTransition(pawn))
            {
                __result = null;
                return false;
            }

            // If this pawn very recently received an invalid SRAI-generated job,
            // let vanilla handle this node briefly instead of rebuilding the same
            // immediately-failing job every tick.
            if (JobLoopGuard.UseVanillaFallbackForNow(pawn))
            {
                return true;
            }

            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map == null || pawn.Faction?.def == null)
            {
                return true;
            }

            if (pawn.mindState?.duty?.def != DutyDefOf.AssaultColony
                || pawn.Faction.def.techLevel < Init.settings.minSmartTechLevel
                || !Init.settings.everyRaidSaps
                || pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn)
                    .Count(x => !x.ThreatDisabled(pawn) && !x.Thing.Destroyed && x.Thing.Faction == Faction.OfPlayer) == 0)
            {
                return true;
            }

            // Upstream discarded this return value unconditionally. If the inner
            // AISapper prefix asks Harmony to run vanilla, propagate that request.
            bool runOriginal = JobGiver_AISapper_TryGiveJob_Patch.Prefix(pawn, ref __result);
            if (runOriginal)
            {
                return true;
            }

            JobLoopDecision decision = JobLoopGuard.ValidateGeneratedJob(pawn, ref __result);
            if (decision == JobLoopDecision.FallbackVanilla)
            {
                JobLoopGuard.BeginVanillaFallback(pawn);
                return true;
            }

            // Suppress means an equivalent SRAI job is already running; do not
            // start another copy. Keep means the generated job is valid.
            return false;
        }
    }
}
