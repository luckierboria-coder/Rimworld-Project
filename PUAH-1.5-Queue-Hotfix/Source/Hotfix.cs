using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PUAHQueueHotfix
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony("local.PUAH15.QueueHotfix");
                Type driverType = AccessTools.TypeByName("PickUpAndHaul.JobDriver_HaulToInventory");
                Type workGiverType = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");

                if (driverType == null || workGiverType == null)
                {
                    Log.Error("[PUAH 1.5 Queue Hotfix V5.1] Pick Up And Haul types were not found; hotfix not applied.");
                    return;
                }

                MethodInfo reserveMethod = AccessTools.Method(driverType, "TryMakePreToilReservations");
                MethodInfo jobOnThingMethod = AccessTools.Method(workGiverType, "JobOnThing", new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });

                if (reserveMethod == null || jobOnThingMethod == null)
                {
                    Log.Error("[PUAH 1.5 Queue Hotfix V5.1] Expected PUAH methods were not found; hotfix not applied.");
                    return;
                }

                harmony.Patch(reserveMethod,
                    prefix: new HarmonyMethod(typeof(ReservationGuard).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)));

                harmony.Patch(jobOnThingMethod,
                    postfix: new HarmonyMethod(typeof(JobCreationGuard).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)));

                Log.Message("[PUAH 1.5 Queue Hotfix V5.1] Applied HaulToInventory queue repair + reservation guards.");
            }
            catch (Exception e)
            {
                Log.Error("[PUAH 1.5 Queue Hotfix V5.1] Failed to initialize: " + e);
            }
        }
    }

    internal static class JobRepairer
    {
        internal static bool TryRepair(Job job, out bool changed, out string repairSummary, out string fatalReason)
        {
            changed = false;
            repairSummary = null;
            fatalReason = null;

            if (job == null)
            {
                fatalReason = "job is null";
                return false;
            }

            if (job.targetQueueA == null)
            {
                fatalReason = "targetQueueA is null";
                return false;
            }

            if (job.countQueue == null)
            {
                fatalReason = "countQueue is null";
                return false;
            }

            int originalTargets = job.targetQueueA.Count;
            int originalCounts = job.countQueue.Count;
            int removedNonPositive = 0;
            int removedInvalid = 0;
            int removedUnmatchedTargets = 0;
            int removedUnmatchedCounts = 0;

            // PUAH consumes targetQueueA and countQueue in lockstep.  If one queue
            // has an unmatched tail, the unmatched entries cannot be executed safely.
            while (job.targetQueueA.Count > job.countQueue.Count)
            {
                job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
                removedUnmatchedTargets++;
                changed = true;
            }

            while (job.countQueue.Count > job.targetQueueA.Count)
            {
                job.countQueue.RemoveAt(job.countQueue.Count - 1);
                removedUnmatchedCounts++;
                changed = true;
            }

            // Walk backwards so paired removals never shift an unchecked index.
            for (int i = job.targetQueueA.Count - 1; i >= 0; i--)
            {
                LocalTargetInfo target = job.targetQueueA[i];
                int count = job.countQueue[i];

                if (count <= 0)
                {
                    job.targetQueueA.RemoveAt(i);
                    job.countQueue.RemoveAt(i);
                    removedNonPositive++;
                    changed = true;
                    continue;
                }

                // HaulToInventory's queue A is a Thing queue.  A cell target, an
                // invalid target, or a despawned/destroyed Thing cannot survive the
                // driver's TargetThingA / SplitOff path, so remove the pair.
                if (!target.IsValid || !target.HasThing)
                {
                    job.targetQueueA.RemoveAt(i);
                    job.countQueue.RemoveAt(i);
                    removedInvalid++;
                    changed = true;
                    continue;
                }

                Thing thing = target.Thing;
                if (thing == null || thing.Destroyed || !thing.Spawned || thing.stackCount <= 0)
                {
                    job.targetQueueA.RemoveAt(i);
                    job.countQueue.RemoveAt(i);
                    removedInvalid++;
                    changed = true;
                }
            }

            if (changed)
            {
                repairSummary =
                    "targets " + originalTargets + "->" + job.targetQueueA.Count +
                    ", counts " + originalCounts + "->" + job.countQueue.Count +
                    ", removed count<=0=" + removedNonPositive +
                    ", invalid=" + removedInvalid +
                    ", unmatchedTargets=" + removedUnmatchedTargets +
                    ", unmatchedCounts=" + removedUnmatchedCounts;
            }

            if (job.targetQueueA.Count == 0 || job.countQueue.Count == 0)
            {
                fatalReason = changed
                    ? "no valid paired haul targets remain after repair (" + repairSummary + ")"
                    : "target/count queues are empty";
                return false;
            }

            // This should be guaranteed by the trimming above, but keep a final
            // invariant check so the original PUAH driver never receives mismatched
            // queues from this hotfix.
            if (job.targetQueueA.Count != job.countQueue.Count)
            {
                fatalReason = "queue mismatch remains after repair targetQueueA=" +
                    job.targetQueueA.Count + ", countQueue=" + job.countQueue.Count;
                return false;
            }

            return true;
        }

        internal static int LogKey(Job job, Pawn pawn, string stage, int salt)
        {
            int key = unchecked((int)0x51A70F11) ^ salt;
            if (job != null) key ^= job.GetHashCode();
            if (pawn != null) key ^= pawn.thingIDNumber;
            if (stage != null) key ^= stage.GetHashCode();
            return key;
        }

        internal static void LogRepair(Job job, Pawn pawn, string stage, string summary)
        {
            // V5.1 release behavior: successful repairs are intentionally silent.
            // V5 logged every repaired job with Log.WarningOnce using a per-job key,
            // which could generate many full stack traces and cause visible micro-stutter
            // in colonies where PUAH frequently produces count<=0 queue entries.
            // Keep the repair itself, but do not perform any logging on this hot path.
        }

        internal static void LogReject(Job job, Pawn pawn, string stage, string reason)
        {
            string pawnName = pawn != null ? pawn.LabelShort : "null";
            int key = unchecked((int)0x0BADBEEF);
            if (pawn != null) key ^= pawn.thingIDNumber;
            if (stage != null) key ^= stage.GetHashCode();
            Log.WarningOnce(
                "[PUAH 1.5 Queue Hotfix V5.1] Rejected unrecoverable HaulToInventory job during " + stage +
                ". Pawn=" + pawnName + "; Reason=" + reason +
                ". The bad PUAH job was discarded instead of crashing.",
                key);
        }
    }

    internal static class VanillaHaulFallback
    {
        private static MethodInfo haulToStorageJob;
        private static bool searched;

        internal static Job TryCreate(Pawn pawn, Thing thing, bool forced)
        {
            try
            {
                if (pawn == null || thing == null || thing.Destroyed || !thing.Spawned)
                    return null;

                if (!searched)
                {
                    searched = true;

                    // Different 1.5 builds/modded assemblies expose different
                    // HaulToStorageJob signatures.  Prefer the legacy 3-arg form when
                    // present.  If it is absent, leave fallback disabled rather than
                    // guessing arguments for an unknown overload.
                    haulToStorageJob = AccessTools.Method(typeof(HaulAIUtility), "HaulToStorageJob",
                        new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                }

                if (haulToStorageJob == null)
                    return null;

                return haulToStorageJob.Invoke(null, new object[] { pawn, thing, forced }) as Job;
            }
            catch (Exception e)
            {
                Log.Warning("[PUAH 1.5 Queue Hotfix V5.1] Vanilla haul fallback failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }
    }

    public static class JobCreationGuard
    {
        public static void Postfix(Pawn pawn, Thing thing, bool forced, ref Job __result)
        {
            if (__result == null || __result.def == null || __result.def.defName != "HaulToInventory")
                return;

            bool changed;
            string repairSummary;
            string fatalReason;
            bool usable = JobRepairer.TryRepair(__result, out changed, out repairSummary, out fatalReason);

            if (changed && usable)
                JobRepairer.LogRepair(__result, pawn, "JobOnThing", repairSummary);

            if (usable)
                return;

            JobRepairer.LogReject(__result, pawn, "JobOnThing", fatalReason);
            __result = VanillaHaulFallback.TryCreate(pawn, thing, forced);
        }
    }

    public static class ReservationGuard
    {
        public static bool Prefix(JobDriver __instance, ref bool __result)
        {
            Job job = __instance != null ? __instance.job : null;
            Pawn pawn = __instance != null ? __instance.pawn : null;

            bool changed;
            string repairSummary;
            string fatalReason;
            bool usable = JobRepairer.TryRepair(job, out changed, out repairSummary, out fatalReason);

            if (changed && usable)
                JobRepairer.LogRepair(job, pawn, "TryMakePreToilReservations", repairSummary);

            if (usable)
                return true;

            JobRepairer.LogReject(job, pawn, "TryMakePreToilReservations", fatalReason);
            __result = false;
            return false;
        }
    }
}
