using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class CleanPathfindingRegionCensus093T34D32
{
	private sealed class MethodStats
	{
		internal readonly string Name;

		internal long Calls;

		internal long Samples;

		internal long TotalTicks;

		internal long MaxTicks;

		internal MethodStats(string name)
		{
			Name = name;
		}
	}

	private const int SampleMask = 63;

	private static readonly MethodStats Best = new MethodStats("best");

	private static readonly MethodStats Precise = new MethodStats("precise");

	private static readonly MethodStats Distance = new MethodStats("distance");

	private static bool installed;

	private static int installFailures;

	internal static void Apply(Harmony harmony)
	{
		if (harmony == null || AccessTools.TypeByName("CleanPathfinding.CleanPathfindingUtility") == null)
		{
			return;
		}
		try
		{
			Patch(harmony, "GetRegionBestDistances", "BestPrefix", "BestPostfix");
			Patch(harmony, "GetPreciseRegionLinkDistances", "PrecisePrefix", "PrecisePostfix");
			Patch(harmony, "GetRegionDistance", "DistancePrefix", "DistancePostfix");
			installed = true;
			Log.Message("[RimMT] T34-D.3.2 Clean Pathfinding region cost census installed (1/64 timing sample).");
		}
		catch (Exception ex)
		{
			installFailures++;
			Log.Warning("[RimMT] T34-D.3.2 region cost census failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void Patch(Harmony harmony, string targetName, string prefixName, string postfixName)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Expected O, but got Unknown
		//IL_006f: Expected O, but got Unknown
		MethodInfo methodInfo = AccessTools.Method(typeof(RegionCostCalculator), targetName, (Type[])null, (Type[])null);
		if (methodInfo == null)
		{
			throw new MissingMethodException(typeof(RegionCostCalculator).FullName, targetName);
		}
		harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(CleanPathfindingRegionCensus093T34D32), prefixName, (Type[])null)
		{
			priority = 800
		}, new HarmonyMethod(typeof(CleanPathfindingRegionCensus093T34D32), postfixName, (Type[])null)
		{
			priority = 0
		}, (HarmonyMethod)null, (HarmonyMethod)null);
	}

	public static void BestPrefix(ref long __state)
	{
		Begin(Best, ref __state);
	}

	public static void BestPostfix(long __state)
	{
		End(Best, __state);
	}

	public static void PrecisePrefix(ref long __state)
	{
		Begin(Precise, ref __state);
	}

	public static void PrecisePostfix(long __state)
	{
		End(Precise, __state);
	}

	public static void DistancePrefix(ref long __state)
	{
		Begin(Distance, ref __state);
	}

	public static void DistancePostfix(long __state)
	{
		End(Distance, __state);
	}

	private static void Begin(MethodStats stats, ref long state)
	{
		state = 0L;
		if (RimMTThreadGuard.IsMainThread && (Interlocked.Increment(ref stats.Calls) & 0x3F) == 0L)
		{
			state = Stopwatch.GetTimestamp();
		}
	}

	private static void End(MethodStats stats, long state)
	{
		if (state == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - state;
		Interlocked.Increment(ref stats.Samples);
		Interlocked.Add(ref stats.TotalTicks, num);
		long num2 = Interlocked.Read(ref stats.MaxTicks);
		while (num > num2)
		{
			long num3 = Interlocked.CompareExchange(ref stats.MaxTicks, num, num2);
			if (num3 != num2)
			{
				num2 = num3;
				continue;
			}
			break;
		}
	}

	internal static string Summary()
	{
		return "T34-D.3.2 Clean Pathfinding region census: installed=" + installed + ", " + Format(Best) + ", " + Format(Precise) + ", " + Format(Distance) + ", installFailures=" + installFailures + ". Timing sample=1/64; behavior unchanged.";
	}

	private static string Format(MethodStats stats)
	{
		long num = Interlocked.Read(ref stats.Samples);
		long num2 = Interlocked.Read(ref stats.TotalTicks);
		double num3 = ((num == 0L) ? 0.0 : ((double)num2 * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num4 = (double)Interlocked.Read(ref stats.MaxTicks) * 1000.0 / (double)Stopwatch.Frequency;
		return stats.Name + "[calls/samples/avgUs/maxMs]=" + Interlocked.Read(ref stats.Calls) + "/" + num + "/" + num3.ToString("F2") + "/" + num4.ToString("F3");
	}
}
