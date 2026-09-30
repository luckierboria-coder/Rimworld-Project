using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class CandidateFabric093T34A
{
	private sealed class SourceState
	{
		internal int SourceId;

		internal int MapId;

		internal Thing[] Members;
	}

	private enum SourceKind
	{
		None,
		Thing,
		Building,
		Pawn
	}

	private enum CaptureFailure
	{
		None,
		Invalid,
		Mobile,
		Unspawned
	}

	internal const string FeatureId = "parallel.candidateFabric";

	private const int MinCandidateCount = 48;

	private const int MaxCandidateCount = 16384;

	private static readonly ConditionalWeakTable<object, SourceState> States = new ConditionalWeakTable<object, SourceState>();

	private static volatile bool compatibilityReady;

	private static bool installed;

	private static int nextSourceId;

	[ThreadStatic]
	private static int assistDepth;

	private static long observedCalls;

	private static long inScopeCalls;

	private static long eligibleCalls;

	private static long customSourceCalls;

	private static long thingRequestSourceCalls;

	private static long acceleratedCalls;

	private static long acceleratedNoResult;

	private static long fallbackCalls;

	private static long shapeBypasses;

	private static long sourceShapeBypasses;

	private static long mobileSourceBypasses;

	private static long smallSourceBypasses;

	private static long largeSourceBypasses;

	private static long membershipHits;

	private static long membershipRefreshes;

	private static long membershipRefreshRejected;

	private static long fabricMisses;

	private static long staleFallbacks;

	private static long candidatesSourceTotal;

	private static long candidatesVisited;

	private static long candidatesAvoided;

	private static long bucketVisits;

	private static long reachabilityChecks;

	private static long validatorChecks;

	private static long failures;

	private static long patchFailures;

	internal static void Apply(Harmony harmony)
	{
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_011f: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		PersistentMapSearchFabric.Apply(harmony);
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(GenClosest), "ClosestThingReachable", new Type[13]
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
			if (methodBase == null)
			{
				FeatureGate.Suppress("parallel.candidateFabric", "GenClosest.ClosestThingReachable 13-arg target not found");
				patchFailures++;
				return;
			}
			CompatibilityGuard.RegisterTarget("parallel.candidateFabric", methodBase);
			HarmonyMethod val = new HarmonyMethod(typeof(CandidateFabric093T34A), "Prefix", (Type[])null);
			val.priority = 1120;
			harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = true;
			Log.Message("[RimMT] T34-A Async Candidate Fabric installed. ThingRequest-backed and custom static candidate sources may use worker-maintained spatial buckets; main thread never waits and live Reachability/validator remain authoritative.");
		}
		catch (Exception ex)
		{
			patchFailures++;
			installed = false;
			FeatureGate.Suppress("parallel.candidateFabric", "T34-A candidate fabric install failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] T34-A candidate fabric failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void MarkCompatibilityReady()
	{
		compatibilityReady = true;
	}

	public static bool Prefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin, int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions, ref Thing __result)
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Invalid comparison between Unknown and I4
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Invalid comparison between Unknown and I4
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Invalid comparison between Unknown and I4
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Invalid comparison between Unknown and I4
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d3: Unknown result type (might be due to invalid IL or missing references)
		Interlocked.Increment(ref observedCalls);
		if (!installed || !compatibilityReady || !FeatureGate.IsEnabled("parallel.candidateFabric") || !JobGiverGlobalNearest04181.InJobGiverScope || assistDepth != 0 || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		Interlocked.Increment(ref inScopeCalls);
		Pawn pawn = traverseParams.pawn;
		if (map == null || map.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != map || !((IntVec3)(ref root)).IsValid || !GenGrid.InBounds(root, map) || maxDistance <= 0f)
		{
			Interlocked.Increment(ref fallbackCalls);
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
		SourceKind kind;
		int count;
		object obj;
		if (customGlobalSearchSet != null)
		{
			if (!((ThingRequest)(ref thingReq)).IsUndefined || !TryGetSourceShape(customGlobalSearchSet, out kind, out count))
			{
				Interlocked.Increment(ref sourceShapeBypasses);
				return true;
			}
			obj = customGlobalSearchSet;
			Interlocked.Increment(ref customSourceCalls);
		}
		else
		{
			List<Thing> list;
			try
			{
				list = map.listerThings.ThingsMatching(thingReq);
			}
			catch
			{
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			if (list == null)
			{
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			obj = list;
			kind = SourceKind.Thing;
			count = list.Count;
			Interlocked.Increment(ref thingRequestSourceCalls);
		}
		if (kind == SourceKind.Pawn)
		{
			Interlocked.Increment(ref mobileSourceBypasses);
			return true;
		}
		if (count < 48)
		{
			Interlocked.Increment(ref smallSourceBypasses);
			return true;
		}
		if (count > 16384)
		{
			Interlocked.Increment(ref largeSourceBypasses);
			return true;
		}
		Interlocked.Increment(ref eligibleCalls);
		Interlocked.Add(ref candidatesSourceTotal, count);
		SourceState value = States.GetValue(obj, CreateState);
		if (value.MapId != map.uniqueID)
		{
			value.MapId = map.uniqueID;
			value.Members = null;
		}
		if (!MembershipMatches(obj, kind, count, value.Members))
		{
			if (!TryCaptureMembers(obj, kind, count, map, out var members, out var failure))
			{
				if (failure == CaptureFailure.Mobile)
				{
					Interlocked.Increment(ref mobileSourceBypasses);
				}
				else
				{
					Interlocked.Increment(ref fallbackCalls);
				}
				return true;
			}
			value.Members = members;
			Interlocked.Increment(ref membershipRefreshes);
			if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, value.SourceId, members))
			{
				Interlocked.Increment(ref membershipRefreshRejected);
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		Interlocked.Increment(ref membershipHits);
		if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, value.SourceId, out var source) || source == null || source.Count != count)
		{
			Interlocked.Increment(ref fabricMisses);
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		assistDepth = 1;
		try
		{
			if (!source.TryFindClosest(root, map, peMode, traverseParams, maxDistance, validator, count + 1, out var chosen, out var visited, out var bucketsSeen, out var reaches, out var validations, out var staleDetected))
			{
				if (staleDetected)
				{
					Interlocked.Increment(ref staleFallbacks);
				}
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
			__result = chosen;
			Interlocked.Increment(ref acceleratedCalls);
			if (chosen == null)
			{
				Interlocked.Increment(ref acceleratedNoResult);
			}
			Interlocked.Add(ref candidatesVisited, visited);
			Interlocked.Add(ref bucketVisits, bucketsSeen);
			Interlocked.Add(ref reachabilityChecks, reaches);
			Interlocked.Add(ref validatorChecks, validations);
			long num = count - visited;
			if (num > 0)
			{
				Interlocked.Add(ref candidatesAvoided, num);
			}
			return false;
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			CircuitBreaker.RecordFailure("parallel.candidateFabric", ex);
			Log.Warning("[RimMT] T34-A candidate fabric runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
			return true;
		}
		finally
		{
			assistDepth = 0;
		}
	}

	private static SourceState CreateState(object ignored)
	{
		int num = Interlocked.Increment(ref nextSourceId);
		if (num == 0)
		{
			num = Interlocked.Increment(ref nextSourceId);
		}
		return new SourceState
		{
			SourceId = num,
			MapId = int.MinValue
		};
	}

	private static bool MembershipMatches(object source, SourceKind kind, int count, Thing[] members)
	{
		if (members == null || members.Length != count)
		{
			return false;
		}
		try
		{
			for (int i = 0; i < count; i++)
			{
				if (GetThingAt(source, kind, i) != members[i])
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryCaptureMembers(object source, SourceKind kind, int count, Map map, out Thing[] members, out CaptureFailure failure)
	{
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		members = (Thing[])(object)new Thing[count];
		failure = CaptureFailure.None;
		try
		{
			for (int i = 0; i < count; i++)
			{
				Thing thingAt = GetThingAt(source, kind, i);
				if (thingAt == null)
				{
					failure = CaptureFailure.Invalid;
					return false;
				}
				if (thingAt is Pawn)
				{
					failure = CaptureFailure.Mobile;
					return false;
				}
				if (!thingAt.Spawned || thingAt.MapHeld != map)
				{
					failure = CaptureFailure.Unspawned;
					return false;
				}
				IntVec3 position = thingAt.Position;
				if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, map))
				{
					failure = CaptureFailure.Invalid;
					return false;
				}
				members[i] = thingAt;
			}
			return true;
		}
		catch
		{
			failure = CaptureFailure.Invalid;
			return false;
		}
	}

	private static bool TryGetSourceShape(object source, out SourceKind kind, out int count)
	{
		if (source is IList<Thing> list)
		{
			kind = SourceKind.Thing;
			count = list.Count;
			return true;
		}
		if (source is IList<Building> list2)
		{
			kind = SourceKind.Building;
			count = list2.Count;
			return true;
		}
		if (source is IList<Pawn> list3)
		{
			kind = SourceKind.Pawn;
			count = list3.Count;
			return true;
		}
		kind = SourceKind.None;
		count = 0;
		return false;
	}

	private static Thing GetThingAt(object source, SourceKind kind, int index)
	{
		return (Thing)(kind switch
		{
			SourceKind.Thing => ((IList<Thing>)source)[index], 
			SourceKind.Building => ((IList<Building>)source)[index], 
			SourceKind.Pawn => ((IList<Pawn>)source)[index], 
			_ => null, 
		});
	}

	internal static bool TryEnsureSourceSnapshotT34B(Map map, object source, int minCount, int maxCount, out PersistentMapSearchFabric.SourceSnapshot snapshot, out int sourceId, out int count)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Invalid comparison between Unknown and I4
		snapshot = null;
		sourceId = 0;
		count = 0;
		if (map == null || map.Disposed || source == null || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return false;
		}
		if (!TryGetSourceShape(source, out var kind, out count) || kind == SourceKind.Pawn || count < minCount || count > maxCount)
		{
			return false;
		}
		SourceState value = States.GetValue(source, CreateState);
		if (value.MapId != map.uniqueID)
		{
			value.MapId = map.uniqueID;
			value.Members = null;
		}
		if (!MembershipMatches(source, kind, count, value.Members))
		{
			if (!TryCaptureMembers(source, kind, count, map, out var members, out var _))
			{
				return false;
			}
			value.Members = members;
			PersistentMapSearchFabric.RegisterOrUpdateSource(map, value.SourceId, members);
			return false;
		}
		if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, value.SourceId, out var source2) || source2 == null || source2.Count != count)
		{
			return false;
		}
		sourceId = value.SourceId;
		snapshot = source2;
		return true;
	}

	internal static bool TryGetKnownSourceSnapshotFastT34B(Map map, object source, out PersistentMapSearchFabric.SourceSnapshot snapshot, out int sourceId, out int count)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Invalid comparison between Unknown and I4
		snapshot = null;
		sourceId = 0;
		count = 0;
		if (map == null || map.Disposed || source == null || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return false;
		}
		if (!States.TryGetValue(source, out var value) || value == null || value.MapId != map.uniqueID || value.Members == null)
		{
			return false;
		}
		count = value.Members.Length;
		if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, value.SourceId, out var source2) || source2 == null || source2.Count != count)
		{
			return false;
		}
		sourceId = value.SourceId;
		snapshot = source2;
		return true;
	}

	internal static bool TryValidateSourceSnapshotT34B(Map map, object source, PersistentMapSearchFabric.SourceSnapshot expected, out int count)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Invalid comparison between Unknown and I4
		count = 0;
		if (map == null || map.Disposed || source == null || expected == null || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return false;
		}
		if (!TryGetSourceShape(source, out var kind, out count) || kind == SourceKind.Pawn)
		{
			return false;
		}
		if (!States.TryGetValue(source, out var value) || value == null || value.MapId != map.uniqueID || !MembershipMatches(source, kind, count, value.Members))
		{
			return false;
		}
		if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, value.SourceId, out var source2) || source2 == null || source2.Count != count)
		{
			return false;
		}
		return source2 == expected;
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref acceleratedCalls);
		long num2 = Interlocked.Read(ref eligibleCalls);
		long num3 = Interlocked.Read(ref candidatesVisited);
		long num4 = Interlocked.Read(ref candidatesAvoided);
		double num5 = ((num2 <= 0) ? 0.0 : ((double)Interlocked.Read(ref candidatesSourceTotal) / (double)num2));
		double num6 = ((num <= 0) ? 0.0 : ((double)num3 / (double)num));
		double num7 = ((num <= 0) ? 0.0 : ((double)num4 / (double)num));
		return "T34-A async candidate fabric: installed=" + installed + ", compatibilityReady=" + compatibilityReady + ", observed=" + Interlocked.Read(ref observedCalls) + ", inScope=" + Interlocked.Read(ref inScopeCalls) + ", eligible=" + num2 + ", source[thingRequest/custom]=" + Interlocked.Read(ref thingRequestSourceCalls) + "/" + Interlocked.Read(ref customSourceCalls) + ", accelerated=" + num + ", acceleratedNull=" + Interlocked.Read(ref acceleratedNoResult) + ", fallback=" + Interlocked.Read(ref fallbackCalls) + ", bypass[shape/sourceShape/mobile/small/large]=" + Interlocked.Read(ref shapeBypasses) + "/" + Interlocked.Read(ref sourceShapeBypasses) + "/" + Interlocked.Read(ref mobileSourceBypasses) + "/" + Interlocked.Read(ref smallSourceBypasses) + "/" + Interlocked.Read(ref largeSourceBypasses) + ", membership[hits/refresh/rejected]=" + Interlocked.Read(ref membershipHits) + "/" + Interlocked.Read(ref membershipRefreshes) + "/" + Interlocked.Read(ref membershipRefreshRejected) + ", fabricMisses=" + Interlocked.Read(ref fabricMisses) + ", staleFallbacks=" + Interlocked.Read(ref staleFallbacks) + ", avgSource=" + num5.ToString("F1") + ", avgVisited=" + num6.ToString("F1") + ", avgAvoided=" + num7.ToString("F1") + ", bucketVisits=" + Interlocked.Read(ref bucketVisits) + ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) + ", validatorChecks=" + Interlocked.Read(ref validatorChecks) + ", failures=" + Interlocked.Read(ref failures) + ", patchFailures=" + Interlocked.Read(ref patchFailures) + ". No-wait: first/stale/missing snapshots fall through; workers never run live validators or Reachability.";
	}
}
