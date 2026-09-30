using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class ScannerParallelFabric093T34B
{
	private sealed class PackageState
	{
		internal readonly long Token;

		internal readonly Map Map;

		internal readonly int MapId;

		internal readonly int RootX;

		internal readonly int RootZ;

		internal readonly ConcurrentDictionary<long, byte> Scheduled = new ConcurrentDictionary<long, byte>();

		private readonly ConcurrentDictionary<long, SharedPlan> plans = new ConcurrentDictionary<long, SharedPlan>();

		internal int Closed;

		internal PackageState(long token, Map map, IntVec3 root)
		{
			//IL_0051: Unknown result type (might be due to invalid IL or missing references)
			//IL_006d: Unknown result type (might be due to invalid IL or missing references)
			Token = token;
			Map = map;
			MapId = map?.uniqueID ?? int.MinValue;
			RootX = (((IntVec3)(ref root)).IsValid ? root.x : int.MinValue);
			RootZ = (((IntVec3)(ref root)).IsValid ? root.z : int.MinValue);
		}

		internal void Publish(SharedPlan plan)
		{
			if (plan != null)
			{
				long key = MakePlanKey(plan.SourceId, plan.RootX, plan.RootZ);
				plans[key] = plan;
			}
		}

		internal bool TryGetPlan(int sourceId, int rootX, int rootZ, PersistentMapSearchFabric.SourceSnapshot snapshot, out SharedPlan plan)
		{
			plan = null;
			long key = MakePlanKey(sourceId, rootX, rootZ);
			if (!plans.TryGetValue(key, out var value) || value == null)
			{
				return false;
			}
			if (value.Snapshot != snapshot)
			{
				return false;
			}
			plan = value;
			return true;
		}
	}

	private sealed class HotSource
	{
		internal readonly object Source;

		internal readonly object PlanSync = new object();

		internal int MapId = int.MinValue;

		internal int SourceId;

		internal int Count;

		internal int Score;

		internal long LastSeen;

		private readonly Dictionary<long, SharedPlan> rootPlans = new Dictionary<long, SharedPlan>();

		internal HotSource(object source)
		{
			Source = source;
		}

		internal void PublishPlan(SharedPlan plan)
		{
			if (plan == null)
			{
				return;
			}
			lock (PlanSync)
			{
				long key = MakeRootKey(plan.RootX, plan.RootZ);
				if (rootPlans.Count >= 8 && !rootPlans.ContainsKey(key))
				{
					long key2 = 0L;
					bool flag = false;
					using (Dictionary<long, SharedPlan>.KeyCollection.Enumerator enumerator = rootPlans.Keys.GetEnumerator())
					{
						if (enumerator.MoveNext())
						{
							key2 = enumerator.Current;
							flag = true;
						}
					}
					if (flag)
					{
						rootPlans.Remove(key2);
					}
				}
				rootPlans[key] = plan;
			}
		}

		internal bool TryGetPlan(int rootX, int rootZ, PersistentMapSearchFabric.SourceSnapshot snapshot, out SharedPlan plan)
		{
			plan = null;
			lock (PlanSync)
			{
				if (!rootPlans.TryGetValue(MakeRootKey(rootX, rootZ), out var value) || value == null || value.Snapshot != snapshot)
				{
					return false;
				}
				plan = value;
				return true;
			}
		}
	}

	private sealed class HotSourceComparer : IComparer<HotSource>
	{
		internal static readonly HotSourceComparer Instance = new HotSourceComparer();

		public int Compare(HotSource a, HotSource b)
		{
			int num = b.Score.CompareTo(a.Score);
			if (num != 0)
			{
				return num;
			}
			return b.LastSeen.CompareTo(a.LastSeen);
		}
	}

	private sealed class SharedPlan
	{
		internal readonly PersistentMapSearchFabric.SourceSnapshot Snapshot;

		internal readonly PersistentMapSearchFabric.DistancePlan DistancePlan;

		internal readonly int SourceId;

		internal readonly int RootX;

		internal readonly int RootZ;

		internal SharedPlan(PersistentMapSearchFabric.SourceSnapshot snapshot, PersistentMapSearchFabric.DistancePlan distancePlan, int sourceId, int rootX, int rootZ)
		{
			Snapshot = snapshot;
			DistancePlan = distancePlan;
			SourceId = sourceId;
			RootX = rootX;
			RootZ = rootZ;
		}
	}

	private struct WorkSpec
	{
		internal readonly HotSource Hot;

		internal readonly PersistentMapSearchFabric.SourceSnapshot Snapshot;

		internal readonly int SourceId;

		internal readonly int RootX;

		internal readonly int RootZ;

		internal WorkSpec(HotSource hot, PersistentMapSearchFabric.SourceSnapshot snapshot, int sourceId, int rootX, int rootZ)
		{
			Hot = hot;
			Snapshot = snapshot;
			SourceId = sourceId;
			RootX = rootX;
			RootZ = rootZ;
		}
	}

	internal const string FeatureId = "parallel.scannerFabric";

	private const int MinSourceCount = 16;

	private const int MaxSourceCount = 16384;

	private const int MaxHotSourcesPerMap = 96;

	private const int PrefetchSourcesPerPackage = 12;

	private const int MaxPackagePlans = 48;

	private const int MaxRootPlansPerHotSource = 8;

	private static readonly object HotSync = new object();

	private static readonly ConditionalWeakTable<object, HotSource> HotBySource = new ConditionalWeakTable<object, HotSource>();

	private static readonly Dictionary<int, List<HotSource>> HotByMap = new Dictionary<int, List<HotSource>>();

	[ThreadStatic]
	private static int packageDepth;

	[ThreadStatic]
	private static PackageState currentPackage;

	private static bool installed;

	private static long nextPackageToken;

	private static long nextHotSequence;

	private static long packages;

	private static long nestedPackages;

	private static long observedQueries;

	private static long shapeBypasses;

	private static long sourceUnavailable;

	private static long sourceRegisteredOrRefreshing;

	private static long eligibleQueries;

	private static long hotRegistrations;

	private static long hotEvictions;

	private static long prefetchPackages;

	private static long prefetchSources;

	private static long prefetchParallelAccepted;

	private static long prefetchParallelRejected;

	private static long onDemandScheduled;

	private static long onDemandRejected;

	private static long duplicateScheduleBypass;

	private static long workerPlansBuilt;

	private static long workerPlansDiscardedClosed;

	private static long workerFailures;

	private static long packagePlanHits;

	private static long crossPackagePlanHits;

	private static long planMisses;

	private static long stalePlanBypass;

	private static long planValidationBypass;

	private static long accelerated;

	private static long acceleratedNull;

	private static long candidatesInSources;

	private static long candidatesWithinDistance;

	private static long candidatesVisitedLive;

	private static long candidatesAvoidedLive;

	private static long reachChecks;

	private static long validatorChecks;

	private static long workerBuildTicks;

	private static long workerBuildTicksMax;

	private static long mainValidateTicks;

	private static long mainValidateTicksMax;

	private static long mainLiveTicks;

	private static long mainLiveTicksMax;

	private static long failures;

	private static long installFailures;

	private static long tickFlushCalls;

	internal static void Apply(Harmony harmony)
	{
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Expected O, but got Unknown
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Expected O, but got Unknown
		//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Expected O, but got Unknown
		//IL_0206: Unknown result type (might be due to invalid IL or missing references)
		//IL_020d: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new Type[2]
			{
				typeof(Pawn),
				typeof(JobIssueParams)
			}, (Type[])null);
			if (methodBase == null)
			{
				throw new MissingMethodException(typeof(JobGiver_Work).FullName, "TryIssueJobPackage");
			}
			HarmonyMethod val = new HarmonyMethod(typeof(ScannerParallelFabric093T34B), "PackagePrefix", (Type[])null);
			val.priority = 1150;
			HarmonyMethod val2 = new HarmonyMethod(typeof(ScannerParallelFabric093T34B), "PackageFinalizer", (Type[])null);
			val2.priority = -350;
			harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, val2);
			MethodBase methodBase2 = AccessTools.Method(typeof(GenClosest), "ClosestThingReachable", new Type[13]
			{
				typeof(IntVec3),
				typeof(Map),
				typeof(ThingRequest),
				typeof(PathEndMode),
				typeof(TraverseParms),
				typeof(float),
				typeof(Predicate<Thing>),
				typeof(IEnumerable<Thing>),
				typeof(int),
				typeof(int),
				typeof(bool),
				typeof(RegionType),
				typeof(bool)
			}, (Type[])null);
			if (methodBase2 == null)
			{
				throw new MissingMethodException(typeof(GenClosest).FullName, "ClosestThingReachable(13)");
			}
			CompatibilityGuard.RegisterTarget("parallel.scannerFabric", methodBase2);
			HarmonyMethod val3 = new HarmonyMethod(typeof(ScannerParallelFabric093T34B), "ClosestThingReachablePrefix", (Type[])null);
			val3.priority = 1180;
			harmony.Patch(methodBase2, val3, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			MethodBase methodBase3 = AccessTools.Method(typeof(TickManager), "DoSingleTick", (Type[])null, (Type[])null);
			if (methodBase3 != null)
			{
				HarmonyMethod val4 = new HarmonyMethod(typeof(ScannerParallelFabric093T34B), "TickPostfix", (Type[])null);
				val4.priority = -450;
				harmony.Patch(methodBase3, (HarmonyMethod)null, val4, (HarmonyMethod)null, (HarmonyMethod)null);
			}
			installed = true;
			Log.Message("[RimMT] T34-B scanner parallel fabric installed. Hot WorkGiver candidate sources are distance-planned on workers in parallel with the current JobGiver package; the main thread never waits and live Reachability/validator remain authoritative.");
		}
		catch (Exception ex)
		{
			installed = false;
			Interlocked.Increment(ref installFailures);
			FeatureGate.Suppress("parallel.scannerFabric", "T34-B install failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] T34-B scanner parallel fabric failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PackagePrefix(Pawn __0)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Invalid comparison between Unknown and I4
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		if (!installed || !FeatureGate.IsEnabled("parallel.scannerFabric") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return;
		}
		if (packageDepth == 0)
		{
			long token = Interlocked.Increment(ref nextPackageToken);
			Map val = ((__0 == null) ? null : ((Thing)__0).Map);
			IntVec3 root = ((__0 == null) ? IntVec3.Invalid : ((Thing)__0).Position);
			currentPackage = new PackageState(token, val, root);
			Interlocked.Increment(ref packages);
			if (val != null && !val.Disposed && ((IntVec3)(ref root)).IsValid)
			{
				PrefetchHotSources(currentPackage);
			}
		}
		else
		{
			Interlocked.Increment(ref nestedPackages);
		}
		packageDepth++;
	}

	public static Exception PackageFinalizer(Exception __exception)
	{
		if (packageDepth > 0)
		{
			packageDepth--;
		}
		if (packageDepth == 0)
		{
			PackageState packageState = currentPackage;
			currentPackage = null;
			if (packageState != null)
			{
				Volatile.Write(ref packageState.Closed, 1);
			}
		}
		return __exception;
	}

	public static void TickPostfix()
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Invalid comparison between Unknown and I4
		if (installed && FeatureGate.IsEnabled("parallel.scannerFabric") && RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			PersistentMapSearchFabric.FlushPendingT34B();
			Interlocked.Increment(ref tickFlushCalls);
		}
	}

	public static bool ClosestThingReachablePrefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin, int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions, ref Thing __result)
	{
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Invalid comparison between Unknown and I4
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Invalid comparison between Unknown and I4
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Invalid comparison between Unknown and I4
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Invalid comparison between Unknown and I4
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_018d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_023e: Unknown result type (might be due to invalid IL or missing references)
		//IL_024d: Unknown result type (might be due to invalid IL or missing references)
		//IL_039a: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0311: Unknown result type (might be due to invalid IL or missing references)
		//IL_0316: Unknown result type (might be due to invalid IL or missing references)
		//IL_0321: Unknown result type (might be due to invalid IL or missing references)
		//IL_0472: Unknown result type (might be due to invalid IL or missing references)
		//IL_0475: Unknown result type (might be due to invalid IL or missing references)
		//IL_047a: Unknown result type (might be due to invalid IL or missing references)
		//IL_047b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0331: Unknown result type (might be due to invalid IL or missing references)
		Interlocked.Increment(ref observedQueries);
		if (!installed || !FeatureGate.IsEnabled("parallel.scannerFabric") || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		Pawn pawn = traverseParams.pawn;
		if (map == null || map.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != map || !((IntVec3)(ref root)).IsValid || !GenGrid.InBounds(root, map) || maxDistance <= 0f)
		{
			return true;
		}
		TraverseMode mode = traverseParams.mode;
		if ((int)mode != 0 && (int)mode != 1 && (int)mode != 2)
		{
			Interlocked.Increment(ref shapeBypasses);
			return true;
		}
		if ((int)traversableRegionTypes != 14 || ignoreEntirelyForbiddenRegions || (searchRegionsMax >= 0 && !forceAllowGlobalSearch))
		{
			Interlocked.Increment(ref shapeBypasses);
			return true;
		}
		object source;
		if (customGlobalSearchSet != null)
		{
			if (!((ThingRequest)(ref thingReq)).IsUndefined)
			{
				Interlocked.Increment(ref shapeBypasses);
				return true;
			}
			source = customGlobalSearchSet;
		}
		else
		{
			try
			{
				source = map.listerThings.ThingsMatching(thingReq);
			}
			catch
			{
				Interlocked.Increment(ref sourceUnavailable);
				return true;
			}
		}
		if (!CandidateFabric093T34A.TryEnsureSourceSnapshotT34B(map, source, 16, 16384, out var snapshot, out var sourceId, out var count) || snapshot == null)
		{
			Interlocked.Increment(ref sourceRegisteredOrRefreshing);
			return true;
		}
		if (count < 16 || count > 16384)
		{
			Interlocked.Increment(ref sourceUnavailable);
			return true;
		}
		Interlocked.Increment(ref eligibleQueries);
		Interlocked.Add(ref candidatesInSources, count);
		HotSource hotSource = RegisterHotSource(source, map, sourceId, count);
		if (hotSource == null)
		{
			return true;
		}
		PackageState packageState = currentPackage;
		if (packageState != null && packageState.MapId == map.uniqueID && packageState.TryGetPlan(sourceId, root.x, root.z, snapshot, out var plan))
		{
			Interlocked.Increment(ref packagePlanHits);
		}
		else
		{
			if (!hotSource.TryGetPlan(root.x, root.z, snapshot, out plan))
			{
				Interlocked.Increment(ref planMisses);
				if (packageState != null && packageState.MapId == map.uniqueID)
				{
					TryScheduleOne(packageState, hotSource, snapshot, root.x, root.z);
				}
				return true;
			}
			Interlocked.Increment(ref crossPackagePlanHits);
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (!CandidateFabric093T34A.TryValidateSourceSnapshotT34B(map, source, snapshot, out var count2) || count2 != count || plan.DistancePlan == null || plan.Snapshot != snapshot || plan.RootX != root.x || plan.RootZ != root.z)
		{
			RecordElapsed(ref mainValidateTicks, ref mainValidateTicksMax, timestamp);
			Interlocked.Increment(ref stalePlanBypass);
			return true;
		}
		PersistentMapSearchFabric.DistancePlanEntry[] entries = plan.DistancePlan.Entries;
		if (entries == null)
		{
			RecordElapsed(ref mainValidateTicks, ref mainValidateTicksMax, timestamp);
			Interlocked.Increment(ref planValidationBypass);
			return true;
		}
		double num = (double)maxDistance * (double)maxDistance;
		int num2 = 0;
		for (int i = 0; i < entries.Length; i++)
		{
			PersistentMapSearchFabric.DistancePlanEntry distancePlanEntry = entries[i];
			if ((double)distancePlanEntry.DistanceSquared > num)
			{
				break;
			}
			Thing thing = distancePlanEntry.Thing;
			if (thing == null || !thing.Spawned || thing.MapHeld != map)
			{
				RecordElapsed(ref mainValidateTicks, ref mainValidateTicksMax, timestamp);
				Interlocked.Increment(ref planValidationBypass);
				return true;
			}
			IntVec3 position = thing.Position;
			if (!((IntVec3)(ref position)).IsValid || position.x != distancePlanEntry.X || position.z != distancePlanEntry.Z)
			{
				RecordElapsed(ref mainValidateTicks, ref mainValidateTicksMax, timestamp);
				Interlocked.Increment(ref planValidationBypass);
				return true;
			}
			num2++;
		}
		RecordElapsed(ref mainValidateTicks, ref mainValidateTicksMax, timestamp);
		Interlocked.Add(ref candidatesWithinDistance, num2);
		CandidateClassificationFabric093T34C.ClassificationPlan plan2;
		bool flag = CandidateClassificationFabric093T34C.TryGetOrSchedule(snapshot, entries, validator, root.x, root.z, out plan2);
		long timestamp2 = Stopwatch.GetTimestamp();
		Thing val = null;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		int num6 = 0;
		try
		{
			for (int j = 0; j < num2; j++)
			{
				Thing thing2 = entries[j].Thing;
				byte reason = 0;
				int sourceIndex = entries[j].SourceIndex;
				if (flag && !CandidateClassificationFabric093T34C.TryGetRejectReason(plan2, sourceIndex, out reason))
				{
					CandidateClassificationFabric093T34C.Quarantine(snapshot, plan2);
					RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, timestamp2);
					return true;
				}
				if (flag && reason != 0)
				{
					if (!CandidateClassificationFabric093T34C.ValidateReject(plan2, sourceIndex, thing2))
					{
						CandidateClassificationFabric093T34C.Quarantine(snapshot, plan2);
						RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, timestamp2);
						return true;
					}
					num6++;
					continue;
				}
				num3++;
				num4++;
				if (!map.reachability.CanReach(root, LocalTargetInfo.op_Implicit(thing2), peMode, traverseParams))
				{
					continue;
				}
				if (validator != null)
				{
					num5++;
					bool flag2 = validator(thing2);
					if (flag)
					{
						CandidateClassificationFabric093T34C.NoteValidatorResult(plan2, sourceIndex, flag2);
					}
					if (!flag2)
					{
						continue;
					}
				}
				val = thing2;
				break;
			}
		}
		catch (Exception exception)
		{
			RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, timestamp2);
			Interlocked.Increment(ref failures);
			CircuitBreaker.RecordFailure("parallel.scannerFabric", exception);
			return true;
		}
		RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, timestamp2);
		if (flag)
		{
			CandidateClassificationFabric093T34C.NoteConsumed(plan2, num6, num2);
		}
		__result = val;
		Interlocked.Increment(ref accelerated);
		if (val == null)
		{
			Interlocked.Increment(ref acceleratedNull);
		}
		Interlocked.Add(ref candidatesVisitedLive, num3);
		Interlocked.Add(ref reachChecks, num4);
		Interlocked.Add(ref validatorChecks, num5);
		long num7 = count - num3;
		if (num7 > 0)
		{
			Interlocked.Add(ref candidatesAvoidedLive, num7);
		}
		return false;
	}

	private static HotSource RegisterHotSource(object source, Map map, int sourceId, int count)
	{
		if (source == null || map == null)
		{
			return null;
		}
		HotSource value = HotBySource.GetValue(source, (object key) => new HotSource(key));
		bool flag = false;
		lock (HotSync)
		{
			value.MapId = map.uniqueID;
			value.SourceId = sourceId;
			value.Count = count;
			value.Score = Math.Min(1000000, value.Score + 1);
			value.LastSeen = Interlocked.Increment(ref nextHotSequence);
			if (!HotByMap.TryGetValue(map.uniqueID, out var value2))
			{
				value2 = new List<HotSource>();
				HotByMap.Add(map.uniqueID, value2);
			}
			if (!value2.Contains(value))
			{
				if (value2.Count >= 96)
				{
					int index = 0;
					for (int num = 1; num < value2.Count; num++)
					{
						if (value2[num].Score < value2[index].Score || (value2[num].Score == value2[index].Score && value2[num].LastSeen < value2[index].LastSeen))
						{
							index = num;
						}
					}
					value2.RemoveAt(index);
					Interlocked.Increment(ref hotEvictions);
				}
				value2.Add(value);
				flag = true;
			}
		}
		if (flag)
		{
			Interlocked.Increment(ref hotRegistrations);
		}
		return value;
	}

	private static void PrefetchHotSources(PackageState package)
	{
		if (package == null || package.Map == null || package.Map.Disposed)
		{
			return;
		}
		List<HotSource> list = new List<HotSource>(12);
		lock (HotSync)
		{
			if (!HotByMap.TryGetValue(package.MapId, out var value) || value.Count == 0)
			{
				return;
			}
			List<HotSource> list2 = new List<HotSource>(value);
			list2.Sort(HotSourceComparer.Instance);
			int num = Math.Min(12, list2.Count);
			for (int i = 0; i < num; i++)
			{
				list.Add(list2[i]);
			}
		}
		if (list.Count == 0)
		{
			return;
		}
		List<WorkSpec> list3 = new List<WorkSpec>(list.Count);
		for (int j = 0; j < list.Count; j++)
		{
			HotSource hotSource = list[j];
			if (CandidateFabric093T34A.TryGetKnownSourceSnapshotFastT34B(package.Map, hotSource.Source, out var snapshot, out var sourceId, out var count) && snapshot != null && count >= 16 && count <= 16384 && !hotSource.TryGetPlan(package.RootX, package.RootZ, snapshot, out var _))
			{
				long key = MakePlanKey(sourceId, package.RootX, package.RootZ);
				if (package.Scheduled.TryAdd(key, 1))
				{
					list3.Add(new WorkSpec(hotSource, snapshot, sourceId, package.RootX, package.RootZ));
				}
			}
		}
		if (list3.Count == 0)
		{
			return;
		}
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler != null)
		{
			Interlocked.Increment(ref prefetchPackages);
			Interlocked.Add(ref prefetchSources, list3.Count);
			WorkSpec[] array = list3.ToArray();
			if (scheduler.ParallelFor("parallel.scannerFabric", 0, array.Length, 1, delegate(int from, int to)
			{
				for (int k = from; k < to; k++)
				{
					BuildPlan(package, array[k], prefetched: true);
				}
			}, null, JobPriority.High))
			{
				Interlocked.Increment(ref prefetchParallelAccepted);
			}
			else
			{
				Interlocked.Increment(ref prefetchParallelRejected);
			}
		}
	}

	private static void TryScheduleOne(PackageState package, HotSource hot, PersistentMapSearchFabric.SourceSnapshot snapshot, int rootX, int rootZ)
	{
		if (package == null || hot == null || snapshot == null || Volatile.Read(ref package.Closed) != 0 || package.Scheduled.Count >= 48)
		{
			return;
		}
		long key = MakePlanKey(hot.SourceId, rootX, rootZ);
		if (!package.Scheduled.TryAdd(key, 1))
		{
			Interlocked.Increment(ref duplicateScheduleBypass);
			return;
		}
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler != null)
		{
			WorkSpec spec = new WorkSpec(hot, snapshot, hot.SourceId, rootX, rootZ);
			if (scheduler.TryEnqueue("parallel.scannerFabric", JobPriority.High, delegate
			{
				BuildPlan(package, spec, prefetched: false);
			}))
			{
				Interlocked.Increment(ref onDemandScheduled);
			}
			else
			{
				Interlocked.Increment(ref onDemandRejected);
			}
		}
	}

	private static void BuildPlan(PackageState package, WorkSpec spec, bool prefetched)
	{
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			if (package == null || spec.Snapshot == null || Volatile.Read(ref package.Closed) != 0)
			{
				Interlocked.Increment(ref workerPlansDiscardedClosed);
				return;
			}
			PersistentMapSearchFabric.DistancePlan distancePlan = spec.Snapshot.BuildDistancePlan(spec.RootX, spec.RootZ);
			if (distancePlan != null)
			{
				SharedPlan plan = new SharedPlan(spec.Snapshot, distancePlan, spec.SourceId, spec.RootX, spec.RootZ);
				spec.Hot.PublishPlan(plan);
				if (Volatile.Read(ref package.Closed) == 0)
				{
					package.Publish(plan);
				}
				else
				{
					Interlocked.Increment(ref workerPlansDiscardedClosed);
				}
				Interlocked.Increment(ref workerPlansBuilt);
			}
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref workerFailures);
			CircuitBreaker.RecordFailure("parallel.scannerFabric", exception);
		}
		finally
		{
			RecordElapsed(ref workerBuildTicks, ref workerBuildTicksMax, timestamp);
		}
	}

	private static long MakePlanKey(int sourceId, int x, int z)
	{
		return (long)(((ulong)(uint)x << 32) | (uint)z) ^ (sourceId * 1099511628211L);
	}

	private static long MakeRootKey(int x, int z)
	{
		return (long)(((ulong)(uint)x << 32) | (uint)z);
	}

	private static void RecordElapsed(ref long total, ref long max, long started)
	{
		long num = Stopwatch.GetTimestamp() - started;
		if (num >= 0)
		{
			Interlocked.Add(ref total, num);
			long num2;
			while (num > (num2 = Interlocked.Read(ref max)) && Interlocked.CompareExchange(ref max, num, num2) != num2)
			{
			}
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref workerPlansBuilt);
		long num2 = Interlocked.Read(ref accelerated);
		long num3 = Interlocked.Read(ref candidatesInSources);
		long num4 = Interlocked.Read(ref candidatesVisitedLive);
		long num5 = Interlocked.Read(ref candidatesAvoidedLive);
		double num6 = ((num <= 0) ? 0.0 : ((double)Interlocked.Read(ref workerBuildTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num7 = (double)Interlocked.Read(ref workerBuildTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num8 = ((num2 <= 0) ? 0.0 : ((double)Interlocked.Read(ref mainValidateTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num2));
		double num9 = ((num2 <= 0) ? 0.0 : ((double)Interlocked.Read(ref mainLiveTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num2));
		double num10 = ((Interlocked.Read(ref eligibleQueries) <= 0) ? 0.0 : ((double)num3 / (double)Interlocked.Read(ref eligibleQueries)));
		double num11 = ((num2 <= 0) ? 0.0 : ((double)num4 / (double)num2));
		double num12 = ((num2 <= 0) ? 0.0 : ((double)num5 / (double)num2));
		return "T34-B scanner parallel fabric: installed=" + installed + ", packages=" + Interlocked.Read(ref packages) + ", nested=" + Interlocked.Read(ref nestedPackages) + ", observedQueries=" + Interlocked.Read(ref observedQueries) + ", eligible=" + Interlocked.Read(ref eligibleQueries) + ", shapeBypass=" + Interlocked.Read(ref shapeBypasses) + ", sourceUnavailable=" + Interlocked.Read(ref sourceUnavailable) + ", sourceRefreshing=" + Interlocked.Read(ref sourceRegisteredOrRefreshing) + ", hot[register/evict]=" + Interlocked.Read(ref hotRegistrations) + "/" + Interlocked.Read(ref hotEvictions) + ", prefetch[packages/sources/accepted/rejected]=" + Interlocked.Read(ref prefetchPackages) + "/" + Interlocked.Read(ref prefetchSources) + "/" + Interlocked.Read(ref prefetchParallelAccepted) + "/" + Interlocked.Read(ref prefetchParallelRejected) + ", onDemand[scheduled/rejected/duplicate]=" + Interlocked.Read(ref onDemandScheduled) + "/" + Interlocked.Read(ref onDemandRejected) + "/" + Interlocked.Read(ref duplicateScheduleBypass) + ", worker[plans/closedDiscard/failures]=" + num + "/" + Interlocked.Read(ref workerPlansDiscardedClosed) + "/" + Interlocked.Read(ref workerFailures) + ", planHits[package/crossPackage/miss/stale/validationBypass]=" + Interlocked.Read(ref packagePlanHits) + "/" + Interlocked.Read(ref crossPackagePlanHits) + "/" + Interlocked.Read(ref planMisses) + "/" + Interlocked.Read(ref stalePlanBypass) + "/" + Interlocked.Read(ref planValidationBypass) + ", accelerated=" + num2 + ", acceleratedNull=" + Interlocked.Read(ref acceleratedNull) + ", avgSource=" + num10.ToString("F1") + ", avgWithinDistance=" + ((num2 <= 0) ? 0.0 : ((double)Interlocked.Read(ref candidatesWithinDistance) / (double)num2)).ToString("F1") + ", avgVisitedLive=" + num11.ToString("F1") + ", avgAvoidedLive=" + num12.ToString("F1") + ", reachChecks=" + Interlocked.Read(ref reachChecks) + ", validatorChecks=" + Interlocked.Read(ref validatorChecks) + ", avgWorkerBuildUs=" + num6.ToString("F2") + ", maxWorkerBuildUs=" + num7.ToString("F2") + ", avgMainValidateUs=" + num8.ToString("F2") + ", avgMainLiveUs=" + num9.ToString("F2") + ", tickFlushCalls=" + Interlocked.Read(ref tickFlushCalls) + ", failures=" + Interlocked.Read(ref failures) + ", installFailures=" + Interlocked.Read(ref installFailures) + ". Same-package overlap + bounded cross-package root reuse; no main-thread worker wait.";
	}
}
