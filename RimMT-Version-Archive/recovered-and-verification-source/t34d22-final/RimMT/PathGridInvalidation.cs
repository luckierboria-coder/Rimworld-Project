using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class PathGridInvalidation
{
	private static readonly MethodBase CellWrapper = AccessTools.Method(typeof(PathGrid), "RecalculatePerceivedPathCostAt", new Type[1] { typeof(IntVec3) }, (Type[])null);

	private static readonly MethodBase CellCore = AccessTools.Method(typeof(PathGrid), "RecalculatePerceivedPathCostAt", new Type[2]
	{
		typeof(IntVec3),
		typeof(bool).MakeByRefType()
	}, (Type[])null);

	private static readonly MethodBase BulkMethod = AccessTools.Method(typeof(PathGrid), "RecalculateAllPerceivedPathCosts", (Type[])null, (Type[])null);

	[ThreadStatic]
	private static int bulkDepth;

	private static long cellInvalidations;

	private static long bulkInvalidations;

	private static long skippedWrapperCallbacks;

	private static long skippedBulkCellCallbacks;

	internal static void ApplyBulkGuard(Harmony harmony)
	{
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Expected O, but got Unknown
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Expected O, but got Unknown
		if (harmony == null || BulkMethod == null)
		{
			return;
		}
		try
		{
			HarmonyMethod val = new HarmonyMethod(typeof(PathGridInvalidation), "BulkPrefix", (Type[])null);
			val.priority = 800;
			HarmonyMethod val2 = new HarmonyMethod(typeof(PathGridInvalidation), "BulkPostfix", (Type[])null);
			val2.priority = 0;
			harmony.Patch(BulkMethod, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
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
		{
			bulkDepth--;
		}
		Interlocked.Increment(ref bulkInvalidations);
		ReachabilityNoCache.InvalidateTopology();
	}

	public static void Postfix(MethodBase __originalMethod)
	{
		if (__originalMethod == null || (BulkMethod != null && __originalMethod.Equals(BulkMethod)))
		{
			return;
		}
		if (CellWrapper != null && __originalMethod.Equals(CellWrapper))
		{
			Interlocked.Increment(ref skippedWrapperCallbacks);
		}
		else if (!(CellCore == null) && __originalMethod.Equals(CellCore))
		{
			if (bulkDepth > 0)
			{
				Interlocked.Increment(ref skippedBulkCellCallbacks);
				return;
			}
			Interlocked.Increment(ref cellInvalidations);
			ReachabilityNoCache.InvalidateTopology();
		}
	}

	internal static string Summary()
	{
		return "PathGrid invalidation V0.4.5: cell=" + Interlocked.Read(ref cellInvalidations) + ", bulk=" + Interlocked.Read(ref bulkInvalidations) + ", skippedNestedWrapper=" + Interlocked.Read(ref skippedWrapperCallbacks) + ", skippedBulkCells=" + Interlocked.Read(ref skippedBulkCellCallbacks);
	}
}
