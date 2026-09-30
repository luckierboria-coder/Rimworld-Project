using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class GlobalHaulAccelerator
{
	private sealed class MapState
	{
		internal int MapId;

		internal int Width;

		internal int Height;

		internal int Generation;

		internal bool BuildInFlight;

		internal bool EverBuilt;

		internal SpatialIndex Index;
	}

	private sealed class SpatialIndex
	{
		private readonly int mapId;

		private readonly int width;

		private readonly int height;

		private readonly int bucketCols;

		private readonly int bucketRows;

		private readonly Dictionary<int, List<Thing>> buckets = new Dictionary<int, List<Thing>>();

		private readonly Dictionary<int, int> thingBucketById = new Dictionary<int, int>();

		private readonly Dictionary<int, Thing> thingById = new Dictionary<int, Thing>();

		internal int Generation;

		internal int Count => thingBucketById.Count;

		private SpatialIndex(int mapId, int width, int height, int generation)
		{
			this.mapId = mapId;
			this.width = width;
			this.height = height;
			Generation = generation;
			bucketCols = Math.Max(1, (width + 16 - 1) / 16);
			bucketRows = Math.Max(1, (height + 16 - 1) / 16);
		}

		internal static SpatialIndex Build(int mapId, int width, int height, int generation, Thing[] things, int[] ids, int[] xs, int[] zs)
		{
			SpatialIndex spatialIndex = new SpatialIndex(mapId, width, height, generation);
			int num = ((things != null) ? things.Length : 0);
			for (int i = 0; i < num; i++)
			{
				int num2 = ids[i];
				int num3 = xs[i];
				int num4 = zs[i];
				if (num2 >= 0 && num3 >= 0 && num4 >= 0 && num3 < width && num4 < height)
				{
					int num5 = spatialIndex.BucketKey(num3, num4);
					if (!spatialIndex.buckets.TryGetValue(num5, out var value))
					{
						value = new List<Thing>();
						spatialIndex.buckets.Add(num5, value);
					}
					value.Add(things[i]);
					spatialIndex.thingBucketById[num2] = num5;
					spatialIndex.thingById[num2] = things[i];
				}
			}
			return spatialIndex;
		}

		internal bool Add(Thing thing, Map map)
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0026: Unknown result type (might be due to invalid IL or missing references)
			//IL_0041: Unknown result type (might be due to invalid IL or missing references)
			//IL_0047: Unknown result type (might be due to invalid IL or missing references)
			if (thing == null || map == null || map.uniqueID != mapId)
			{
				return false;
			}
			IntVec3 positionHeld = thing.PositionHeld;
			if (!((IntVec3)(ref positionHeld)).IsValid || !GenGrid.InBounds(positionHeld, map))
			{
				return false;
			}
			int thingIDNumber = thing.thingIDNumber;
			RemoveById(thingIDNumber);
			int num = BucketKey(positionHeld.x, positionHeld.z);
			if (!buckets.TryGetValue(num, out var value))
			{
				value = new List<Thing>();
				buckets.Add(num, value);
			}
			value.Add(thing);
			thingBucketById[thingIDNumber] = num;
			thingById[thingIDNumber] = thing;
			return true;
		}

		internal bool Remove(Thing thing)
		{
			if (thing != null)
			{
				return RemoveById(thing.thingIDNumber);
			}
			return false;
		}

		private bool RemoveById(int id)
		{
			if (!thingBucketById.TryGetValue(id, out var value))
			{
				return false;
			}
			thingById.TryGetValue(id, out var value2);
			if (buckets.TryGetValue(value, out var value3))
			{
				if (value2 != null)
				{
					value3.Remove(value2);
				}
				if (value3.Count == 0)
				{
					buckets.Remove(value);
				}
			}
			thingBucketById.Remove(id);
			thingById.Remove(id);
			return true;
		}

		internal bool TryFindClosest(IntVec3 root, Map map, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, out Thing chosen, out int visited, out int bucketsSeen, out int reaches, out int validations)
		{
			//IL_002c: Unknown result type (might be due to invalid IL or missing references)
			//IL_003f: Unknown result type (might be due to invalid IL or missing references)
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
			//IL_0115: Unknown result type (might be due to invalid IL or missing references)
			//IL_0117: Unknown result type (might be due to invalid IL or missing references)
			//IL_0118: Unknown result type (might be due to invalid IL or missing references)
			//IL_013d: Unknown result type (might be due to invalid IL or missing references)
			//IL_013f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0140: Unknown result type (might be due to invalid IL or missing references)
			//IL_0173: Unknown result type (might be due to invalid IL or missing references)
			//IL_0175: Unknown result type (might be due to invalid IL or missing references)
			//IL_0176: Unknown result type (might be due to invalid IL or missing references)
			//IL_019b: Unknown result type (might be due to invalid IL or missing references)
			//IL_019d: Unknown result type (might be due to invalid IL or missing references)
			//IL_019e: Unknown result type (might be due to invalid IL or missing references)
			chosen = null;
			visited = 0;
			bucketsSeen = 0;
			reaches = 0;
			validations = 0;
			if (map == null || map.uniqueID != mapId || width != map.Size.x || height != map.Size.z || !GenGrid.InBounds(root, map))
			{
				return false;
			}
			float num = maxDistance * maxDistance;
			float bestDistanceSquared = float.MaxValue;
			int num2 = root.x / 16;
			int num3 = root.z / 16;
			int num4 = Math.Max(Math.Max(num2, bucketCols - 1 - num2), Math.Max(num3, bucketRows - 1 - num3));
			for (int i = 0; i <= num4; i++)
			{
				int num5 = Math.Max(0, num2 - i);
				int num6 = Math.Min(bucketCols - 1, num2 + i);
				int num7 = Math.Max(0, num3 - i);
				int num8 = Math.Min(bucketRows - 1, num3 + i);
				if (i == 0)
				{
					if (!ProcessBucket(num2, num3, root, map, peMode, traverseParams, num, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
					{
						return false;
					}
				}
				else
				{
					for (int j = num5; j <= num6; j++)
					{
						if (!ProcessBucket(j, num7, root, map, peMode, traverseParams, num, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
						{
							return false;
						}
						if (num8 != num7 && !ProcessBucket(j, num8, root, map, peMode, traverseParams, num, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
						{
							return false;
						}
					}
					for (int k = num7 + 1; k < num8; k++)
					{
						if (!ProcessBucket(num5, k, root, map, peMode, traverseParams, num, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
						{
							return false;
						}
						if (num6 != num5 && !ProcessBucket(num6, k, root, map, peMode, traverseParams, num, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
						{
							return false;
						}
					}
				}
				float num9 = MinimumOutsideDistanceSquared(root, num5, num6, num7, num8);
				if ((chosen != null && num9 > bestDistanceSquared) || (chosen == null && num9 > num))
				{
					break;
				}
			}
			return true;
		}

		private bool ProcessBucket(int bx, int bz, IntVec3 root, Map map, PathEndMode peMode, TraverseParms traverseParams, float maxDistanceSquared, Predicate<Thing> validator, ref Thing chosen, ref float bestDistanceSquared, ref int visited, ref int bucketsSeen, ref int reaches, ref int validations)
		{
			//IL_0070: Unknown result type (might be due to invalid IL or missing references)
			//IL_0075: Unknown result type (might be due to invalid IL or missing references)
			//IL_0080: Unknown result type (might be due to invalid IL or missing references)
			//IL_008c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0093: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00db: Unknown result type (might be due to invalid IL or missing references)
			//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
			if (bx < 0 || bz < 0 || bx >= bucketCols || bz >= bucketRows)
			{
				return true;
			}
			int num = bx + bz * bucketCols;
			if (!buckets.TryGetValue(num, out var value))
			{
				return true;
			}
			bucketsSeen++;
			for (int i = 0; i < value.Count; i++)
			{
				Thing val = value[i];
				visited++;
				if (val == null)
				{
					return false;
				}
				if (!val.Spawned && !HaulAIUtility.IsInHaulableInventory(val))
				{
					return false;
				}
				IntVec3 positionHeld = val.PositionHeld;
				if (!((IntVec3)(ref positionHeld)).IsValid || !GenGrid.InBounds(positionHeld, map) || BucketKey(positionHeld.x, positionHeld.z) != num)
				{
					return false;
				}
				IntVec3 val2 = root - positionHeld;
				float num2 = ((IntVec3)(ref val2)).LengthHorizontalSquared;
				if (num2 > maxDistanceSquared || num2 >= bestDistanceSquared)
				{
					continue;
				}
				reaches++;
				if (!map.reachability.CanReach(root, LocalTargetInfo.op_Implicit(val), peMode, traverseParams))
				{
					continue;
				}
				if (validator != null)
				{
					validations++;
					if (!validator(val))
					{
						continue;
					}
				}
				chosen = val;
				bestDistanceSquared = num2;
			}
			return true;
		}

		private int BucketKey(int x, int z)
		{
			return x / 16 + z / 16 * bucketCols;
		}

		private float MinimumOutsideDistanceSquared(IntVec3 root, int minBx, int maxBx, int minBz, int maxBz)
		{
			//IL_0047: Unknown result type (might be due to invalid IL or missing references)
			//IL_0070: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
			int num = minBx * 16;
			int num2 = Math.Min(width - 1, (maxBx + 1) * 16 - 1);
			int num3 = minBz * 16;
			int num4 = Math.Min(height - 1, (maxBz + 1) * 16 - 1);
			long num5 = long.MaxValue;
			if (minBx > 0)
			{
				long num6 = root.x - (num - 1);
				num5 = Math.Min(num5, num6 * num6);
			}
			if (maxBx < bucketCols - 1)
			{
				long num7 = num2 + 1 - root.x;
				num5 = Math.Min(num5, num7 * num7);
			}
			if (minBz > 0)
			{
				long num8 = root.z - (num3 - 1);
				num5 = Math.Min(num5, num8 * num8);
			}
			if (maxBz < bucketRows - 1)
			{
				long num9 = num4 + 1 - root.z;
				num5 = Math.Min(num5, num9 * num9);
			}
			if (num5 != long.MaxValue)
			{
				return num5;
			}
			return float.MaxValue;
		}
	}

	private const string FeatureId = "parallel.haulGlobal";

	private const int BucketSize = 16;

	private const int MinCandidateCount = 24;

	private static readonly object Sync = new object();

	private static readonly Dictionary<int, MapState> States = new Dictionary<int, MapState>();

	private static readonly FieldInfo ListerMapField = AccessTools.Field(typeof(ListerHaulables), "map");

	private static volatile bool compatibilityReady;

	private static long observedCalls;

	private static long eligibleCalls;

	private static long acceleratedCalls;

	private static long acceleratedNoResult;

	private static long fallbackCalls;

	private static long wrongSearchSetFallbacks;

	private static long priorityFallbacks;

	private static long smallSetFallbacks;

	private static long noIndexFallbacks;

	private static long staleFallbacks;

	private static long buildsScheduled;

	private static long buildsPublished;

	private static long buildsDiscarded;

	private static long buildsRejected;

	private static long rebuilds;

	private static long incrementalAdds;

	private static long incrementalRemoves;

	private static long invalidations;

	private static long bucketVisits;

	private static long candidatesVisited;

	private static long candidatesAvoided;

	private static long reachabilityChecks;

	private static long validatorChecks;

	private static long failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			int num = 0;
			List<MethodInfo> declaredMethods = AccessTools.GetDeclaredMethods(typeof(GenClosest));
			for (int i = 0; i < declaredMethods.Count; i++)
			{
				MethodInfo methodInfo = declaredMethods[i];
				if (IsSupportedTarget(methodInfo))
				{
					CompatibilityGuard.RegisterTarget("parallel.haulGlobal", methodInfo);
					HarmonyMethod val = new HarmonyMethod(typeof(GlobalHaulAccelerator), "ClosestThingGlobalReachablePrefix", (Type[])null);
					val.priority = 800;
					harmony.Patch((MethodBase)methodInfo, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			if (num == 0)
			{
				FeatureGate.Suppress("parallel.haulGlobal", "ClosestThing_Global_Reachable target not found");
				Log.Warning("[RimMT] parallel.haulGlobal V0.4.7 unavailable: compatible GenClosest.ClosestThing_Global_Reachable overload not found.");
				return;
			}
			PatchListerMutation(harmony, "Check");
			PatchListerMutation(harmony, "CheckAdd");
			PatchListerMutation(harmony, "TryRemove");
			Log.Message("[RimMT] parallel.haulGlobal V0.4.7 installed on " + num + " GenClosest.ClosestThing_Global_Reachable overload(s). Exact ListerHaulables calls may use the worker-built spatial index; live reachability and validators remain main-thread authoritative.");
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress("parallel.haulGlobal", "global haul accelerator patch failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] parallel.haulGlobal V0.4.7 patch failed; Vanilla global hauling remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static bool IsSupportedTarget(MethodInfo method)
	{
		if (method == null || method.Name != "ClosestThing_Global_Reachable" || method.ReturnType != typeof(Thing))
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length != 8)
		{
			return false;
		}
		if (parameters[0].ParameterType == typeof(IntVec3) && parameters[1].ParameterType == typeof(Map) && typeof(IEnumerable<Thing>).IsAssignableFrom(parameters[2].ParameterType) && parameters[3].ParameterType == typeof(PathEndMode) && parameters[4].ParameterType == typeof(TraverseParms) && parameters[5].ParameterType == typeof(float))
		{
			return parameters[6].ParameterType == typeof(Predicate<Thing>);
		}
		return false;
	}

	private static void PatchListerMutation(Harmony harmony, string methodName)
	{
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Expected O, but got Unknown
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Expected O, but got Unknown
		MethodBase methodBase = AccessTools.Method(typeof(ListerHaulables), methodName, new Type[1] { typeof(Thing) }, (Type[])null);
		if (methodBase == null)
		{
			throw new MissingMethodException(typeof(ListerHaulables).FullName, methodName);
		}
		HarmonyMethod val = new HarmonyMethod(typeof(GlobalHaulAccelerator), "ListerMutationPrefix", (Type[])null);
		HarmonyMethod val2 = new HarmonyMethod(typeof(GlobalHaulAccelerator), "ListerMutationPostfix", (Type[])null);
		harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
	}

	internal static void MarkCompatibilityReady()
	{
		compatibilityReady = true;
	}

	public static bool ClosestThingGlobalReachablePrefix(ref Thing __result, object[] __args)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Invalid comparison between Unknown and I4
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_018b: Unknown result type (might be due to invalid IL or missing references)
		//IL_018d: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		Interlocked.Increment(ref observedCalls);
		if (!compatibilityReady || !FeatureGate.IsEnabled("parallel.haulGlobal") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || __args == null || __args.Length != 8)
		{
			return true;
		}
		try
		{
			IntVec3 val = (IntVec3)__args[0];
			object obj = __args[1];
			Map val2 = (Map)((obj is Map) ? obj : null);
			IEnumerable<Thing> enumerable = __args[2] as IEnumerable<Thing>;
			PathEndMode peMode = (PathEndMode)__args[3];
			TraverseParms traverseParams = (TraverseParms)__args[4];
			float maxDistance = (float)__args[5];
			Predicate<Thing> validator = __args[6] as Predicate<Thing>;
			object obj2 = __args[7];
			if (val2 == null || val2.Disposed || !((IntVec3)(ref val)).IsValid || !GenGrid.InBounds(val, val2) || enumerable == null)
			{
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			if (obj2 != null)
			{
				Interlocked.Increment(ref priorityFallbacks);
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			List<Thing> list = ((val2.listerHaulables == null) ? null : val2.listerHaulables.ThingsPotentiallyNeedingHauling());
			if (list == null || enumerable != list)
			{
				Interlocked.Increment(ref wrongSearchSetFallbacks);
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			Interlocked.Increment(ref eligibleCalls);
			if (list.Count < 24)
			{
				Interlocked.Increment(ref smallSetFallbacks);
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			MapState state = GetState(val2);
			SpatialIndex usableIndex = GetUsableIndex(state, list);
			if (usableIndex == null)
			{
				EnsureIndexBuildScheduled(val2, state, list);
				Interlocked.Increment(ref noIndexFallbacks);
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			if (!usableIndex.TryFindClosest(val, val2, peMode, traverseParams, maxDistance, validator, out var chosen, out var visited, out var bucketsSeen, out var reaches, out var validations))
			{
				InvalidateIndex(state);
				EnsureIndexBuildScheduled(val2, state, list);
				Interlocked.Increment(ref staleFallbacks);
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			__result = chosen;
			Interlocked.Increment(ref acceleratedCalls);
			if (chosen == null)
			{
				Interlocked.Increment(ref acceleratedNoResult);
			}
			Interlocked.Add(ref bucketVisits, bucketsSeen);
			Interlocked.Add(ref candidatesVisited, visited);
			Interlocked.Add(ref reachabilityChecks, reaches);
			Interlocked.Add(ref validatorChecks, validations);
			long num = list.Count - visited;
			if (num > 0)
			{
				Interlocked.Add(ref candidatesAvoided, num);
			}
			return false;
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			Interlocked.Increment(ref fallbackCalls);
			CircuitBreaker.RecordFailure("parallel.haulGlobal", ex);
			Log.Warning("[RimMT] parallel.haulGlobal V0.4.7 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
			return true;
		}
	}

	public static void ListerMutationPrefix(ListerHaulables __instance, ref int __state)
	{
		__state = -1;
		if (__instance == null || !RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		try
		{
			__state = __instance.ThingsPotentiallyNeedingHauling().Count;
		}
		catch
		{
			__state = -1;
		}
	}

	public static void ListerMutationPostfix(ListerHaulables __instance, Thing t, int __state)
	{
		if (__instance == null || __state < 0 || !RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		try
		{
			int count = __instance.ThingsPotentiallyNeedingHauling().Count;
			if (count == __state)
			{
				return;
			}
			Map val = (Map)((ListerMapField == null) ? null : /*isinst with value type is only supported in some contexts*/);
			if (val == null || val.Disposed)
			{
				return;
			}
			MapState state = GetState(val);
			state.Generation++;
			SpatialIndex index = state.Index;
			if (index == null)
			{
				return;
			}
			bool flag;
			if (count > __state)
			{
				flag = index.Add(t, val);
				if (flag)
				{
					Interlocked.Increment(ref incrementalAdds);
				}
			}
			else
			{
				flag = index.Remove(t);
				if (flag)
				{
					Interlocked.Increment(ref incrementalRemoves);
				}
			}
			if (!flag || index.Count != count)
			{
				InvalidateIndex(state);
			}
			else
			{
				index.Generation = state.Generation;
			}
		}
		catch
		{
			Interlocked.Increment(ref failures);
		}
	}

	private static MapState GetState(Map map)
	{
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		lock (Sync)
		{
			if (!States.TryGetValue(map.uniqueID, out var value))
			{
				value = new MapState();
				value.MapId = map.uniqueID;
				value.Width = map.Size.x;
				value.Height = map.Size.z;
				States.Add(map.uniqueID, value);
			}
			return value;
		}
	}

	private static SpatialIndex GetUsableIndex(MapState state, List<Thing> haulables)
	{
		SpatialIndex index = state.Index;
		if (index == null)
		{
			return null;
		}
		if (index.Generation != state.Generation || index.Count != haulables.Count)
		{
			InvalidateIndex(state);
			return null;
		}
		return index;
	}

	private static void EnsureIndexBuildScheduled(Map map, MapState state, List<Thing> haulables)
	{
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		if (state.BuildInFlight || haulables == null || haulables.Count < 24)
		{
			return;
		}
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler == null)
		{
			return;
		}
		int count = haulables.Count;
		Thing[] things = (Thing[])(object)new Thing[count];
		int[] ids = new int[count];
		int[] xs = new int[count];
		int[] zs = new int[count];
		for (int i = 0; i < count; i++)
		{
			Thing val = haulables[i];
			things[i] = val;
			if (val == null)
			{
				ids[i] = -1;
				xs[i] = int.MinValue;
				zs[i] = int.MinValue;
			}
			else
			{
				ids[i] = val.thingIDNumber;
				IntVec3 positionHeld = val.PositionHeld;
				xs[i] = positionHeld.x;
				zs[i] = positionHeld.z;
			}
		}
		int mapId = map.uniqueID;
		int width = map.Size.x;
		int height = map.Size.z;
		int generation = state.Generation;
		state.BuildInFlight = true;
		Interlocked.Increment(ref buildsScheduled);
		if (state.EverBuilt)
		{
			Interlocked.Increment(ref rebuilds);
		}
		if (!scheduler.TryEnqueue("parallel.haulGlobal", JobPriority.Normal, delegate
		{
			SpatialIndex built = SpatialIndex.Build(mapId, width, height, generation, things, ids, xs, zs);
			MainThreadDispatcher.TryEnqueue(delegate
			{
				state.BuildInFlight = false;
				if (map.Disposed || map.uniqueID != mapId || state.Generation != generation)
				{
					Interlocked.Increment(ref buildsDiscarded);
				}
				else
				{
					state.Index = built;
					state.EverBuilt = true;
					Interlocked.Increment(ref buildsPublished);
				}
			});
		}))
		{
			state.BuildInFlight = false;
			Interlocked.Increment(ref buildsRejected);
		}
	}

	private static void InvalidateIndex(MapState state)
	{
		if (state != null && state.Index != null)
		{
			state.Index = null;
			Interlocked.Increment(ref invalidations);
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref acceleratedCalls);
		long num2 = Interlocked.Read(ref candidatesVisited);
		long num3 = Interlocked.Read(ref candidatesAvoided);
		double num4 = ((num <= 0) ? 0.0 : ((double)num2 / (double)num));
		double num5 = ((num <= 0) ? 0.0 : ((double)num3 / (double)num));
		return "Global haul production V0.4.7: compatibilityReady=" + compatibilityReady + ", observed=" + Interlocked.Read(ref observedCalls) + ", eligible=" + Interlocked.Read(ref eligibleCalls) + ", accelerated=" + num + ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) + ", fallback=" + Interlocked.Read(ref fallbackCalls) + ", wrongSet=" + Interlocked.Read(ref wrongSearchSetFallbacks) + ", priority=" + Interlocked.Read(ref priorityFallbacks) + ", smallSet=" + Interlocked.Read(ref smallSetFallbacks) + ", noIndex=" + Interlocked.Read(ref noIndexFallbacks) + ", stale=" + Interlocked.Read(ref staleFallbacks) + ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) + ", buildsPublished=" + Interlocked.Read(ref buildsPublished) + ", buildsDiscarded=" + Interlocked.Read(ref buildsDiscarded) + ", buildsRejected=" + Interlocked.Read(ref buildsRejected) + ", rebuilds=" + Interlocked.Read(ref rebuilds) + ", incrementalAdds=" + Interlocked.Read(ref incrementalAdds) + ", incrementalRemoves=" + Interlocked.Read(ref incrementalRemoves) + ", invalidations=" + Interlocked.Read(ref invalidations) + ", bucketVisits=" + Interlocked.Read(ref bucketVisits) + ", candidatesVisited=" + num2 + ", avgCandidatesVisited=" + num4.ToString("F1") + ", candidatesAvoided=" + num3 + ", avgCandidatesAvoided=" + num5.ToString("F1") + ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) + ", validatorChecks=" + Interlocked.Read(ref validatorChecks) + ", failures=" + Interlocked.Read(ref failures) + ". Exact ListerHaulables global searches use a worker-built spatial index; reachability/validator checks stay on the main thread.";
	}
}
