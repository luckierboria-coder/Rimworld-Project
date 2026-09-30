using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    // V0.4.17.2 compatibility layer for source-reviewed GrowerSow season patches.
    //
    // Biomes! Core and ReGrowth Core both change PlantUtility.GrowthSeasonNow semantics while
    // WorkGiver_GrowerSow.JobOnCell is running. ReGrowth additionally consults a normal
    // Dictionary<Map, Dictionary<IntVec3, PlantExpandable>> outside the JobOnCell boundary.
    // That dictionary is maintained by main-thread SpawnSetup/DeSpawn and must never be read
    // concurrently by a RimMT worker.
    //
    // Therefore restricted Sow mode does not merely discard an OutOfGrowthSeason prediction
    // after the worker has computed it. V0.4.17.2 brackets the private worker evaluator with a
    // ThreadStatic marker and returns true ("do not prove a season negative") from the exact
    // worker-side GrowthSeasonNow call before Biomes/ReGrowth season prefixes execute. The
    // evaluator prefix uses Harmony's positional __1 argument rather than object[] __args so no
    // byref/out Work-prefilter state can be rewritten by compatibility plumbing.
    [StaticConstructorOnStartup]
    internal static class WorkPrefilterCompatibility04172
    {
        private static readonly FieldInfo SowCompatibleField =
            AccessTools.Field(typeof(ParallelWorkPrefilter), "sowCompatible");

        [ThreadStatic]
        private static bool restrictedSowWorkerEvaluation;

        private static volatile bool restrictedSowMode;
        private static volatile bool biomesSowCoexists;
        private static volatile bool regrowthSowCoexists;
        private static long workerSeasonCallsBypassed;
        private static long shimFaults;

        static WorkPrefilterCompatibility04172()
        {
            try
            {
                MethodInfo markReady = AccessTools.Method(typeof(ParallelWorkPrefilter), "MarkCompatibilityReady");
                MethodInfo evaluateLiveNegative = AccessTools.Method(typeof(ParallelWorkPrefilter), "EvaluateLiveNegative");
                MethodInfo growthSeasonNow = AccessTools.Method(
                    typeof(PlantUtility),
                    nameof(PlantUtility.GrowthSeasonNow),
                    new Type[] { typeof(IntVec3), typeof(Map), typeof(bool) });

                if (markReady == null || evaluateLiveNegative == null || growthSeasonNow == null || SowCompatibleField == null)
                {
                    Log.Warning("[RimMT] V0.4.17.2 Sow compatibility layer unavailable: required private/runtime targets were not found. V0.4.17 fail-closed behavior remains in force.");
                    return;
                }

                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);

                HarmonyMethod readyPostfix = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04172), nameof(CompatibilityReadyPostfix));
                readyPostfix.priority = Priority.Last;
                harmony.Patch(markReady, postfix: readyPostfix);

                HarmonyMethod evalPrefix = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04172), nameof(EvaluateLiveNegativePrefix));
                evalPrefix.priority = Priority.First;
                HarmonyMethod evalFinalizer = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04172), nameof(EvaluateLiveNegativeFinalizer));
                evalFinalizer.priority = Priority.Last;
                harmony.Patch(evaluateLiveNegative, prefix: evalPrefix, finalizer: evalFinalizer);

                HarmonyMethod seasonPrefix = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04172), nameof(GrowthSeasonNowPrefix));
                seasonPrefix.priority = Priority.First;
                seasonPrefix.before = new string[] { "rimworld.biomes.core", "Helixien.ReGrowthCore" };
                harmony.Patch(growthSeasonNow, prefix: seasonPrefix);

                Log.Message("[RimMT] V0.4.17.2 Sow compatibility layer installed. Exact Biomes/ReGrowth JobOnCell season-context patches may coexist only in restricted mode; worker GrowthSeasonNow calls are bypassed before foreign season prefixes, while live Vanilla JobOnCell keeps full mod authority.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] V0.4.17.2 Sow compatibility layer failed to install; V0.4.17 fail-closed behavior remains in force. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CompatibilityReadyPostfix()
        {
            try
            {
                MethodBase sowTarget = AccessTools.Method(
                    typeof(WorkGiver_GrowerSow),
                    nameof(WorkGiver_GrowerSow.JobOnCell),
                    new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
                MethodBase baseHasJobTarget = AccessTools.Method(
                    typeof(WorkGiver_Scanner),
                    nameof(WorkGiver_Scanner.HasJobOnCell),
                    new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });

                bool sawBiomes;
                bool sawReGrowth;
                string sowBlocker;
                bool sowSafe = InspectRestrictedSowTarget(
                    sowTarget,
                    out sawBiomes,
                    out sawReGrowth,
                    out sowBlocker);

                string baseBlocker;
                bool baseSafe = NoForeignPatches(baseHasJobTarget, out baseBlocker);

                if (sowSafe && baseSafe && (sawBiomes || sawReGrowth))
                {
                    SowCompatibleField.SetValue(null, 1);
                    restrictedSowMode = true;
                    biomesSowCoexists = sawBiomes;
                    regrowthSowCoexists = sawReGrowth;
                    Log.Message("[RimMT] V0.4.17.2 enables GrowerSow prefilter in restricted season mode. exactBiomes=" +
                        sawBiomes + ", exactReGrowth=" + sawReGrowth +
                        ". Workers skip GrowthSeasonNow entirely for Sow classification; NoDesiredPlant/SameDesiredPlantPresent hard negatives remain eligible, and live Vanilla JobOnCell owns all season decisions.");
                }
                else if ((sawBiomes || sawReGrowth) && (!sowSafe || !baseSafe))
                {
                    Log.Warning("[RimMT] V0.4.17.2 keeps GrowerSow fail-closed because an unreviewed foreign patch is still present. blocker=" +
                        (!sowSafe ? sowBlocker : baseBlocker));
                }

                Log.Message("[RimMT] V0.4.17.2 Sow coexistence result: restricted=" + restrictedSowMode +
                    ", biomes=" + biomesSowCoexists +
                    ", regrowth=" + regrowthSowCoexists +
                    ". BuildRoof policy is unchanged; Harvest remains governed by the exact AlienRace coexistence layer.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref shimFaults);
                Log.Warning("[RimMT] V0.4.17.2 Sow coexistence evaluation failed; previous fail-closed decisions remain authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        // EvaluateLiveNegative(Map map, WorkKind kind, IntVec3 c): __1 is the private WorkKind.
        // object is sufficient for the boxed enum and avoids object[] argument-array rewriting.
        private static void EvaluateLiveNegativePrefix(object __1)
        {
            restrictedSowWorkerEvaluation = false;
            if (!restrictedSowMode || RimMTThreadGuard.IsMainThread)
                return;

            try
            {
                if (__1 != null && string.Equals(__1.ToString(), "Sow", StringComparison.Ordinal))
                    restrictedSowWorkerEvaluation = true;
            }
            catch
            {
                restrictedSowWorkerEvaluation = false;
                Interlocked.Increment(ref shimFaults);
            }
        }

        private static Exception EvaluateLiveNegativeFinalizer(Exception __exception)
        {
            restrictedSowWorkerEvaluation = false;
            return __exception;
        }

        private static bool GrowthSeasonNowPrefix(ref bool __result)
        {
            if (!restrictedSowWorkerEvaluation || RimMTThreadGuard.IsMainThread)
                return true;

            // Returning true here means "season is not a hard negative". Priority.First plus
            // explicit before-owner constraints prevents the reviewed Biomes/ReGrowth prefixes
            // from touching their main-thread-only/shared-static season context on this worker.
            __result = true;
            Interlocked.Increment(ref workerSeasonCallsBypassed);
            return false;
        }

        private static bool InspectRestrictedSowTarget(
            MethodBase target,
            out bool sawBiomes,
            out bool sawReGrowth,
            out string blocker)
        {
            sawBiomes = false;
            sawReGrowth = false;
            blocker = null;
            if (target == null)
            {
                blocker = "<missing-target>";
                return false;
            }

            Patches info = Harmony.GetPatchInfo(target);
            if (info == null)
                return true;

            if (!CheckSowPatchList(info.Prefixes, "prefix", ref sawBiomes, ref sawReGrowth, out blocker)) return false;
            if (!CheckSowPatchList(info.Postfixes, "postfix", ref sawBiomes, ref sawReGrowth, out blocker)) return false;
            if (!CheckSowPatchList(info.Transpilers, "transpiler", ref sawBiomes, ref sawReGrowth, out blocker)) return false;
            if (!CheckSowPatchList(info.Finalizers, "finalizer", ref sawBiomes, ref sawReGrowth, out blocker)) return false;
            return true;
        }

        private static bool CheckSowPatchList(
            IList<Patch> patches,
            string kind,
            ref bool sawBiomes,
            ref bool sawReGrowth,
            out string blocker)
        {
            blocker = null;
            if (patches == null)
                return true;

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    continue;

                if (IsExactBiomesSowPrefix(patch, kind))
                {
                    sawBiomes = true;
                    continue;
                }

                if (IsExactReGrowthSowBoundary(patch, kind))
                {
                    sawReGrowth = true;
                    continue;
                }

                blocker = Describe(patch, kind);
                return false;
            }
            return true;
        }

        private static bool IsExactBiomesSowPrefix(Patch patch, string kind)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            return string.Equals(kind, "prefix", StringComparison.Ordinal) &&
                   string.Equals(patch.owner, "rimworld.biomes.core", StringComparison.OrdinalIgnoreCase) &&
                   method != null && method.DeclaringType != null &&
                   string.Equals(method.DeclaringType.FullName,
                       "BiomesCore.Patches.Plants.WorkGiver_GrowerSow_JobOnCell_Patch",
                       StringComparison.Ordinal) &&
                   string.Equals(method.Name, "Prefix", StringComparison.Ordinal);
        }

        private static bool IsExactReGrowthSowBoundary(Patch patch, string kind)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            if (method == null || method.DeclaringType == null ||
                !string.Equals(patch.owner, "Helixien.ReGrowthCore", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(method.DeclaringType.FullName,
                    "ReGrowthCore.PlantExpandable+WorkGiver_GrowerSow_JobOnCell_Patch",
                    StringComparison.Ordinal))
                return false;

            return (string.Equals(kind, "prefix", StringComparison.Ordinal) &&
                    string.Equals(method.Name, "Prefix", StringComparison.Ordinal)) ||
                   (string.Equals(kind, "postfix", StringComparison.Ordinal) &&
                    string.Equals(method.Name, "Postfix", StringComparison.Ordinal));
        }

        private static bool NoForeignPatches(MethodBase target, out string blocker)
        {
            blocker = null;
            if (target == null)
            {
                blocker = "<missing-target>";
                return false;
            }

            Patches info = Harmony.GetPatchInfo(target);
            if (info == null)
                return true;

            if (FindForeign(info.Prefixes, "prefix", out blocker)) return false;
            if (FindForeign(info.Postfixes, "postfix", out blocker)) return false;
            if (FindForeign(info.Transpilers, "transpiler", out blocker)) return false;
            if (FindForeign(info.Finalizers, "finalizer", out blocker)) return false;
            return true;
        }

        private static bool FindForeign(IList<Patch> patches, string kind, out string blocker)
        {
            blocker = null;
            if (patches == null)
                return false;

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    continue;
                blocker = Describe(patch, kind);
                return true;
            }
            return false;
        }

        private static string Describe(Patch patch, string kind)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            return kind + " " + (patch == null ? "<null>" : patch.owner ?? "<unknown-owner>") + " :: " +
                (method == null || method.DeclaringType == null
                    ? "<unknown-method>"
                    : method.DeclaringType.FullName + "." + method.Name);
        }

        internal static string Summary()
        {
            return "Work prefilter Sow compatibility V0.4.17.2: restrictedSow=" + restrictedSowMode +
                ", biomes=" + biomesSowCoexists +
                ", regrowth=" + regrowthSowCoexists +
                ", workerSeasonCallsBypassed=" + Interlocked.Read(ref workerSeasonCallsBypassed) +
                ", shimFaults=" + Interlocked.Read(ref shimFaults) +
                ". Restricted Sow workers never execute modded GrowthSeasonNow semantics; live Vanilla JobOnCell remains authoritative.";
        }
    }
}
