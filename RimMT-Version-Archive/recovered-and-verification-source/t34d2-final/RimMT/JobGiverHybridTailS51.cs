using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

[StaticConstructorOnStartup]
internal static class JobGiverHybridTailS51
{
	private struct Candidate
	{
		internal readonly Thing Thing;

		internal readonly long DistanceSquared;

		internal readonly int SourceIndex;

		internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
		{
			Thing = thing;
			DistanceSquared = distanceSquared;
			SourceIndex = sourceIndex;
		}
	}

	private sealed class CandidateComparer : IComparer<Candidate>
	{
		internal static readonly CandidateComparer Instance = new CandidateComparer();

		public int Compare(Candidate a, Candidate b)
		{
			int num = a.DistanceSquared.CompareTo(b.DistanceSquared);
			if (num == 0)
			{
				return a.SourceIndex.CompareTo(b.SourceIndex);
			}
			return num;
		}
	}

	private const int EarlyThresholdMs = 16;

	private const int KnownFastMax = 127;

	private const int MaxSourceCount = 16384;

	private static readonly long EarlyThresholdTicks;

	[ThreadStatic]
	private static Candidate[] scratch;

	private static int failureLogs;

	private static long observed;

	private static long knownSmall;

	private static long thresholdBypass;

	private static long accelerated;

	private static long acceleratedNull;

	private static long validatorRejected;

	private static long reachRejected;

	private static long failures;

	static JobGiverHybridTailS51()
	{
		EarlyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * 16 / 1000);
		LongEventHandler.ExecuteWhenFinished((Action)Install);
	}

	private static void Install()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Expected O, but got Unknown
		try
		{
			Harmony val = new Harmony("allen.rimmt");
			int num = 0;
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (Supported(methodInfo))
				{
					HarmonyMethod val2 = new HarmonyMethod(typeof(JobGiverHybridTailS51), "RoutePrefix", (Type[])null)
					{
						priority = 1000
					};
					val.Patch((MethodBase)methodInfo, val2, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			Log.Message("[RimMT] Unified Lean S5.1 active on " + num + " ClosestThingReachable overload(s); known <=127 custom sets admit after 16ms.");
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] Unified Lean S5.1 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static bool Supported(MethodInfo method)
	{
		if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable")
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length >= 8 && parameters[0].ParameterType == typeof(IntVec3) && parameters[1].ParameterType == typeof(Map) && parameters[3].ParameterType == typeof(PathEndMode) && parameters[4].ParameterType == typeof(TraverseParms) && parameters[5].ParameterType == typeof(float) && parameters[6].ParameterType == typeof(Predicate<Thing>))
		{
			return typeof(IEnumerable<Thing>).IsAssignableFrom(parameters[7].ParameterType);
		}
		return false;
	}

	public static bool RoutePrefix(IntVec3 __0, Map __1, PathEndMode __3, TraverseParms __4, float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Invalid comparison between Unknown and I4
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Invalid comparison between Unknown and I4
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Invalid comparison between Unknown and I4
		if (__7 == null || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		observed++;
		if (!TryKnownCount(__7, out var count) || count <= 0 || count > 127 || count > 16384)
		{
			return true;
		}
		knownSmall++;
		long currentScopeStartTicks = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
		if (currentScopeStartTicks <= 0 || Stopwatch.GetTimestamp() - currentScopeStartTicks < EarlyThresholdTicks)
		{
			thresholdBypass++;
			return true;
		}
		Pawn pawn = __4.pawn;
		if (__1 == null || __1.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != __1 || !((IntVec3)(ref __0)).IsValid || !GenGrid.InBounds(__0, __1) || __5 <= 0f)
		{
			return true;
		}
		TraverseMode mode = __4.mode;
		if ((int)mode != 0 && (int)mode != 1 && (int)mode != 2)
		{
			return true;
		}
		return TryFast(__7, count, __0, __1, __3, __4, __5, __6, ref __result);
	}

	private static bool TryKnownCount(IEnumerable<Thing> source, out int count)
	{
		if (source is ICollection<Thing> collection)
		{
			count = collection.Count;
			return true;
		}
		if (source is ICollection collection2)
		{
			count = collection2.Count;
			return true;
		}
		count = -1;
		return false;
	}

	private static bool TryFast(IEnumerable<Thing> source, int knownCount, IntVec3 root, Map map, PathEndMode endMode, TraverseParms traverseParms, float maxDistance, Predicate<Thing> validator, ref Thing result)
	{
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Candidate[] array = EnsureScratch(Math.Max(knownCount, 16));
			double num = (double)maxDistance * (double)maxDistance;
			int num2 = 0;
			int num3 = 0;
			foreach (Thing item in source)
			{
				int sourceIndex = num3++;
				if (item == null || !item.Spawned || item.Map != map)
				{
					continue;
				}
				IntVec3 position = item.Position;
				if (!((IntVec3)(ref position)).IsValid)
				{
					continue;
				}
				long num4 = (long)position.x - (long)root.x;
				long num5 = (long)position.z - (long)root.z;
				long num6 = num4 * num4 + num5 * num5;
				if (!((double)num6 > num))
				{
					if (num2 >= array.Length)
					{
						array = EnsureScratch(num2 + 1);
					}
					array[num2++] = new Candidate(item, num6, sourceIndex);
				}
			}
			if (num3 != knownCount)
			{
				return true;
			}
			if (num2 > 1)
			{
				Array.Sort(array, 0, num2, CandidateComparer.Instance);
			}
			for (int i = 0; i < num2; i++)
			{
				Thing thing = array[i].Thing;
				if (validator != null && !validator(thing))
				{
					validatorRejected++;
					continue;
				}
				if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
				{
					reachRejected++;
					continue;
				}
				result = thing;
				accelerated++;
				return false;
			}
			result = null;
			accelerated++;
			acceleratedNull++;
			return false;
		}
		catch (Exception ex)
		{
			failures++;
			if (failureLogs++ < 4)
			{
				Log.Warning("[RimMT] Unified Lean S5.1 fast path failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
			}
			return true;
		}
	}

	internal static string Summary()
	{
		return "S5.1 tail rescue: observed=" + observed + ", knownSmall=" + knownSmall + ", thresholdBypass=" + thresholdBypass + ", accelerated=" + accelerated + ", acceleratedNull=" + acceleratedNull + ", validatorRejected=" + validatorRejected + ", reachRejected=" + reachRejected + ", failures=" + failures + ".";
	}

	private static Candidate[] EnsureScratch(int required)
	{
		Candidate[] array = scratch;
		if (array != null && array.Length >= required)
		{
			return array;
		}
		int num;
		for (num = ((array == null) ? 128 : array.Length); num < required; num <<= 1)
		{
		}
		scratch = new Candidate[num];
		return scratch;
	}
}
