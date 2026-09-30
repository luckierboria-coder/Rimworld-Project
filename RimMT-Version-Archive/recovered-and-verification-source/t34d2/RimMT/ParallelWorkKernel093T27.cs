using System;
using System.Collections;
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

internal static class ParallelWorkKernel093T27
{
	private sealed class SourceState
	{
		internal int SourceId;

		internal int MapId;

		internal Thing[] Members;

		internal int NeedsRefresh;

		internal WeakReference SourceRef;

		internal long LastUsePackage;

		internal long UseCount;

		internal int LastUseWasCustom;

		internal int HotMapId;
	}

	private sealed class HotMapState
	{
		internal readonly List<SourceState> Sources = new List<SourceState>();
	}

	private sealed class SourceStateComparer : IComparer<SourceState>
	{
		internal static readonly SourceStateComparer Instance = new SourceStateComparer();

		public int Compare(SourceState a, SourceState b)
		{
			if (a == b)
			{
				return 0;
			}
			if (a == null)
			{
				return 1;
			}
			if (b == null)
			{
				return -1;
			}
			int num = b.LastUsePackage.CompareTo(a.LastUsePackage);
			if (num != 0)
			{
				return num;
			}
			return b.UseCount.CompareTo(a.UseCount);
		}
	}

	private sealed class PlanSlot
	{
		internal readonly IList Source;

		internal readonly SourceState State;

		internal readonly PersistentMapSearchFabric.SourceSnapshot Snapshot;

		internal readonly bool IsCustom;

		internal readonly long ScheduledTicks;

		internal PersistentMapSearchFabric.DistancePlan Plan;

		internal long ReadyTicks;

		internal long FirstUseTicks;

		internal int Ready;

		internal int Failed;

		internal int Consumed;

		internal int FirstUseRecorded;

		internal PlanSlot(IList source, SourceState state, PersistentMapSearchFabric.SourceSnapshot snapshot, bool isCustom, long scheduledTicks)
		{
			Source = source;
			State = state;
			Snapshot = snapshot;
			IsCustom = isCustom;
			ScheduledTicks = scheduledTicks;
		}
	}

	private sealed class PackageContext
	{
		internal readonly Pawn Pawn;

		internal readonly Map Map;

		internal readonly IntVec3 Root;

		internal readonly long Sequence;

		internal readonly long StartTicks;

		internal readonly Dictionary<object, PlanSlot> Plans = new Dictionary<object, PlanSlot>(ReferenceObjectComparer.Instance);

		internal PackageContext(Pawn pawn, Map map, IntVec3 root, long sequence, long startTicks)
		{
			//IL_0025: Unknown result type (might be due to invalid IL or missing references)
			//IL_0026: Unknown result type (might be due to invalid IL or missing references)
			Pawn = pawn;
			Map = map;
			Root = root;
			Sequence = sequence;
			StartTicks = startTicks;
		}
	}

	private sealed class ReferenceObjectComparer : IEqualityComparer<object>
	{
		internal static readonly ReferenceObjectComparer Instance = new ReferenceObjectComparer();

		public new bool Equals(object x, object y)
		{
			return x == y;
		}

		public int GetHashCode(object obj)
		{
			return RuntimeHelpers.GetHashCode(obj);
		}
	}

	internal const string FeatureId = "parallel.workKernel";

	private const int MinSourceCount = 64;

	private const int MaxSourceCount = 4096;

	private const int MaxPlansPerPackage = 8;

	private const int MaxHotSourcesPerMap = 24;

	private const long HotWindowPackages = 48L;

	private static readonly ConditionalWeakTable<object, SourceState> Sources = new ConditionalWeakTable<object, SourceState>();

	private static readonly ConditionalWeakTable<Map, HotMapState> HotMaps = new ConditionalWeakTable<Map, HotMapState>();

	[ThreadStatic]
	private static int packageDepth;

	[ThreadStatic]
	private static PackageContext current;

	private static volatile bool installed;

	private static volatile bool targetAuthoritySafe = true;

	private static int nextSourceId;

	private static long nextPackageSequence;

	private static long packages;

	private static long sourceUses;

	private static long staticSourceUses;

	private static long customSourceUses;

