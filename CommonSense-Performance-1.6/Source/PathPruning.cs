using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.CommonSensePerformance
{
    /// <summary>
    /// M2: selectively skip the third expensive FindPathNow call only when
    /// the exact Common Sense 1.6 answer is already provably null.
    /// Target is a PRIVATE Common Sense method, not global PawnPath / Pathfinder.
    /// </summary>
    public static class PathPruning
    {
        private static Func<Pawn, bool> incapableOfCleaning;
        private static Func<Pawn, LocalTargetInfo, int, Job> makeCleaningJob;
        private static FieldInfo calculateFullPath;
        private static bool disabled;
        private static bool loggedFailure;

        public static void TryInstall()
        {
            try
            {
                Type tasksType = AccessTools.TypeByName("CommonSense.OpportunisticTasks");
                Type utilityType = AccessTools.TypeByName("CommonSense.Utility");
                Type settingsType = AccessTools.TypeByName("CommonSense.Settings");
                if (tasksType == null || utilityType == null || settingsType == null)
                {
                    Log.Warning("[CS Perf M2] Common Sense 1.6 API not found; M2 disabled.");
                    return;
                }

                MethodInfo target = AccessTools.Method(tasksType, "Cleaning_Opportunity",
                    new[] { typeof(Job), typeof(Pawn), typeof(int) });
                MethodInfo incapable = AccessTools.Method(utilityType, "IncapableOfCleaning",
                    new[] { typeof(Pawn) });
                MethodInfo make = AccessTools.Method(tasksType, "MakeCleaningJob",
                    new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int) });
                FieldInfo setting = AccessTools.Field(settingsType, "calculate_full_path");
                if (target == null || target.ReturnType != typeof(Job) ||
                    incapable == null || incapable.ReturnType != typeof(bool) ||
                    make == null || make.ReturnType != typeof(Job) ||
                    setting == null || setting.FieldType != typeof(bool))
                {
                    Log.Warning("[CS Perf M2] Common Sense 1.6 signature mismatch; M2 disabled.");
                    return;
                }

                Patches info = Harmony.GetPatchInfo(target);
                if (info != null && (info.Prefixes.Count != 0 ||
                    info.Postfixes.Count != 0 || info.Transpilers.Count != 0 ||
                    info.Finalizers.Count != 0))
                {
                    Log.Warning("[CS Perf M2] Cleaning_Opportunity already modified by another Harmony patch; M2 disabled.");
                    return;
                }

                // Bind delegates ONCE at startup rather than using MethodInfo.Invoke in hot loops.
                incapableOfCleaning = AccessTools.MethodDelegate<Func<Pawn, bool>>(incapable);
                makeCleaningJob = AccessTools.MethodDelegate<Func<Pawn, LocalTargetInfo, int, Job>>(make);
                calculateFullPath = setting;

                var prefix = new HarmonyMethod(AccessTools.Method(typeof(PathPruning), nameof(Prefix)));
                new Harmony("allen.commonsense.performance16.m2").Patch(target, prefix: prefix);
                Log.Message("[CS Perf M2] Exact-output path pruning enabled (third-path short-circuit only).");
            }
            catch (Exception ex)
            {
                Log.Warning("[CS Perf M2] Installation failed; vanilla Common Sense path logic is unchanged. " + ex);
            }
        }

        public static bool Prefix(Job currJob, Pawn pawn, int Limit, ref Job __result)
        {
            if (disabled) return true;
            try
            {
                // With the setting disabled, use the ORIGINAL Euclidean path logic.
                if (!(bool)calculateFullPath.GetValue(null)) return true;

                // Preserve the original early eligibility exit, including its null answer.
                if (incapableOfCleaning(pawn))
                {
                    __result = null;
                    return false;
                }

                Thing building = currJob.targetA.Thing;
                if (building == null) return true;

                Thing target = null;
                if (currJob.targetB != null)
                    target = currJob.targetB.Thing;
                if (target == null && currJob.targetQueueB != null &&
                    currJob.targetQueueB.Count > 0)
                    target = currJob.targetQueueB[0].Thing;
                if (target == null) return true;

                // Exactly the same first two FindPathNow requests as Common Sense 1.6.
                // Release each pooled path immediately, even when exceptions occur.
                float stot;
                if (!TryCost(target.Map, pawn.Position, target, out stot))
                {
                    __result = null;
                    return false;
                }

                float stob;
                if (!TryCost(building.Map, pawn.Position, building, out stob))
                {
                    __result = null;
                    return false;
                }

                // All found path costs are non-negative, so btot >= 0.
                // If stob > 500 and stot/stob is comfortably below 0.7,
                // the upstream predicate will be TRUE for ANY btot >= 0.
                // A substantial margin (0.65) avoids borderline float issues.
                // If the third path were unreachable, upstream also returns null.
                // Therefore skipping it cannot change the returned Job here.
                if (stob > 500f && stot < stob * 0.65f)
                {
                    __result = null;
                    return false;
                }

                float btot;
                if (!TryCost(target.Map, building.Position, target, out btot))
                {
                    __result = null;
                    return false;
                }

                // Unchanged upstream formula (do not re-associate float arithmetic).
                if (stob > 500f && stot / (stob + btot) < 0.7f)
                {
                    __result = null;
                    return false;
                }

                // Call ORIGINAL MakeCleaningJob; existing Common Sense and our M1
                // filth-selection patch still own all cleaning eligibility logic.
                __result = makeCleaningJob(pawn, currJob.targetA, Limit);
                return false;
            }
            catch (Exception ex)
            {
                disabled = true;
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Log.Warning("[CS Perf M2] Runtime exception; disabling M2 for this session. "
                        + "Original Common Sense will handle this decision. " + ex);
                }
                return true;
            }
        }

        private static bool TryCost(Map map, IntVec3 source, Thing destination, out float cost)
        {
            PawnPath path = map.pathFinder.FindPathNow(source, destination,
                TraverseParms.For(TraverseMode.PassDoors, Danger.None),
                null, PathEndMode.Touch);
            try
            {
                if (!path.Found)
                {
                    cost = 0f;
                    return false;
                }
                cost = path.TotalCost;
                return true;
            }
            finally
            {
                path.ReleaseToPool();
            }
        }
    }
}
