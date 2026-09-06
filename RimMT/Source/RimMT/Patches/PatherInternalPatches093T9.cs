using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class PatherInternalPatches093T9
    {
        private static int patched;
        private static int missing;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            Type t = typeof(Pawn_PathFollower);
            PatchOne(harmony, AccessTools.Method(t, "WillCollideWithPawnAt", new Type[] { typeof(IntVec3) }), nameof(CollidePrefix), nameof(CollidePostfix), "WillCollideWithPawnAt");
            PatchOne(harmony, AccessTools.Method(t, "TryEnterNextPathCell"), nameof(EnterPrefix), nameof(EnterPostfix), "TryEnterNextPathCell");
            PatchOne(harmony, AccessTools.Method(t, "BuildingBlockingNextPathCell"), nameof(BlockPrefix), nameof(BlockPostfix), "BuildingBlockingNextPathCell");
            PatchOne(harmony, AccessTools.Method(t, "NextCellDoorToWaitForOrManuallyOpen"), nameof(DoorPrefix), nameof(DoorPostfix), "NextCellDoorToWaitForOrManuallyOpen");
            PatchOne(harmony, AccessTools.Method(t, "NeedNewPath"), nameof(NeedPrefix), nameof(NeedPostfix), "NeedNewPath");
            PatchOne(harmony, AccessTools.Method(t, "TrySetNewPath"), nameof(SetPrefix), nameof(SetPostfix), "TrySetNewPath");
            PatchOne(harmony, AccessTools.Method(t, "GenerateNewPath"), nameof(GeneratePrefix), nameof(GeneratePostfix), "GenerateNewPath");
            PatchOne(harmony, AccessTools.Method(t, "AtDestinationPosition"), nameof(DestPrefix), nameof(DestPostfix), "AtDestinationPosition");
            PatchOne(harmony, AccessTools.Method(t, "SetupMoveIntoNextCell"), nameof(SetupPrefix), nameof(SetupPostfix), "SetupMoveIntoNextCell");
            PatchOne(harmony, AccessTools.Method(t, "CostToMoveIntoCell", new Type[] { typeof(IntVec3) }), nameof(CostMovePrefix), nameof(CostMovePostfix), "CostToMoveIntoCell(IntVec3)");
            PatchOne(harmony, AccessTools.Method(t, "CostToPayThisTick"), nameof(CostPayPrefix), nameof(CostPayPostfix), "CostToPayThisTick");
            Log.Message("[RimMT] T9 bounded Pather internal attribution installed: patched=" + patched + ", missing=" + missing + ". Stopwatch active only inside existing T2 deep PatherTick samples; behavior unchanged.");
        }

        private static void PatchOne(Harmony harmony, MethodBase target, string prefixName, string postfixName, string label)
        {
            if (target == null)
            {
                missing++;
                Log.Warning("[RimMT] T9 Pather attribution target missing: " + label);
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(PatherInternalPatches093T9), prefixName) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(PatherInternalPatches093T9), postfixName) { priority = Priority.Last });
                patched++;
            }
            catch (Exception ex)
            {
                missing++;
                Log.Warning("[RimMT] T9 Pather attribution target failed closed: " + label + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Begin(ref long state) { state = PatherInternalAttribution093T9.BeginCall(); }
        private static void End(long state, PatherInternalPhase093T9 phase) { if (state != 0L) PatherInternalAttribution093T9.EndCall(state, phase); }

        public static void CollidePrefix(ref long __state) { Begin(ref __state); }
        public static void CollidePostfix(long __state) { End(__state, PatherInternalPhase093T9.WillCollideWithPawnAt); }
        public static void EnterPrefix(ref long __state) { Begin(ref __state); }
        public static void EnterPostfix(long __state) { End(__state, PatherInternalPhase093T9.TryEnterNextPathCell); }
        public static void BlockPrefix(ref long __state) { Begin(ref __state); }
        public static void BlockPostfix(long __state) { End(__state, PatherInternalPhase093T9.BuildingBlockingNextPathCell); }
        public static void DoorPrefix(ref long __state) { Begin(ref __state); }
        public static void DoorPostfix(long __state) { End(__state, PatherInternalPhase093T9.NextCellDoorToWaitForOrManuallyOpen); }
        public static void NeedPrefix(ref long __state) { Begin(ref __state); }
        public static void NeedPostfix(long __state) { End(__state, PatherInternalPhase093T9.NeedNewPath); }
        public static void SetPrefix(ref long __state) { Begin(ref __state); }
        public static void SetPostfix(long __state) { End(__state, PatherInternalPhase093T9.TrySetNewPath); }
        public static void GeneratePrefix(ref long __state) { Begin(ref __state); }
        public static void GeneratePostfix(long __state) { End(__state, PatherInternalPhase093T9.GenerateNewPath); }
        public static void DestPrefix(ref long __state) { Begin(ref __state); }
        public static void DestPostfix(long __state) { End(__state, PatherInternalPhase093T9.AtDestinationPosition); }
        public static void SetupPrefix(ref long __state) { Begin(ref __state); }
        public static void SetupPostfix(long __state) { End(__state, PatherInternalPhase093T9.SetupMoveIntoNextCell); }
        public static void CostMovePrefix(ref long __state) { Begin(ref __state); }
        public static void CostMovePostfix(long __state) { End(__state, PatherInternalPhase093T9.CostToMoveIntoCell); }
        public static void CostPayPrefix(ref long __state) { Begin(ref __state); }
        public static void CostPayPostfix(long __state) { End(__state, PatherInternalPhase093T9.CostToPayThisTick); }
    }
}
