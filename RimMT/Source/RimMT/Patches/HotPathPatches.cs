using System.Diagnostics;

namespace RimMT
{
    internal static class HotPathPatches
    {
        public static void TickPrefix(ref long __state) { __state = Stopwatch.GetTimestamp(); }
        public static void TickPostfix(long __state) { HotPathProfiler.End("TickManager.DoSingleTick", __state); AdaptiveLoadBalancer.RecordTick(__state); }
        public static void PathPrefix(ref long __state) { __state = FeatureGate.IsEnabled("diagnostics.hotPaths") ? HotPathProfiler.Begin() : 0L; }
        public static void PathPostfix(long __state) { if (__state != 0L) HotPathProfiler.End("PathFinder.FindPath", __state); }
        public static void JobGiverPrefix(ref long __state) { __state = FeatureGate.IsEnabled("diagnostics.hotPaths") ? HotPathProfiler.Begin() : 0L; }
        public static void JobGiverPostfix(long __state) { if (__state != 0L) HotPathProfiler.End("JobGiver_Work.TryIssueJobPackage", __state); }
    }
}