	private static long customNonListBypass;

	private static long sourceUseSizeBypass;

	private static long sourceUseUnsafeMemberBypass;

	private static long hotSourcesAdded;

	private static long hotSourcesExpired;

	private static long hotPreheatConsidered;

	private static long hotPreheatScheduled;

	private static long hotPreheatSkippedCold;

	private static long sourceRefreshes;

	private static long sourceRegisterRejected;

	private static long snapshotMisses;

	private static long plansScheduled;

	private static long plansCompleted;

	private static long plansReadyAtPackageEnd;

	private static long plansNotReadyAtPackageEnd;

	private static long plansReadyUnusedAtPackageEnd;

	private static long plansConsumed;

	private static long plansConsumedStatic;

	private static long plansConsumedCustom;

	private static long noPlanAtUse;

	private static long planNotReadyAtUse;

	private static long planValidationFailed;

	private static long schedulerRejected;

	private static long workerFailures;

	private static long planItems;

	private static long validatedItems;

	private static long buildTicks;

	private static long buildTicksMax;

	private static long consumeTicks;

	private static long consumeTicksMax;

	private static long firstUseReady;

	private static long firstUseNotReady;

	private static long firstUseWindowTicks;

	private static long firstUseWindowTicksMax;

	private static long readyLeadTicks;

	private static long readyLeadSamples;

	private static long missWindowTicks;

	private static long missWindowSamples;

	private static long useWindowLt50;

	private static long useWindow50To100;

	private static long useWindow100To250;

	private static long useWindow250To500;

	private static long useWindow500To1000;

	private static long useWindowGe1000;

	private static long foreignTargetBypass;

	private static long installFailures;

