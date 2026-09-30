using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{

internal static class CleanPathfindingGlowCache093T34D3
{
	private const int CacheTicks = 30;
	private static readonly Dictionary<int, MapCache> Maps = new Dictionary<int, MapCache>();
	private static long lookups;
	private static long hits;
	private static long stores;
	private static long failures;
	private static bool installed;

	internal static void Apply(Harmony harmony)
	{
		try
		{
			Type utility = AccessTools.TypeByName("CleanPathfinding.CleanPathfindingUtility");
			if (utility == null) return;
			MethodInfo target = AccessTools.Method(utility, "<AdjustCosts>g__GameGlowAtFast|14_1");
			if (target == null || target.ReturnType != typeof(float)) return;
			ParameterInfo[] parameters = target.GetParameters();
			if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Map) || parameters[1].ParameterType != typeof(int)) return;
			harmony.Patch(target,
				new HarmonyMethod(typeof(CleanPathfindingGlowCache093T34D3), nameof(Prefix)) { priority = Priority.First },
				new HarmonyMethod(typeof(CleanPathfindingGlowCache093T34D3), nameof(Postfix)) { priority = Priority.Last });
			installed = true;
			Log.Message("[RimMT] T34-D.3 Clean Pathfinding glow cache installed (30-tick bounded reuse).");
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			Log.Warning("[RimMT] T34-D.3 Clean Pathfinding glow cache failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static bool Prefix(Map __0, int __1, ref float __result, ref bool __state)
	{
		__state = true;
		if (!RimMTThreadGuard.IsMainThread) return true;
		lookups++;
		try
		{
			if (__0 == null || __1 < 0 || __1 >= __0.cellIndices.NumGridCells) return true;
			int generation = ((Find.TickManager?.TicksGame) ?? 0) / CacheTicks + 1;
			MapCache cache;
			if (!Maps.TryGetValue(__0.uniqueID, out cache) || cache.CellCount != __0.cellIndices.NumGridCells)
			{
				cache = new MapCache(__0.cellIndices.NumGridCells);
				Maps[__0.uniqueID] = cache;
			}
			if (cache.Generations[__1] != generation) return true;
			__result = cache.Values[__1];
			__state = false;
			hits++;
			return false;
		}
		catch
		{
			Interlocked.Increment(ref failures);
			return true;
		}
	}

	public static void Postfix(Map __0, int __1, float __result, bool __state)
	{
		if (!__state || !RimMTThreadGuard.IsMainThread || __0 == null || __1 < 0) return;
		try
		{
			int count = __0.cellIndices.NumGridCells;
			if (__1 >= count) return;
			int generation = ((Find.TickManager?.TicksGame) ?? 0) / CacheTicks + 1;
			MapCache cache;
			if (!Maps.TryGetValue(__0.uniqueID, out cache) || cache.CellCount != count)
			{
				cache = new MapCache(count);
				Maps[__0.uniqueID] = cache;
			}
			cache.Values[__1] = __result;
			cache.Generations[__1] = generation;
			stores++;
		}
		catch { Interlocked.Increment(ref failures); }
	}

	internal static string Summary()
	{
		long total = Interlocked.Read(ref lookups);
		long cached = Interlocked.Read(ref hits);
		double rate = total == 0 ? 0.0 : cached * 100.0 / total;
		return "T34-D.3 Clean Pathfinding glow cache: installed=" + installed + ", lookups=" + total +
			", hits=" + cached + ", stores=" + Interlocked.Read(ref stores) + ", hitRate=" + rate.ToString("F2") +
			"%, failures=" + Interlocked.Read(ref failures) + ". Clean Pathfinding remains authoritative for final path cost.";
	}

	private sealed class MapCache
	{
		internal readonly int CellCount;
		internal readonly int[] Generations;
		internal readonly float[] Values;
		internal MapCache(int cellCount)
		{
			CellCount = cellCount;
			Generations = new int[cellCount];
			Values = new float[cellCount];
		}
	}
}

}



