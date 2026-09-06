using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace Allen.SmarterRaiderAI.Patch15
{
    internal enum JobLoopDecision
    {
        Keep,
        Suppress,
        FallbackVanilla
    }

    internal static class JobLoopGuard
    {
        private const int InvalidJobFallbackTicks = 30;
        private const int RepeatWindowTicks = 10;
        private const int RepeatThreshold = 3;

        private static readonly Dictionary<Pawn, int> vanillaFallbackUntilTick = new Dictionary<Pawn, int>();
        private static readonly Dictionary<Pawn, RepeatStamp> repeatStamps = new Dictionary<Pawn, RepeatStamp>();

        private sealed class RepeatStamp
        {
            public JobDef def;
            public int targetThingId;
            public int lastTick;
            public int count;
        }

        internal static bool IsMountTransition(Pawn pawn)
        {
            JobDef currentDef = pawn?.CurJobDef;
            return currentDef != null && string.Equals(currentDef.defName, "Mount", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool UseVanillaFallbackForNow(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            if (!vanillaFallbackUntilTick.TryGetValue(pawn, out int untilTick))
            {
                return false;
            }

            int now = Find.TickManager?.TicksGame ?? 0;
            if (pawn.Destroyed || now >= untilTick)
            {
                vanillaFallbackUntilTick.Remove(pawn);
                return false;
            }

            return true;
        }

        internal static void BeginVanillaFallback(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }

            int now = Find.TickManager?.TicksGame ?? 0;
            vanillaFallbackUntilTick[pawn] = now + InvalidJobFallbackTicks;
            repeatStamps.Remove(pawn);
        }

        internal static JobLoopDecision ValidateGeneratedJob(Pawn pawn, ref Job job)
        {
            if (job == null)
            {
                return JobLoopDecision.Keep;
            }

            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map == null)
            {
                job = null;
                return JobLoopDecision.FallbackVanilla;
            }

            if (IsMountTransition(pawn))
            {
                job = null;
                return JobLoopDecision.Suppress;
            }

            if (IsSameCurrentJob(pawn, job))
            {
                job = null;
                return JobLoopDecision.Suppress;
            }

            if (job.def == JobDefOf.Follow)
            {
                Pawn followTarget = job.targetA.Thing as Pawn;
                if (followTarget == null
                    || followTarget.Destroyed
                    || !followTarget.Spawned
                    || followTarget.Downed
                    || followTarget.Map != pawn.Map
                    || !pawn.CanReach((LocalTargetInfo)followTarget, PathEndMode.Touch, Danger.Deadly))
                {
                    job = null;
                    return JobLoopDecision.FallbackVanilla;
                }
            }
            else if (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.Mine)
            {
                Thing target = job.targetA.Thing;
                if (target == null
                    || target.Destroyed
                    || !target.Spawned
                    || target.Map != pawn.Map
                    || !pawn.CanReach((LocalTargetInfo)target, PathEndMode.Touch, Danger.Deadly))
                {
                    job = null;
                    return JobLoopDecision.FallbackVanilla;
                }
            }

            if (IsRapidRepeat(pawn, job))
            {
                job = null;
                return JobLoopDecision.FallbackVanilla;
            }

            return JobLoopDecision.Keep;
        }

        private static bool IsSameCurrentJob(Pawn pawn, Job candidate)
        {
            Job current = pawn.CurJob;
            if (current == null || current.def != candidate.def)
            {
                return false;
            }

            Thing candidateTarget = candidate.targetA.Thing;
            return candidateTarget != null && current.targetA.Thing == candidateTarget;
        }

        private static bool IsRapidRepeat(Pawn pawn, Job candidate)
        {
            if (candidate.def != JobDefOf.Follow && candidate.def != JobDefOf.AttackMelee && candidate.def != JobDefOf.Mine)
            {
                return false;
            }

            Thing target = candidate.targetA.Thing;
            if (target == null)
            {
                return false;
            }

            int now = Find.TickManager?.TicksGame ?? 0;
            int targetId = target.thingIDNumber;

            if (!repeatStamps.TryGetValue(pawn, out RepeatStamp stamp)
                || stamp.def != candidate.def
                || stamp.targetThingId != targetId
                || now - stamp.lastTick > RepeatWindowTicks)
            {
                repeatStamps[pawn] = new RepeatStamp
                {
                    def = candidate.def,
                    targetThingId = targetId,
                    lastTick = now,
                    count = 1
                };
                return false;
            }

            stamp.lastTick = now;
            stamp.count++;
            if (stamp.count >= RepeatThreshold)
            {
                repeatStamps.Remove(pawn);
                return true;
            }

            return false;
        }
    }

    internal static class JobGiverTrashReplacement
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (JobLoopGuard.IsMountTransition(pawn))
            {
                __result = null;
                return false;
            }

            if (JobLoopGuard.UseVanillaFallbackForNow(pawn))
            {
                return true;
            }

            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map == null || pawn.Faction?.def == null)
            {
                return true;
            }

            if (pawn.mindState?.duty?.def != DutyDefOf.AssaultColony
                || pawn.Faction.def.techLevel < PogoAI.Init.settings.minSmartTechLevel
                || !PogoAI.Init.settings.everyRaidSaps
                || pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn)
                    .Count(x => !x.ThreatDisabled(pawn) && !x.Thing.Destroyed && x.Thing.Faction == Faction.OfPlayer) == 0)
            {
                return true;
            }

            bool runOriginal = PogoAI.Patches.JobGiver_AISapper.JobGiver_AISapper_TryGiveJob_Patch.Prefix(pawn, ref __result);
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

            return false;
        }
    }

    internal static class JobGiverSapperReplacement
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (JobLoopGuard.IsMountTransition(pawn))
            {
                __result = null;
                return false;
            }

            if (JobLoopGuard.UseVanillaFallbackForNow(pawn))
            {
                return true;
            }

            bool runOriginal = PogoAI.Patches.JobGiver_AISapper.JobGiver_AISapper_TryGiveJob_Patch.Prefix(pawn, ref __result);
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

            return false;
        }
    }
}
