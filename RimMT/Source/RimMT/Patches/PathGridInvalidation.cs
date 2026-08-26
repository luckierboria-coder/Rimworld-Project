using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class PathGridInvalidation
    {
        private static readonly MethodBase CellWrapper = AccessTools.Method(typeof(PathGrid), "RecalculatePerceivedPathCostAt", new Type[] { typeof(IntVec3) });
        private static readonly MethodBase CellCore = AccessTools.Method(typeof(PathGrid), "RecalculatePerceivedPathCostAt", new Type[] { typeof(IntVec3), typeof(bool).MakeByRefType() });
        private static readonly MethodBase BulkMethod = AccessTools.Method(typeof(PathGrid), "RecalculateAllPerceivedPathCosts");

        [ThreadStatic]
        private static int bulkDepth;

        private static long cellInvalidations;
        private static long bulkInvalidations;
        private static long skippedWrapperCallbacks;
        private static long skippedBulkCellCallbacks;

        internal static void ApplyBulkGuard(Harmony harmony)
        {
            if (harmony == null || BulkMethod == null)
                return;

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(PathGridInvalidation), nameof(BulkPrefix));
                prefix.priority = Priority.First;
                HarmonyMethod postfix = new HarmonyMethod(typeof(PathGridInvalidation), nameof(BulkPostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(BulkMethod, prefix: prefix, postfix: postfix);
                Log.Message("[RimMT] ai.pathTopology V0.4.5 bulk guard active: full PathGrid recalculation now produces one topology generation instead of per-cell invalidation storms.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] ai.pathTopology bulk guard could not be installed; legacy fail-safe invalidation remains. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void BulkPrefix()
        {
            bulkDepth++;
        }

        public static void BulkPostfix()
        {
            if (bulkDepth > 0)
                bulkDepth--;
            Interlocked.Increment(ref bulkInvalidations);
            ReachabilityNoCache.InvalidateTopology();
        }

        // RimMTPatches intentionally still patches every 1.5 overload fail-closed. This postfix
        // filters nested overloads so one real cell update becomes one generation change, while
        // the bulk guard suppresses the thousands of nested cell callbacks during a full rebuild.
        public static void Postfix(MethodBase __originalMethod)
        {
            if (__originalMethod == null)
                return;

            if (BulkMethod != null && __originalMethod.Equals(BulkMethod))
                return; // BulkPostfix owns the single invalidation for this operation.

            if (CellWrapper != null && __originalMethod.Equals(CellWrapper))
            {
                Interlocked.Increment(ref skippedWrapperCallbacks);
                return; // The one-arg wrapper delegates into CellCore; avoid double counting.
            }

            if (CellCore == null || !__originalMethod.Equals(CellCore))
                return;

            if (bulkDepth > 0)
            {
                Interlocked.Increment(ref skippedBulkCellCallbacks);
                return;
            }

            Interlocked.Increment(ref cellInvalidations);
            ReachabilityNoCache.InvalidateTopology();
        }

        internal static string Summary()
        {
            return "PathGrid invalidation V0.4.5: cell=" + Interlocked.Read(ref cellInvalidations) +
                ", bulk=" + Interlocked.Read(ref bulkInvalidations) +
                ", skippedNestedWrapper=" + Interlocked.Read(ref skippedWrapperCallbacks) +
                ", skippedBulkCells=" + Interlocked.Read(ref skippedBulkCellCallbacks);
        }
    }
}
