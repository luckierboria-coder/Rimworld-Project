using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class TryEnterCleanPatches093T12
    {
        private static int requested;
        private static int installed;
        private static int missing;
        private static int failures;
        private static bool tryEnterInstalled;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            requested = installed = missing = failures = 0;
            tryEnterInstalled = false;

            MethodBase tryEnter = AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell");
            requested++;
            if (tryEnter == null)
            {
                missing++;
                Log.Warning("[RimMT] T12 clean TryEnter attribution disabled: TryEnterNextPathCell was not found.");
                return;
            }

            try
            {
                harmony.Patch(tryEnter,
                    prefix: new HarmonyMethod(typeof(TryEnterCleanPatches093T12), nameof(EnterPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(TryEnterCleanPatches093T12), nameof(EnterPostfix)) { priority = Priority.Last });
                installed++;
                tryEnterInstalled = true;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warning("[RimMT] T12 TryEnter root probe failed closed: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "BuildingBlockingNextPathCell"), nameof(BlockerPrefix), nameof(BlockerPostfix), "BuildingBlockingNextPathCell", false);
            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "NextCellDoorToWaitForOrManuallyOpen"), nameof(NextDoorPrefix), nameof(NextDoorPostfix), "NextCellDoorToWaitForOrManuallyOpen", false);
            Patch(harmony, AccessTools.PropertySetter(typeof(Thing), nameof(Thing.Position)), nameof(PositionPrefix), nameof(PositionPostfix), "Thing.Position.set", true);
            Patch(harmony, AccessTools.Method(typeof(GenClamor), nameof(GenClamor.DoClamor), new[] { typeof(Thing), typeof(float), typeof(ClamorDef) }), nameof(ClamorPrefix), nameof(ClamorPostfix), "GenClamor.DoClamor", true);
            Patch(harmony, AccessTools.Method(typeof(Pawn_FilthTracker), "Notify_EnteredNewCell"), nameof(FilthPrefix), nameof(FilthPostfix), "Pawn_FilthTracker.Notify_EnteredNewCell", true);
            Patch(harmony, AccessTools.Method(typeof(SnowGrid), nameof(SnowGrid.AddDepth), new[] { typeof(IntVec3), typeof(float) }), nameof(SnowPrefix), nameof(SnowPostfix), "SnowGrid.AddDepth", true);
            Patch(harmony, AccessTools.Method(typeof(Building_Door), nameof(Building_Door.CheckFriendlyTouched), new[] { typeof(Pawn) }), nameof(DoorTouchPrefix), nameof(DoorTouchPostfix), "Building_Door.CheckFriendlyTouched", true);
            Patch(harmony, AccessTools.Method(typeof(Building_Door), nameof(Building_Door.StartManualOpenBy), new[] { typeof(Pawn) }), nameof(DoorOpenPrefix), nameof(DoorOpenPostfix), "Building_Door.StartManualOpenBy", true);
            Patch(harmony, AccessTools.Method(typeof(Building_Door), nameof(Building_Door.StartManualCloseBy), new[] { typeof(Pawn) }), nameof(DoorClosePrefix), nameof(DoorClosePostfix), "Building_Door.StartManualCloseBy", true);
            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "RopeeWithStretchedRopeAtNextPathCell"), nameof(RopePrefix), nameof(RopePostfix), "RopeeWithStretchedRopeAtNextPathCell", false);
            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "NeedNewPath"), nameof(NeedPrefix), nameof(NeedPostfix), "NeedNewPath", true);
            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "TrySetNewPath"), nameof(SetPrefix), nameof(SetPostfix), "TrySetNewPath", true);
            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "AtDestinationPosition"), nameof(DestPrefix), nameof(DestPostfix), "AtDestinationPosition", true);
            Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell"), nameof(SetupPrefix), nameof(SetupPostfix), "SetupMoveIntoNextCell", true);

            Log.Message("[RimMT] T12 clean TryEnter attribution installed: root=" + tryEnterInstalled +
                ", helpers=" + (installed - 1) + ", missing=" + missing + ", failures=" + failures +
                ". Active only inside existing T2 deep windows; T9/T10/T11 are absent.");
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefixName, string postfixName, string label, bool expected)
        {
            requested++;
            if (target == null)
            {
                missing++;
                if (expected) Log.Warning("[RimMT] T12 helper probe unavailable: " + label + ". Residual remains authoritative for that work.");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(TryEnterCleanPatches093T12), prefixName) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(TryEnterCleanPatches093T12), postfixName) { priority = Priority.Last });
                installed++;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warning("[RimMT] T12 helper probe failed closed for " + label + ": " + ex.GetType().Name);
            }
        }

        internal static string Summary()
        {
            return "T12 clean TryEnter probes: root=" + tryEnterInstalled + ", requested=" + requested +
                ", installed=" + installed + ", missing=" + missing + ", failures=" + failures +
                ". Instrumentation-only; no path result, Harmony owner order, position, filth, snow, door or job state is changed.";
        }

        public static void EnterPrefix(Pawn ___pawn, ref long __state)
        {
            __state = TryEnterCleanAttribution093T12.BeginEnter(___pawn);
        }

        public static void EnterPostfix(long __state)
        {
            TryEnterCleanAttribution093T12.EndEnter(__state);
        }

        public static void PositionPrefix(Thing __instance, ref long __state)
        {
            __state = TryEnterCleanAttribution093T12.ShouldMeasureThing(__instance)
                ? TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.Position) : 0L;
        }
        public static void PositionPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.Position, __state); }

        public static void BlockerPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.Blocker); }
        public static void BlockerPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.Blocker, __state); }
        public static void NextDoorPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.NextDoor); }
        public static void NextDoorPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.NextDoor, __state); }
        public static void ClamorPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.Clamor); }
        public static void ClamorPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.Clamor, __state); }
        public static void FilthPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.Filth); }
        public static void FilthPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.Filth, __state); }
        public static void SnowPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.Snow); }
        public static void SnowPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.Snow, __state); }
        public static void DoorTouchPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.DoorTouch); }
        public static void DoorTouchPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.DoorTouch, __state); }
        public static void DoorOpenPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.DoorOpen); }
        public static void DoorOpenPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.DoorOpen, __state); }
        public static void DoorClosePrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.DoorClose); }
        public static void DoorClosePostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.DoorClose, __state); }
        public static void RopePrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.Rope); }
        public static void RopePostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.Rope, __state); }
        public static void NeedPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.NeedNewPath); }
        public static void NeedPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.NeedNewPath, __state); }
        public static void SetPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.TrySetNewPath); }
        public static void SetPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.TrySetNewPath, __state); }
        public static void DestPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.AtDestination); }
        public static void DestPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.AtDestination, __state); }
        public static void SetupPrefix(ref long __state) { __state = TryEnterCleanAttribution093T12.BeginStage(TryEnterStage093T12.SetupMove); }
        public static void SetupPostfix(long __state) { TryEnterCleanAttribution093T12.EndStage(TryEnterStage093T12.SetupMove, __state); }
    }
}
