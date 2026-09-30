using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class TailPawnPatches093T2
    {
        private static int patched;
        private static int missing;
        private static string missingNames = string.Empty;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            PatchOne(harmony, AccessTools.Method(typeof(Pawn), "Tick"), nameof(PawnPrefix), nameof(PawnPostfix), "Pawn.Tick");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), nameof(PhasePrefix), nameof(JobTrackerPostfix), "Pawn_JobTracker.JobTrackerTick");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), nameof(DeterminePrefix), nameof(DeterminePostfix), "Pawn_JobTracker.DetermineNextJob");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "CheckForJobOverride_NewTemp"), nameof(PhasePrefix), nameof(OverridePostfix), "Pawn_JobTracker.CheckForJobOverride_NewTemp");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"), nameof(PhasePrefix), nameof(PatherPostfix), "Pawn_PathFollower.PatherTick");
            Log.Message("[RimMT] T2 Pawn Tail Attribution installed: patched=" + patched + ", missing=" + missing +
                (missing == 0 ? "." : ", missingTargets=" + missingNames + ".") +
                " Stopwatch timing only on bounded deep-sample ticks; optimizer behavior unchanged.");
        }

        private static void PatchOne(Harmony harmony, MethodBase target, string prefixName, string postfixName, string label)
        {
            if (target == null)
            {
                missing++;
                if (missingNames.Length != 0) missingNames += ",";
                missingNames += label;
                return;
            }
            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(TailPawnPatches093T2), prefixName) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(TailPawnPatches093T2), postfixName) { priority = Priority.Last };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                patched++;
            }
            catch (Exception ex)
            {
                missing++;
                Log.Warning("[RimMT] T2 pawn attribution target failed closed: " + label + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PhasePrefix(ref long __state)
        {
            __state = TailPawnAttribution093T2.DeepActive ? TailPawnAttribution093T2.BeginPhase() : 0L;
        }

        public static void DeterminePrefix(ref long __state)
        {
            if (!TailPawnAttribution093T2.DeepActive)
            {
                __state = 0L;
                return;
            }
            JobGiverSlowSearch0419S.T8BeginDetermineAttribution();
            __state = TailPawnAttribution093T2.BeginPhase();
        }

        public static void PawnPrefix(Pawn __instance, ref long __state)
        {
            bool deep = TailPawnAttribution093T2.DeepActive;
            PawnTickAggregateAttribution093T13.BeginPawn(__instance, deep);
            __state = deep ? TailPawnAttribution093T2.BeginPhase() : 0L;
            PlayerHumanResidualAttribution093T14.BeginPawn(__instance, deep, __state);
        }

        public static void PawnPostfix(Pawn __instance, long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPawn(__state, __instance);
        }

        public static void JobTrackerPostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.JobTrackerTick);
        }

        public static void DeterminePostfix(long __state)
        {
            if (__state == 0L) return;
            JobGiverSlowSearch0419S.T8EndDetermineAttribution(__state);
            TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
        }

        public static void OverridePostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.CheckForJobOverride);
        }

        public static void PatherPostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.PatherTick);
        }
    }
}



