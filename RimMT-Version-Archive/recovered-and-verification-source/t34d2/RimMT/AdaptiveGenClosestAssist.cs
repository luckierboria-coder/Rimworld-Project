using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class AdaptiveGenClosestAssist
{
	private enum SourceKind
	{
		None,
		Thing,
		Pawn,
		Building
	}

	private enum CaptureFailure
	{
		None,
		Invalid,
		Mobile,
		Unspawned
	}

	private sealed class SourceState
	{
		internal int SourceId;

		internal int MapId;

		internal Thing[] Members;
	}

	private const string FeatureId = "parallel.jobPartition";

	private const int MinCandidateCount = 96;

	private const int MaxLiveChecks = 64;

	private const long ColdAfterObservedWithoutUseful = 50000L;

	private const int ColdProbeMask = 255;

	private static readonly ConditionalWeakTable<object, SourceState> States = new ConditionalWeakTable<object, SourceState>();

	private static volatile bool compatibilityReady;

	private static int coldModeValue;

	private static int nextSourceId;

	[ThreadStatic]
	private static int assistDepth;

	private static long observedCalls;

	private static long eligibleCalls;

	private static long acceleratedCalls;

	private static long acceleratedNoResult;

	private static long fallbackCalls;

	private static long nonListBypasses;

	private static long shapeBypasses;

	private static long smallSetBypasses;

	private static long haulableBypasses;

	private static long mobileSourceBypasses;

	private static long unspawnedSourceBypasses;

	private static long membershipHits;

	private static long membershipRefreshes;

	private static long membershipRefreshRejected;

	private static long fabricMisses;

	private static long broadQueryBypasses;

	private static long liveCapFallbacks;

	private static long staleFallbacks;

	private static long estimatedCandidatesTotal;

	private static long queryTicks;

	private static long queryTicksMax;

	private static long bucketVisits;

	private static long candidatesVisited;

	private static long candidatesAvoided;

	private static long reachabilityChecks;

	private static long validatorChecks;

	private static long failures;

	private static long lastUsefulObserved;

	private static long coldBypasses;

	private static long coldProbes;

	private static long coldEnters;

	private static long coldExits;

	internal static void Apply(Harmony harmony)
	{
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Expected O, but got Unknown
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
				FeatureGate.Suppress("parallel.jobPartition", "GenClosest.ClosestThingReachable target not found");
				Log.Warning("[RimMT] parallel.jobPartition V0.4.14 unavailable: GenClosest.ClosestThingReachable target not found.");
				return;
			}
			CompatibilityGuard.RegisterTarget("parallel.jobPartition", methodBase);
			HarmonyMethod val = new HarmonyMethod(typeof(AdaptiveGenClosestAssist), "Prefix", (Type[])null);
			val.priority = 900;
			harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			Log.Message("[RimMT] parallel.jobPartition V0.4.14 persistent-fabric consumer installed with zero-yield cold sleep. After 50k calls without a useful static source/acceleration it probes 1/256 calls until useful work reappears.");
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress("parallel.jobPartition", "persistent-fabric GenClosest patch failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] parallel.jobPartition V0.4.14 patch failed; Vanilla GenClosest remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void MarkCompatibilityReady()
	{
		compatibilityReady = true;
	}

	public static bool Prefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin, int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions, ref Thing __result)
	{
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Invalid comparison between Unknown and I4
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Invalid comparison between Unknown and I4
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_029f: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a2: Unknown result type (might be due to invalid IL or missing references)
		long observedNow = Interlocked.Increment(ref observedCalls);
		if (!compatibilityReady || !FeatureGate.IsEnabled("parallel.jobPartition") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		if (ShouldColdBypass(observedNow))
		{
			return true;
		}
		if (map == null || map.Disposed || !((IntVec3)(ref root)).IsValid || !GenGrid.InBounds(root, map) || customGlobalSearchSet == null)
		{
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		if (!((ThingRequest)(ref thingReq)).IsUndefined || (int)traversableRegionTypes != 14 || ignoreEntirelyForbiddenRegions || (searchRegionsMax >= 0 && !forceAllowGlobalSearch))
		{
			Interlocked.Increment(ref shapeBypasses);
			return true;
		}
		if (!TryGetSourceShape(customGlobalSearchSet, out var kind, out var count))
		{
			Interlocked.Increment(ref nonListBypasses);
			return true;
		}
		if (count < 96)
		{
			Interlocked.Increment(ref smallSetBypasses);
			return true;
		}
		if (customGlobalSearchSet is IList<Thing>)
		{
			try
			{
				List<Thing> list = ((map.listerHaulables == null) ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling());
				if (list != null && customGlobalSearchSet == list)
				{
					Interlocked.Increment(ref haulableBypasses);
					return true;
				}
			}
			catch
			{
				Interlocked.Increment(ref fallbackCalls);
				return true;
			}
		}
		if (assistDepth != 0)
		{
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		Interlocked.Increment(ref eligibleCalls);
		SourceState value = States.GetValue(customGlobalSearchSet, CreateState);
		if (value.MapId != map.uniqueID)
		{
			value.MapId = map.uniqueID;
			value.Members = null;
		}
		if (!MembershipMatches(customGlobalSearchSet, kind, count, value.Members))
		{
			if (!TryCaptureMembers(customGlobalSearchSet, kind, count, map, out var members, out var failure))
			{
				switch (failure)
				{
				case CaptureFailure.Mobile:
					Interlocked.Increment(ref mobileSourceBypasses);
					break;
				case CaptureFailure.Unspawned:
					Interlocked.Increment(ref unspawnedSourceBypasses);
					break;
				default:
					Interlocked.Increment(ref fallbackCalls);
					break;
				}
				return true;
			}
			value.Members = members;
			Interlocked.Increment(ref membershipRefreshes);
			if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, value.SourceId, members))
			{
				Interlocked.Increment(ref membershipRefreshRejected);
				return true;
			}
			MarkUseful(observedNow);
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		Interlocked.Increment(ref membershipHits);
		MarkUseful(observedNow);
		if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, value.SourceId, out var source) || source == null || source.Count != count)
		{
			Interlocked.Increment(ref fabricMisses);
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		int num = source.EstimateCandidates(root, maxDistance, 64);
		Interlocked.Add(ref estimatedCandidatesTotal, num);
		if (num > 64)
		{
			Interlocked.Increment(ref broadQueryBypasses);
			Interlocked.Increment(ref fallbackCalls);
			return true;
		}
		assistDepth = 1;
		try
		{
			long timestamp = Stopwatch.GetTimestamp();
			Thing chosen;
			int visited;
			int bucketsSeen;
			int reaches;
			int validations;
			bool staleDetected;
			bool num2 = source.TryFindClosest(root, map, peMode, traverseParams, maxDistance, validator, 64, out chosen, out visited, out bucketsSeen, out reaches, out validations, out staleDetected);
			long value2 = Stopwatch.GetTimestamp() - timestamp;
			Interlocked.Add(ref queryTicks, value2);
			UpdateMax(ref queryTicksMax, value2);
			if (!num2)
			{
				if (staleDetected)
				{
					Interlocked.Increment(ref staleFallbacks);
				}
				else
				{
					Interlocked.Increment(ref liveCapFallbacks);
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
			Interlocked.Add(ref bucketVisits, bucketsSeen);
			Interlocked.Add(ref candidatesVisited, visited);
			Interlocked.Add(ref reachabilityChecks, reaches);
			Interlocked.Add(ref validatorChecks, validations);
			long num3 = count - visited;
			if (num3 > 0)
			{
				Interlocked.Add(ref candidatesAvoided, num3);
			}
			MarkUseful(observedNow);
			return false;
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			CircuitBreaker.RecordFailure("parallel.jobPartition", ex);
			Log.Warning("[RimMT] parallel.jobPartition V0.4.14 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
			return true;
		}
		finally
		{
			assistDepth = 0;
		}
	}

	private static bool ShouldColdBypass(long observedNow)
	{
		if (Volatile.Read(ref coldModeValue) == 0)
		{
			long num = Interlocked.Read(ref lastUsefulObserved);
			if (observedNow - num < 50000)
			{
				return false;
			}
			Volatile.Write(ref coldModeValue, 1);
			Interlocked.Increment(ref coldEnters);
		}
		if ((observedNow & 0xFF) == 0L)
		{
			Interlocked.Increment(ref coldProbes);
			return false;
		}
		Interlocked.Increment(ref coldBypasses);
		return true;
	}

	private static void MarkUseful(long observedNow)
	{
		Interlocked.Exchange(ref lastUsefulObserved, observedNow);
		if (Volatile.Read(ref coldModeValue) != 0)
		{
			Volatile.Write(ref coldModeValue, 0);
			Interlocked.Increment(ref coldExits);
		}
	}

	private static SourceState CreateState(object source)
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
		for (int i = 0; i < count; i++)
		{
			if (GetThingAt(source, kind, i) != members[i])
			{
				return false;
			}
		}
		return true;
	}

	private static bool TryCaptureMembers(object source, SourceKind kind, int count, Map map, out Thing[] members, out CaptureFailure failure)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		members = (Thing[])(object)new Thing[count];
		failure = CaptureFailure.None;
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

	private static bool TryGetSourceShape(object source, out SourceKind kind, out int count)
	{
		if (source is IList<Thing> list)
		{
			kind = SourceKind.Thing;
			count = list.Count;
			return true;
		}
		if (source is IList<Pawn> list2)
		{
			kind = SourceKind.Pawn;
			count = list2.Count;
			return true;
		}
		if (source is IList<Building> list3)
		{
			kind = SourceKind.Building;
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
			SourceKind.Pawn => ((IList<Pawn>)source)[index], 
			SourceKind.Building => ((IList<Building>)source)[index], 
			_ => null, 
		});
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref acceleratedCalls);
		long num2 = Interlocked.Read(ref candidatesVisited);
		long num3 = Interlocked.Read(ref candidatesAvoided);
		long num4 = Interlocked.Read(ref eligibleCalls);
		double num5 = ((num <= 0) ? 0.0 : ((double)num2 / (double)num));
		double num6 = ((num <= 0) ? 0.0 : ((double)num3 / (double)num));
		double num7 = ((num4 <= 0) ? 0.0 : ((double)Interlocked.Read(ref estimatedCandidatesTotal) / (double)num4));
		double num8 = ((num <= 0) ? 0.0 : ((double)Interlocked.Read(ref queryTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num9 = (double)Interlocked.Read(ref queryTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		return "Persistent-fabric GenClosest V0.4.14: compatibilityReady=" + compatibilityReady + ", coldMode=" + (Volatile.Read(ref coldModeValue) != 0) + ", observed=" + Interlocked.Read(ref observedCalls) + ", eligible=" + num4 + ", accelerated=" + num + ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) + ", fallback=" + Interlocked.Read(ref fallbackCalls) + ", coldEnters=" + Interlocked.Read(ref coldEnters) + ", coldExits=" + Interlocked.Read(ref coldExits) + ", coldBypasses=" + Interlocked.Read(ref coldBypasses) + ", coldProbes=" + Interlocked.Read(ref coldProbes) + ", nonListBypass=" + Interlocked.Read(ref nonListBypasses) + ", shapeBypass=" + Interlocked.Read(ref shapeBypasses) + ", smallSetBypass=" + Interlocked.Read(ref smallSetBypasses) + ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) + ", mobileSourceBypass=" + Interlocked.Read(ref mobileSourceBypasses) + ", unspawnedSourceBypass=" + Interlocked.Read(ref unspawnedSourceBypasses) + ", membershipHits=" + Interlocked.Read(ref membershipHits) + ", membershipRefreshes=" + Interlocked.Read(ref membershipRefreshes) + ", membershipRefreshRejected=" + Interlocked.Read(ref membershipRefreshRejected) + ", fabricMisses=" + Interlocked.Read(ref fabricMisses) + ", broadQueryBypass=" + Interlocked.Read(ref broadQueryBypasses) + ", liveCapFallback=" + Interlocked.Read(ref liveCapFallbacks) + ", staleFallback=" + Interlocked.Read(ref staleFallbacks) + ", maxLiveChecks=" + 64 + ", avgEstimate=" + num7.ToString("F1") + ", avgQueryUs=" + num8.ToString("F2") + ", maxQueryUs=" + num9.ToString("F2") + ", bucketVisits=" + Interlocked.Read(ref bucketVisits) + ", candidatesVisited=" + num2 + ", avgCandidatesVisited=" + num5.ToString("F1") + ", candidatesAvoided=" + num3 + ", avgCandidatesAvoided=" + num6.ToString("F1") + ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) + ", validatorChecks=" + Interlocked.Read(ref validatorChecks) + ", failures=" + Interlocked.Read(ref failures) + ". Cold sleep bypasses only this consumer; Vanilla remains authoritative for every bypass.";
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
