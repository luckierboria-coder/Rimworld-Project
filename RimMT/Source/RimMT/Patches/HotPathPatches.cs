using System.Diagnostics;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal struct PathPatchState
    {
        internal long Started;
        internal int RequestId;
        internal bool IsTraverseParms;
    }

    internal static class HotPathPatches
    {
        public static void TickPrefix(ref long __state)
        {
            // Butter++ can split one logical DoSingleTick across multiple rendered frames and
            // replays foreign DoSingleTick prefixes/postfixes itself. Do not start a wall-clock
            // sample here in that mode; TickManagerUpdate slices are measured instead.
            __state = RuntimeCompatibility.ButterPlusPlusActive ? 0L : Stopwatch.GetTimestamp();
        }

        public static void TickPostfix(long __state)
        {
            if (__state == 0L)
                return;
            HotPathProfiler.End("TickManager.DoSingleTick", __state);
            AdaptiveLoadBalancer.RecordTick(__state);
        }

        public static void PathPrefix(PathFinder __instance, object[] __args, ref PathPatchState __state)
        {
            __state.Started = FeatureGate.IsEnabled("diagnostics.hotPaths") ? HotPathProfiler.Begin() : 0L;
            __state.IsTraverseParms = __args != null && __args.Length > 2 && __args[2] is TraverseParms;
            __state.RequestId = PathSnapshotWorker.TrySchedule(__instance, __args);
        }

        public static void PathPostfix(PawnPath __result, PathPatchState __state)
        {
            if (__state.Started != 0L)
            {
                HotPathProfiler.End("PathFinder.FindPath", __state.Started);
                HotPathProfiler.End(__state.IsTraverseParms ? "PathFinder.FindPath[traverseParms]" : "PathFinder.FindPath[pawn]", __state.Started);
            }
            if (__state.RequestId != 0)
                PathSnapshotWorker.RecordVanilla(__state.RequestId, __result);
        }

        public static void JobGiverPrefix(ref long __state)
        {
            WorkScanProduction.EnterJobGiver();
            __state = FeatureGate.IsEnabled("diagnostics.hotPaths") ? HotPathProfiler.Begin() : 0L;
        }

        public static void JobGiverPostfix(long __state)
        {
            if (__state != 0L)
                HotPathProfiler.End("JobGiver_Work.TryIssueJobPackage", __state);
        }
    }
}
