using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class CleanPathfindingGlowCache093T34D3
{
	private const int CacheTicks = 30;
	private static readonly object Sync = new object();
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

	public static bool Prefix(Map val, int num3, ref float __result, ref bool __state)
	{
		__state = true;
		Interlocked.Increment(ref lookups);
		try
		{
			if (val == null || num3 < 0 || num3 >= val.cellIndices.NumGridCells) return true;
			int generation = ((Find.TickManager?.TicksGame) ?? 0) / CacheTicks + 1;
			lock (Sync)
			{
				MapCache cache;
				if (!Maps.TryGetValue(val.uniqueID, out cache) || cache.CellCount != val.cellIndices.NumGridCells)
				{
					cache = new MapCache(val.cellIndices.NumGridCells);
					Maps[val.uniqueID] = cache;
				}
				if (cache.Generations[num3] != generation) return true;
				__result = cache.Values[num3];
			}
			__state = false;
			Interlocked.Increment(ref hits);
			return false;
		}
		catch
		{
			Interlocked.Increment(ref failures);
			return true;
		}
	}

	public static void Postfix(Map val, int num3, float __result, bool __state)
	{
		if (!__state || val == null || num3 < 0) return;
		try
		{
			int count = val.cellIndices.NumGridCells;
			if (num3 >= count) return;
			int generation = ((Find.TickManager?.TicksGame) ?? 0) / CacheTicks + 1;
			lock (Sync)
			{
				MapCache cache;
				if (!Maps.TryGetValue(val.uniqueID, out cache) || cache.CellCount != count)
				{
					cache = new MapCache(count);
					Maps[val.uniqueID] = cache;
				}
				cache.Values[num3] = __result;
				cache.Generations[num3] = generation;
			}
			Interlocked.Increment(ref stores);
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
