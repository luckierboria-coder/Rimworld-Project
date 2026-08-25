using System.Diagnostics;
using Verse.AI;

namespace RimMT
{
    internal struct PathPatchState
    {
        internal long Started;
        internal int RequestId;
    }

    internal static class HotPathPatches
    {
        public static void TickPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void TickPostfix(long __state)
        {
            HotPathProfiler.End("TickManager.DoSingleTick", __state);
            AdaptiveLoadBalancer.RecordTick(__state);
        }

        public static void PathPrefix(PathFinder __instance, object[] __args, ref PathPatchState __state)
        {
            __state.Started = FeatureGate.IsEnabled("diagnostics.hotPaths") ? HotPathProfiler.Begin() : 0L;
            __state.RequestId = PathSnapshotWorker.TrySchedule(__instance, __args);
        }

        public static void PathPostfix(PawnPath __result, PathPatchState __state)
        {
            if (__state.Started != 0L)
                HotPathProfiler.End("PathFinder.FindPath", __state.Started);
            if (__state.RequestId != 0)
                PathSnapshotWorker.RecordVanilla(__state.RequestId, __result);
        }

        public static void JobGiverPrefix(ref long __state)
        {
            __state = FeatureGate.IsEnabled("diagnostics.hotPaths") ? HotPathProfiler.Begin() : 0L;
        }

        public static void JobGiverPostfix(long __state)
        {
            if (__state != 0L)
                HotPathProfiler.End("JobGiver_Work.TryIssueJobPackage", __state);
        }
    }
}
