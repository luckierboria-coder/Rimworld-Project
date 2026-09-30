using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class CleanPathfindingGlowCache093T34D3
{
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
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Expected O, but got Unknown
		//IL_00d5: Expected O, but got Unknown
		try
		{
			Type type = AccessTools.TypeByName("CleanPathfinding.CleanPathfindingUtility");
			if (type == null)
			{
				return;
			}
			MethodInfo methodInfo = AccessTools.Method(type, "<AdjustCosts>g__GameGlowAtFast|14_1", (Type[])null, (Type[])null);
			if (!(methodInfo == null) && !(methodInfo.ReturnType != typeof(float)))
			{
				ParameterInfo[] parameters = methodInfo.GetParameters();
				if (parameters.Length == 2 && !(parameters[0].ParameterType != typeof(Map)) && !(parameters[1].ParameterType != typeof(int)))
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(CleanPathfindingGlowCache093T34D3), "Prefix", (Type[])null)
					{
						priority = 800
					}, new HarmonyMethod(typeof(CleanPathfindingGlowCache093T34D3), "Postfix", (Type[])null)
					{
						priority = 0
					}, (HarmonyMethod)null, (HarmonyMethod)null);
					installed = true;
					Log.Message("[RimMT] T34-D.3 Clean Pathfinding glow cache installed (30-tick bounded reuse).");
				}
			}
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
			if (val == null || num3 < 0 || num3 >= val.cellIndices.NumGridCells)
			{
				return true;
			}
			TickManager tickManager = Find.TickManager;
			int num4 = ((tickManager != null) ? tickManager.TicksGame : 0) / 30 + 1;
			lock (Sync)
			{
				if (!Maps.TryGetValue(val.uniqueID, out var value) || value.CellCount != val.cellIndices.NumGridCells)
				{
					value = new MapCache(val.cellIndices.NumGridCells);
					Maps[val.uniqueID] = value;
				}
				if (value.Generations[num3] != num4)
				{
					return true;
				}
				__result = value.Values[num3];
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
		if (!__state || val == null || num3 < 0)
		{
			return;
		}
		try
		{
			int numGridCells = val.cellIndices.NumGridCells;
			if (num3 >= numGridCells)
			{
				return;
			}
			TickManager tickManager = Find.TickManager;
			int num4 = ((tickManager != null) ? tickManager.TicksGame : 0) / 30 + 1;
			lock (Sync)
			{
				if (!Maps.TryGetValue(val.uniqueID, out var value) || value.CellCount != numGridCells)
				{
					value = new MapCache(numGridCells);
					Maps[val.uniqueID] = value;
				}
				value.Values[num3] = __result;
				value.Generations[num3] = num4;
			}
			Interlocked.Increment(ref stores);
		}
		catch
		{
			Interlocked.Increment(ref failures);
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref lookups);
		long num2 = Interlocked.Read(ref hits);
		double num3 = ((num == 0L) ? 0.0 : ((double)num2 * 100.0 / (double)num));
		return "T34-D.3 Clean Pathfinding glow cache: installed=" + installed + ", lookups=" + num + ", hits=" + num2 + ", stores=" + Interlocked.Read(ref stores) + ", hitRate=" + num3.ToString("F2") + "%, failures=" + Interlocked.Read(ref failures) + ". Clean Pathfinding remains authoritative for final path cost.";
	}
}
