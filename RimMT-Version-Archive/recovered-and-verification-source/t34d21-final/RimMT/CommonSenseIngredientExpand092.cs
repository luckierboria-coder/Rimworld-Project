using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

[StaticConstructorOnStartup]
internal static class CommonSenseIngredientExpand092
{
	internal struct ExpandState
	{
		internal ExpandKey Key;

		internal int Tick;

		internal long StartTimestamp;

		internal HashSet<int> Before;

		internal bool Valid;
	}

	private sealed class ExpandCache
	{
		internal int Tick;

		internal Thing[] Extras;
	}

	internal struct ExpandKey : IEquatable<ExpandKey>
	{
		private readonly Map map;

		private readonly int pawnId;

		private readonly int relevantCount;

		private readonly int relevantHash;

		private readonly int processedCount;

		internal ExpandKey(Map map, int pawnId, int relevantCount, int relevantHash, int processedCount)
		{
			this.map = map;
			this.pawnId = pawnId;
			this.relevantCount = relevantCount;
			this.relevantHash = relevantHash;
			this.processedCount = processedCount;
		}

		public bool Equals(ExpandKey other)
		{
			if (map == other.map && pawnId == other.pawnId && relevantCount == other.relevantCount && relevantHash == other.relevantHash)
			{
				return processedCount == other.processedCount;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ExpandKey)
			{
				return Equals((ExpandKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (((((((((map != null) ? ((object)map).GetHashCode() : 0) * 397) ^ pawnId) * 397) ^ relevantCount) * 397) ^ relevantHash) * 397) ^ processedCount;
		}
	}

	private const int TtlTicks = 60;

	private const int MaxEntries = 4096;

	private static readonly Dictionary<ExpandKey, ExpandCache> Cache;

	private static FieldInfo preferSpoilingField;

	private static int failureLogs;

	private static bool installed;

	private static long eligibleCalls;

	private static long cacheHits;

	private static long cacheMisses;

	private static long extrasRevalidated;

	private static long extrasRejectedLive;

	private static long extrasReplayed;

	private static long publishes;

	private static long missPathTicks;

	private static long missPathTicksMax;

	private static readonly Thing[] EmptyThings;

	static CommonSenseIngredientExpand092()
	{
		Cache = new Dictionary<ExpandKey, ExpandCache>();
		EmptyThings = (Thing[])(object)new Thing[0];
		LongEventHandler.ExecuteWhenFinished((Action)Install);
	}

	private static void Install()
	{
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Expected O, but got Unknown
		//IL_00bc: Expected O, but got Unknown
		try
		{
			Type type = AccessTools.TypeByName("CommonSense.Utility");
			Type type2 = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestIngredientsHelper_CommonSensePatch");
			if (!(type == null) && !(type2 == null))
			{
				Type type3 = AccessTools.TypeByName("CommonSense.Settings");
				preferSpoilingField = ((type3 == null) ? null : AccessTools.Field(type3, "prefer_spoiling_ingredients"));
				MethodInfo methodInfo = AccessTools.Method(type2, "PreProcess", (Type[])null, (Type[])null);
				if (!(methodInfo == null))
				{
					new Harmony("allen.rimmt").Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(CommonSenseIngredientExpand092), "Prefix", (Type[])null)
					{
						priority = 800
					}, new HarmonyMethod(typeof(CommonSenseIngredientExpand092), "Postfix", (Type[])null)
					{
						priority = 0
					}, (HarmonyMethod)null, (HarmonyMethod)null);
					installed = true;
					Log.Message("[RimMT] Unified Common Sense ingredient-expansion memo active; cached setting metadata, allocation-light miss capture and miss-only timing are enabled.");
				}
			}
		}
		catch (Exception ex)
		{
			installed = false;
			Log.Warning("[RimMT] Common Sense ingredient memo install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static bool Prefix(Pawn pawn, Predicate<Thing> baseValidator, bool billGiverIsPawn, List<Thing> newRelevantThings, HashSet<Thing> processedThings, out ExpandState __state)
	{
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		__state = default(ExpandState);
		try
		{
			if (!RimMTThreadGuard.IsMainThread || !ReadPreferSpoilingSetting() || billGiverIsPawn || pawn == null || ((Thing)pawn).Map == null || baseValidator == null || newRelevantThings == null || processedThings == null)
			{
				return true;
			}
			eligibleCalls++;
			int num = CurrentTick();
			ExpandKey key = BuildKey(pawn, newRelevantThings, processedThings.Count);
			if (Cache.TryGetValue(key, out var value) && value != null && value.Extras != null && num - value.Tick <= 60)
			{
				cacheHits++;
				Thing[] extras = value.Extras;
				for (int i = 0; i < extras.Length; i++)
				{
					extrasRevalidated++;
					Thing val = extras[i];
					if (val == null || val.Destroyed || !val.Spawned || val.Map != ((Thing)pawn).Map || val.def.IsMedicine || processedThings.Contains(val))
					{
						extrasRejectedLive++;
						continue;
					}
					if (!baseValidator(val))
					{
						extrasRejectedLive++;
						continue;
					}
					if (!ReachabilityUtility.CanReach(pawn, LocalTargetInfo.op_Implicit(val), (PathEndMode)1, (Danger)3, false, false, (TraverseMode)0))
					{
						extrasRejectedLive++;
						continue;
					}
					newRelevantThings.Add(val);
					processedThings.Add(val);
					extrasReplayed++;
				}
				return false;
			}
			cacheMisses++;
			__state = new ExpandState
			{
				Key = key,
				Tick = num,
				StartTimestamp = Stopwatch.GetTimestamp(),
				Before = CaptureIds(newRelevantThings),
				Valid = true
			};
			return true;
		}
		catch (Exception ex)
		{
			LogFailure("ingredient memo hit", ex);
			return true;
		}
	}

	public static void Postfix(List<Thing> newRelevantThings, ExpandState __state)
	{
		if (!__state.Valid || newRelevantThings == null)
		{
			return;
		}
		try
		{
			if (__state.StartTimestamp != 0L)
			{
				long num = Stopwatch.GetTimestamp() - __state.StartTimestamp;
				if (num > 0)
				{
					missPathTicks += num;
					if (num > missPathTicksMax)
					{
						missPathTicksMax = num;
					}
				}
			}
			int num2 = 0;
			for (int i = 0; i < newRelevantThings.Count; i++)
			{
				Thing val = newRelevantThings[i];
				if (val != null && !__state.Before.Contains(val.thingIDNumber))
				{
					num2++;
				}
			}
			Thing[] array = (Thing[])((num2 == 0) ? ((Array)EmptyThings) : ((Array)new Thing[num2]));
			int num3 = 0;
			for (int j = 0; j < newRelevantThings.Count; j++)
			{
				if (num3 >= num2)
				{
					break;
				}
				Thing val2 = newRelevantThings[j];
				if (val2 != null && !__state.Before.Contains(val2.thingIDNumber))
				{
					array[num3++] = val2;
				}
			}
			if (Cache.Count >= 4096)
			{
				Cache.Clear();
			}
			Cache[__state.Key] = new ExpandCache
			{
				Tick = __state.Tick,
				Extras = array
			};
			publishes++;
		}
		catch (Exception ex)
		{
			LogFailure("ingredient memo publish", ex);
		}
	}

	private static HashSet<int> CaptureIds(List<Thing> things)
	{
		HashSet<int> hashSet = new HashSet<int>();
		for (int i = 0; i < things.Count; i++)
		{
			Thing val = things[i];
			if (val != null)
			{
				hashSet.Add(val.thingIDNumber);
			}
		}
		return hashSet;
	}

	private static bool ReadPreferSpoilingSetting()
	{
		try
		{
			FieldInfo fieldInfo = preferSpoilingField;
			return fieldInfo != null && fieldInfo.FieldType == typeof(bool) && (bool)fieldInfo.GetValue(null);
		}
		catch
		{
			return false;
		}
	}

	internal static string Summary()
	{
		long num = eligibleCalls;
		long num2 = cacheHits;
		long num3 = cacheMisses;
		double num4 = ((num <= 0) ? 0.0 : ((double)num2 * 100.0 / (double)num));
		double num5 = ((num3 <= 0) ? 0.0 : ((double)missPathTicks * 1000000.0 / (double)Stopwatch.Frequency / (double)num3));
		double num6 = (double)missPathTicksMax * 1000000.0 / (double)Stopwatch.Frequency;
		double num7 = ((num2 <= 0) ? 0.0 : (num5 * (double)num2 / 1000.0));
		return "CommonSense ingredient memo: installed=" + installed + ", eligibleCalls=" + num + ", hits=" + num2 + ", misses=" + num3 + ", hitRate=" + num4.ToString("F2") + "%, extrasRevalidated=" + extrasRevalidated + ", extrasRejectedLive=" + extrasRejectedLive + ", extrasReplayed=" + extrasReplayed + ", publishes=" + publishes + ", entries=" + Cache.Count + ", missPathAvgUs=" + num5.ToString("F2") + ", missPathMaxUs=" + num6.ToString("F2") + ", estimatedAvoidedMs=" + num7.ToString("F1") + ".";
	}

	private static ExpandKey BuildKey(Pawn pawn, List<Thing> relevant, int processedCount)
	{
		int num = 17;
		int num2 = 0;
		for (int i = 0; i < relevant.Count; i++)
		{
			Thing val = relevant[i];
			if (val != null)
			{
				num = num * 31 + val.thingIDNumber;
				num2++;
			}
		}
		return new ExpandKey(((Thing)pawn).Map, ((Thing)pawn).thingIDNumber, num2, num, processedCount);
	}

	private static int CurrentTick()
	{
		try
		{
			return (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
		}
		catch
		{
			return 0;
		}
	}

	private static void LogFailure(string where, Exception ex)
	{
		if (failureLogs++ < 4)
		{
			Log.Warning("[RimMT] Common Sense " + where + " failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}
}