	internal static void Apply(Harmony harmony)
	{
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Expected O, but got Unknown
		//IL_0172: Expected O, but got Unknown
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", (Type[])null, (Type[])null);
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
			if (methodBase == null || methodBase2 == null)
			{
				installFailures++;
				FeatureGate.Suppress("parallel.workKernel", "T27.1 JobGiver_Work/ClosestThingReachable target missing");
				return;
			}
			targetAuthoritySafe = !HasForeignPatches(methodBase2);
			harmony.Patch(methodBase, new HarmonyMethod(typeof(ParallelWorkKernel093T27), "PackagePrefix", (Type[])null)
			{
				priority = 1225
			}, (HarmonyMethod)null, (HarmonyMethod)null, new HarmonyMethod(typeof(ParallelWorkKernel093T27), "PackageFinalizer", (Type[])null)
			{
				priority = -425
			});
			harmony.Patch(methodBase2, new HarmonyMethod(typeof(ParallelWorkKernel093T27), "ClosestThingReachablePrefix", (Type[])null)
			{
				priority = 1225
			}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = true;
			Log.Message("[RimMT] T27.1 Parallel Work Kernel installed. Source-centric hot-use scheduling; stable custom IList learning; package-local plans only; FullParallel OFF.");
		}
		catch (Exception ex)
		{
			installFailures++;
			installed = false;
			FeatureGate.Suppress("parallel.workKernel", "T27.1 install failure: " + ex.GetType().Name);
			Log.Warning("[RimMT] T27.1 Parallel Work Kernel install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PackagePrefix(Pawn pawn)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Invalid comparison between Unknown and I4
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		if (packageDepth++ != 0)
		{
			return;
		}
		current = null;
		if (!installed || !targetAuthoritySafe || !FeatureGate.IsEnabled("parallel.workKernel") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map == null)
		{
			return;
		}
		long sequence = Interlocked.Increment(ref nextPackageSequence);
		Interlocked.Increment(ref packages);
		PackageContext context = (current = new PackageContext(pawn, ((Thing)pawn).Map, ((Thing)pawn).Position, sequence, Stopwatch.GetTimestamp()));
		try
		{
			PreheatRecentlyUsedSources(context);
		}
		catch
		{
		}
	}

	public static Exception PackageFinalizer(Exception __exception)
	{
		if (packageDepth > 0)
		{
			packageDepth--;
		}
		if (packageDepth == 0)
		{
			PackageContext packageContext = current;
			if (packageContext != null)
			{
				foreach (KeyValuePair<object, PlanSlot> plan in packageContext.Plans)
				{
					PlanSlot value = plan.Value;
					if (value == null)
					{
						continue;
					}
					if (Volatile.Read(ref value.Ready) != 0)
					{
						Interlocked.Increment(ref plansReadyAtPackageEnd);
						if (Volatile.Read(ref value.Consumed) == 0)
						{
							Interlocked.Increment(ref plansReadyUnusedAtPackageEnd);
						}
					}
					else
					{
						Interlocked.Increment(ref plansNotReadyAtPackageEnd);
					}
				}
			}
			current = null;
		}
		return __exception;
	}

	private static void PreheatRecentlyUsedSources(PackageContext context)
	{
		if (!HotMaps.TryGetValue(context.Map, out var value) || value == null || value.Sources.Count == 0)
		{
			return;
		}
		for (int num = value.Sources.Count - 1; num >= 0; num--)
		{
			SourceState sourceState = value.Sources[num];
			if (sourceState == null || sourceState.HotMapId != context.Map.uniqueID || sourceState.SourceRef == null || !sourceState.SourceRef.IsAlive)
			{
				value.Sources.RemoveAt(num);
				Interlocked.Increment(ref hotSourcesExpired);
			}
			else
			{
				long num2 = context.Sequence - sourceState.LastUsePackage;
				if (num2 < 0 || num2 > 48)
				{
					value.Sources.RemoveAt(num);
					sourceState.HotMapId = int.MinValue;
					Interlocked.Increment(ref hotSourcesExpired);
				}
			}
		}
		if (value.Sources.Count == 0)
		{
			return;
		}
		value.Sources.Sort(SourceStateComparer.Instance);
		for (int i = 0; i < value.Sources.Count; i++)
		{
			if (context.Plans.Count >= 8)
			{
				break;
			}
			SourceState sourceState2 = value.Sources[i];
			Interlocked.Increment(ref hotPreheatConsidered);
			long num3 = context.Sequence - sourceState2.LastUsePackage;
			if (num3 < 0 || num3 > 48)
			{
				Interlocked.Increment(ref hotPreheatSkippedCold);
			}
			else if (((sourceState2.SourceRef == null) ? null : sourceState2.SourceRef.Target) is IList source && TrySchedulePlan(context, source, sourceState2, sourceState2.LastUseWasCustom != 0))
			{
				Interlocked.Increment(ref hotPreheatScheduled);
			}
		}
	}

	public static void ClosestThingReachablePrefix(IntVec3 root, Map map, ThingRequest thingReq, TraverseParms traverseParams, ref IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin, int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions)
	{
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Invalid comparison between Unknown and I4
		//IL_021e: Unknown result type (might be due to invalid IL or missing references)
		if (!installed || !targetAuthoritySafe || !FeatureGate.IsEnabled("parallel.workKernel") || !RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		PackageContext packageContext = current;
		if (packageContext == null || packageDepth <= 0 || map == null || map != packageContext.Map || traverseParams.pawn == null || traverseParams.pawn != packageContext.Pawn || root.x != packageContext.Root.x || root.z != packageContext.Root.z || searchRegionsMin != 0 || searchRegionsMax >= 0 || (int)traversableRegionTypes != 14 || ignoreEntirelyForbiddenRegions)
		{
			return;
		}
		if (!targetAuthoritySafe)
		{
			Interlocked.Increment(ref foreignTargetBypass);
			return;
		}
		bool flag = customGlobalSearchSet != null;
		IList list = null;
		if (flag)
		{
			list = customGlobalSearchSet as IList;
			if (list == null)
			{
				Interlocked.Increment(ref customNonListBypass);
				return;
			}
		}
		else
		{
			if (((ThingRequest)(ref thingReq)).IsUndefined)
			{
				return;
			}
			try
			{
				list = map.listerThings.ThingsMatching(thingReq);
			}
			catch
			{
				return;
			}
			if (list == null)
			{
				return;
			}
		}
		int count;
		try
		{
			count = list.Count;
		}
		catch
		{
			return;
		}
		if (count < 64 || count > 4096)
		{
			Interlocked.Increment(ref sourceUseSizeBypass);
			return;
		}
		SourceState value = Sources.GetValue(list, CreateSourceState);
		Interlocked.Increment(ref sourceUses);
		if (flag)
		{
			Interlocked.Increment(ref customSourceUses);
		}
		else
		{
			Interlocked.Increment(ref staticSourceUses);
		}
		value.LastUsePackage = packageContext.Sequence;
		value.UseCount++;
		value.LastUseWasCustom = (flag ? 1 : 0);
		AddHotSource(map, value);
		if (!EnsureRegistered(map, list, value))
		{
			Interlocked.Increment(ref noPlanAtUse);
			return;
		}
		if (!packageContext.Plans.TryGetValue(list, out var value2) || value2 == null)
		{
			Interlocked.Increment(ref noPlanAtUse);
			TrySchedulePlan(packageContext, list, value, flag);
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		RecordFirstUse(value2, timestamp);
		if (Volatile.Read(ref value2.Ready) == 0 || value2.Plan == null || Volatile.Read(ref value2.Failed) != 0)
		{
			Interlocked.Increment(ref planNotReadyAtUse);
			return;
		}
		long timestamp2 = Stopwatch.GetTimestamp();
		bool num = ValidatePlan(map, list, value2, packageContext.Root);
		long value3 = Stopwatch.GetTimestamp() - timestamp2;
		Interlocked.Add(ref consumeTicks, value3);
		UpdateMax(ref consumeTicksMax, value3);
		if (!num)
		{
			Volatile.Write(ref value2.State.NeedsRefresh, 1);
			Interlocked.Increment(ref planValidationFailed);
			return;
		}
		customGlobalSearchSet = value2.Plan.OrderedThings;
		Interlocked.Add(ref validatedItems, value2.Plan.Count);
		Interlocked.Increment(ref plansConsumed);
		if (flag)
		{
			Interlocked.Increment(ref plansConsumedCustom);
		}
		else
		{
			Interlocked.Increment(ref plansConsumedStatic);
		}
		Volatile.Write(ref value2.Consumed, 1);
	}

	private static void AddHotSource(Map map, SourceState state)
	{
		if (map == null || state == null)
		{
			return;
		}
		HotMapState value = HotMaps.GetValue(map, CreateHotMapState);
		if (state.HotMapId == map.uniqueID)
		{
			return;
		}
		state.HotMapId = map.uniqueID;
		value.Sources.Add(state);
		Interlocked.Increment(ref hotSourcesAdded);
		if (value.Sources.Count <= 24)
		{
			return;
		}
		value.Sources.Sort(SourceStateComparer.Instance);
		while (value.Sources.Count > 24)
		{
			int index = value.Sources.Count - 1;
			SourceState sourceState = value.Sources[index];
			value.Sources.RemoveAt(index);
			if (sourceState != null && sourceState.HotMapId == map.uniqueID)
			{
				sourceState.HotMapId = int.MinValue;
			}
			Interlocked.Increment(ref hotSourcesExpired);
		}
	}

	private static bool TrySchedulePlan(PackageContext context, IList source, SourceState state, bool isCustom)
	{
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		if (context == null || source == null || state == null || context.Plans.Count >= 8)
		{
			return false;
		}
		if (context.Plans.ContainsKey(source))
		{
			return true;
		}
		int count;
		try
		{
			count = source.Count;
		}
		catch
		{
			return false;
		}
		if (count < 64 || count > 4096)
		{
			return false;
		}
		if (!EnsureRegistered(context.Map, source, state))
		{
			return false;
		}
		if (!PersistentMapSearchFabric.TryGetSourceSnapshot(context.Map, state.SourceId, out var snapshot) || snapshot == null || !snapshot.Complete || snapshot.Count != count)
		{
			Interlocked.Increment(ref snapshotMisses);
			return false;
		}
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler == null || scheduler.ProductionPending > 24)
		{
			Interlocked.Increment(ref schedulerRejected);
			return false;
		}
		PlanSlot slot = new PlanSlot(source, state, snapshot, isCustom, Stopwatch.GetTimestamp());
		context.Plans.Add(source, slot);
		IntVec3 root = context.Root;
		if (scheduler.TryEnqueue("parallel.workKernel", JobPriority.High, delegate
		{
			long timestamp = Stopwatch.GetTimestamp();
			try
			{
				PersistentMapSearchFabric.DistancePlan distancePlan = snapshot.BuildDistancePlan(root.x, root.z);
				slot.Plan = distancePlan;
				if (distancePlan != null)
				{
					Interlocked.Add(ref planItems, distancePlan.Count);
				}
				slot.ReadyTicks = Stopwatch.GetTimestamp();
				Interlocked.Increment(ref plansCompleted);
				Volatile.Write(ref slot.Ready, 1);
			}
			catch
			{
				Volatile.Write(ref slot.Failed, 1);
				Interlocked.Increment(ref workerFailures);
			}
			finally
			{
				long value = Stopwatch.GetTimestamp() - timestamp;
				Interlocked.Add(ref buildTicks, value);
				UpdateMax(ref buildTicksMax, value);
			}
		}))
		{
			Interlocked.Increment(ref plansScheduled);
			return true;
		}
		context.Plans.Remove(source);
		Interlocked.Increment(ref schedulerRejected);
		return false;
	}

	private static bool EnsureRegistered(Map map, IList source, SourceState state)
	{
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		int count;
		try
		{
			count = source.Count;
		}
		catch
		{
			return false;
		}
		if (state.MapId == map.uniqueID && Volatile.Read(ref state.NeedsRefresh) == 0 && QuickMembershipMatches(source, state.Members))
		{
			return true;
		}
		Thing[] array = (Thing[])(object)new Thing[count];
		try
		{
			for (int i = 0; i < count; i++)
			{
				object obj2 = source[i];
				Thing val = (Thing)((obj2 is Thing) ? obj2 : null);
				if (val == null || val is Pawn || !val.Spawned || val.MapHeld != map)
				{
					Interlocked.Increment(ref sourceUseUnsafeMemberBypass);
					return false;
				}
				IntVec3 position = val.Position;
				if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, map))
				{
					Interlocked.Increment(ref sourceUseUnsafeMemberBypass);
					return false;
				}
				array[i] = val;
			}
		}
		catch
		{
			Interlocked.Increment(ref sourceUseUnsafeMemberBypass);
			return false;
		}
		if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, array))
		{
			Interlocked.Increment(ref sourceRegisterRejected);
			return false;
		}
		state.MapId = map.uniqueID;
		state.Members = array;
		Volatile.Write(ref state.NeedsRefresh, 0);
		Interlocked.Increment(ref sourceRefreshes);
		return false;
	}

	private static bool QuickMembershipMatches(IList source, Thing[] members)
	{
		if (source == null || members == null)
		{
			return false;
		}
		int count;
		try
		{
			count = source.Count;
		}
		catch
		{
			return false;
		}
		if (count != members.Length)
		{
			return false;
		}
		if (count == 0)
		{
			return true;
		}
		int num = count >> 1;
		try
		{
			return source[0] == members[0] && source[num] == members[num] && source[count - 1] == members[count - 1];
		}
		catch
		{
			return false;
		}
	}

	private static bool ValidatePlan(Map map, IList source, PlanSlot slot, IntVec3 root)
	{
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		SourceState state = slot.State;
		PersistentMapSearchFabric.DistancePlan plan = slot.Plan;
		Thing[] members = state.Members;
		if (state.MapId != map.uniqueID || members == null || plan == null || plan.MapId != map.uniqueID || plan.Width != map.Size.x || plan.Height != map.Size.z || plan.RootX != root.x || plan.RootZ != root.z || source.Count != members.Length || plan.Count != members.Length || plan.OrderedThings == null || plan.OrderedThings.Length != plan.Count)
		{
			return false;
		}
		for (int i = 0; i < members.Length; i++)
		{
			if (source[i] != members[i])
			{
				return false;
			}
		}
		PersistentMapSearchFabric.DistancePlanEntry[] entries = plan.Entries;
		for (int j = 0; j < entries.Length; j++)
		{
			PersistentMapSearchFabric.DistancePlanEntry distancePlanEntry = entries[j];
			Thing thing = distancePlanEntry.Thing;
			if (thing == null || distancePlanEntry.SourceIndex < 0 || distancePlanEntry.SourceIndex >= members.Length || members[distancePlanEntry.SourceIndex] != thing || !thing.Spawned || thing.MapHeld != map)
			{
				return false;
			}
			IntVec3 position = thing.Position;
			if (position.x != distancePlanEntry.X || position.z != distancePlanEntry.Z)
			{
				return false;
			}
		}
		return true;
	}

	private static void RecordFirstUse(PlanSlot slot, long useTicks)
	{
		if (slot != null && Interlocked.CompareExchange(ref slot.FirstUseRecorded, 1, 0) == 0)
		{
			slot.FirstUseTicks = useTicks;
			long num = Math.Max(0L, useTicks - slot.ScheduledTicks);
			Interlocked.Add(ref firstUseWindowTicks, num);
			UpdateMax(ref firstUseWindowTicksMax, num);
			RecordUseWindowBucket(num);
			if (Volatile.Read(ref slot.Ready) != 0 && slot.ReadyTicks > 0 && slot.ReadyTicks <= useTicks)
			{
				Interlocked.Increment(ref firstUseReady);
				long value = useTicks - slot.ReadyTicks;
				Interlocked.Add(ref readyLeadTicks, value);
				Interlocked.Increment(ref readyLeadSamples);
			}
			else
			{
				Interlocked.Increment(ref firstUseNotReady);
				Interlocked.Add(ref missWindowTicks, num);
				Interlocked.Increment(ref missWindowSamples);
			}
		}
	}

	private static void RecordUseWindowBucket(long ticks)
	{
		double num = (double)ticks * 1000000.0 / (double)Stopwatch.Frequency;
		if (num < 50.0)
		{
			Interlocked.Increment(ref useWindowLt50);
		}
		else if (num < 100.0)
		{
			Interlocked.Increment(ref useWindow50To100);
		}
		else if (num < 250.0)
		{
			Interlocked.Increment(ref useWindow100To250);
		}
		else if (num < 500.0)
		{
			Interlocked.Increment(ref useWindow250To500);
		}
		else if (num < 1000.0)
		{
			Interlocked.Increment(ref useWindow500To1000);
		}
		else
		{
			Interlocked.Increment(ref useWindowGe1000);
		}
	}

	private static SourceState CreateSourceState(object source)
	{
		int num = Interlocked.Increment(ref nextSourceId);
		if (num == 0)
		{
			num = Interlocked.Increment(ref nextSourceId);
		}
		return new SourceState
		{
			SourceId = num,
			MapId = int.MinValue,
			HotMapId = int.MinValue,
			SourceRef = new WeakReference(source)
		};
	}

	private static HotMapState CreateHotMapState(Map map)
	{
		return new HotMapState();
	}

	private static bool HasForeignPatches(MethodBase method)
	{
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(method);
			if (patchInfo == null)
			{
				return false;
			}
			return HasForeign(patchInfo.Prefixes) || HasForeign(patchInfo.Postfixes) || HasForeign(patchInfo.Transpilers) || HasForeign(patchInfo.Finalizers);
		}
		catch
		{
			return true;
		}
	}

	private static bool HasForeign(IEnumerable<Patch> patches)
	{
		if (patches == null)
		{
			return false;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null && !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref plansScheduled);
		long num2 = Interlocked.Read(ref plansCompleted);
		long num3 = Interlocked.Read(ref plansConsumed);
		long num4 = Interlocked.Read(ref firstUseReady);
		long num5 = Interlocked.Read(ref firstUseNotReady);
		long num6 = num4 + num5;
		long num7 = Interlocked.Read(ref readyLeadSamples);
		long num8 = Interlocked.Read(ref missWindowSamples);
		double num9 = ((num2 == 0L) ? 0.0 : ((double)Interlocked.Read(ref planItems) / (double)num2));
		double num10 = ((num2 == 0L) ? 0.0 : ((double)Interlocked.Read(ref buildTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num2));
		double num11 = ((num3 == 0L) ? 0.0 : ((double)Interlocked.Read(ref consumeTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num3));
		double num12 = ((num6 == 0L) ? 0.0 : ((double)Interlocked.Read(ref firstUseWindowTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num6));
		double num13 = ((num7 == 0L) ? 0.0 : ((double)Interlocked.Read(ref readyLeadTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num7));
		double num14 = ((num8 == 0L) ? 0.0 : ((double)Interlocked.Read(ref missWindowTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num8));
		return "T27.1 source-centric parallel work kernel: installed=" + installed + ", authoritySafe=" + targetAuthoritySafe + ", packages=" + Interlocked.Read(ref packages) + ", sourceUses[all/static/custom/nonList/size/unsafeMember]=" + Interlocked.Read(ref sourceUses) + "/" + Interlocked.Read(ref staticSourceUses) + "/" + Interlocked.Read(ref customSourceUses) + "/" + Interlocked.Read(ref customNonListBypass) + "/" + Interlocked.Read(ref sourceUseSizeBypass) + "/" + Interlocked.Read(ref sourceUseUnsafeMemberBypass) + ", hot[added/expired/considered/scheduled/coldSkip]=" + Interlocked.Read(ref hotSourcesAdded) + "/" + Interlocked.Read(ref hotSourcesExpired) + "/" + Interlocked.Read(ref hotPreheatConsidered) + "/" + Interlocked.Read(ref hotPreheatScheduled) + "/" + Interlocked.Read(ref hotPreheatSkippedCold) + ", sourceRefreshes=" + Interlocked.Read(ref sourceRefreshes) + ", sourceRegisterRejected=" + Interlocked.Read(ref sourceRegisterRejected) + ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) + ", plans[scheduled/completed/consumed(static/custom)/noPlanUse/notReadyUse/validationFail]=" + num + "/" + num2 + "/" + num3 + "(" + Interlocked.Read(ref plansConsumedStatic) + "/" + Interlocked.Read(ref plansConsumedCustom) + ")/" + Interlocked.Read(ref noPlanAtUse) + "/" + Interlocked.Read(ref planNotReadyAtUse) + "/" + Interlocked.Read(ref planValidationFailed) + ", firstUse[ready/notReady]=" + num4 + "/" + num5 + ", avgScheduleToFirstUseUs=" + num12.ToString("F2") + ", maxScheduleToFirstUseUs=" + ((double)Interlocked.Read(ref firstUseWindowTicksMax) * 1000000.0 / (double)Stopwatch.Frequency).ToString("F2") + ", avgReadyLeadUs=" + num13.ToString("F2") + ", avgMissWindowUs=" + num14.ToString("F2") + ", useWindowUs[<50/50-100/100-250/250-500/500-1000/>=1000]=" + Interlocked.Read(ref useWindowLt50) + "/" + Interlocked.Read(ref useWindow50To100) + "/" + Interlocked.Read(ref useWindow100To250) + "/" + Interlocked.Read(ref useWindow250To500) + "/" + Interlocked.Read(ref useWindow500To1000) + "/" + Interlocked.Read(ref useWindowGe1000) + ", endReady/notReady/readyUnused=" + Interlocked.Read(ref plansReadyAtPackageEnd) + "/" + Interlocked.Read(ref plansNotReadyAtPackageEnd) + "/" + Interlocked.Read(ref plansReadyUnusedAtPackageEnd) + ", schedulerRejected=" + Interlocked.Read(ref schedulerRejected) + ", workerFailures=" + Interlocked.Read(ref workerFailures) + ", avgItems=" + num9.ToString("F1") + ", avgWorkerBuildUs=" + num10.ToString("F2") + ", maxWorkerBuildUs=" + ((double)Interlocked.Read(ref buildTicksMax) * 1000000.0 / (double)Stopwatch.Frequency).ToString("F2") + ", avgMainValidateUs=" + num11.ToString("F2") + ", maxMainValidateUs=" + ((double)Interlocked.Read(ref consumeTicksMax) * 1000000.0 / (double)Stopwatch.Frequency).ToString("F2") + ", validatedItems=" + Interlocked.Read(ref validatedItems) + ", fullParallel=OFF, waits=0, crossPackagePlanCache=OFF. Only source-use history crosses packages; every distance plan is rebuilt for the current root and dies with that synchronous package.";
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
