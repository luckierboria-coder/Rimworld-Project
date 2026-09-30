using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class DoBillTailFabric092
{
	private enum ValidationFailure
	{
		None,
		InvalidMember,
		Pawn,
		Unspawned,
		InvalidPosition,
		CountMismatch
	}

	private sealed class SourceState
	{
		internal int SourceId;

		internal Thing[] Members;
	}

	private struct SourceKey : IEquatable<SourceKey>
	{
		private readonly int mapId;

		private readonly int count;

		private readonly int hash;

		internal SourceKey(int mapId, int count, int hash)
		{
			this.mapId = mapId;
			this.count = count;
			this.hash = hash;
		}

		public bool Equals(SourceKey other)
		{
			if (mapId == other.mapId && count == other.count)
			{
				return hash == other.hash;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is SourceKey)
			{
				return Equals((SourceKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (((mapId * 397) ^ count) * 397) ^ hash;
		}
	}

	private const int MinCount = 16;

	private const int MaxCount = 127;

	private const int TailThresholdMs = 16;

	private const int MaxTrackedStates = 2048;

	private const int ColdAfterZeroYield = 256;

	private const int ColdProbeMask = 63;

	private static readonly long TailThresholdTicks = Math.Max(1L, Stopwatch.Frequency * 16 / 1000);

	private static readonly Dictionary<SourceKey, SourceState> States = new Dictionary<SourceKey, SourceState>();

	private static int nextSourceId = 400000;

	private static bool patched;

	private static bool coldMode;

	private static int zeroYieldStreak;

	private static int coldProbeSerial;

	private static int failureLogs;

	private static long observed;

	private static long tailEligible;

	private static long registrations;

	private static long registrationRejected;

	private static long snapshotHits;

	private static long snapshotMisses;

	private static long accelerated;

	private static long broadBypass;

	private static long liveFallback;

	private static long liveChecks;

	private static long candidatesVisited;

	private static long stateResets;

	private static long invalidMemberBypass;

	private static long pawnBypass;

	private static long unspawnedBypass;

	private static long invalidPositionBypass;

	private static long countMismatchBypass;

	private static long coldEnters;

	private static long coldExits;

	private static long coldBypasses;

	private static long coldProbes;

	internal static void Apply(Harmony harmony)
	{
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
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
			if (!(methodBase == null))
			{
				HarmonyMethod val = new HarmonyMethod(typeof(DoBillTailFabric092), "Prefix", (Type[])null);
				val.priority = 1020;
				harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				patched = true;
				Log.Message("[RimMT] DoBill worker-tail fabric active with zero-yield cold sleep: repeated 16..127 static bill-giver sets can use worker-maintained spatial snapshots after a 16ms JobGiver tail threshold; 256 consecutive zero-yield eligible tails enter 1/64 probing until useful membership returns.");
			}
		}
		catch (Exception ex)
		{
			patched = false;
			Log.Warning("[RimMT] DoBill worker-tail fabric install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static bool Prefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin, int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions, ref Thing __result)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Invalid comparison between Unknown and I4
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Invalid comparison between Unknown and I4
		//IL_0239: Unknown result type (might be due to invalid IL or missing references)
		//IL_025e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0260: Unknown result type (might be due to invalid IL or missing references)
		//IL_0261: Unknown result type (might be due to invalid IL or missing references)
		if (!patched || !FeatureGate.IsEnabled("parallel.jobPartition") || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || customGlobalSearchSet == null)
		{
			return true;
		}
		observed++;
		long currentScopeStartTicks = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
		if (currentScopeStartTicks <= 0 || Stopwatch.GetTimestamp() - currentScopeStartTicks < TailThresholdTicks)
		{
			return true;
		}
		if (map == null || map.Disposed || !((IntVec3)(ref root)).IsValid || !GenGrid.InBounds(root, map) || !((ThingRequest)(ref thingReq)).IsUndefined || (int)traversableRegionTypes != 14 || ignoreEntirelyForbiddenRegions || (searchRegionsMax >= 0 && !forceAllowGlobalSearch))
		{
			return true;
		}
		if (!(customGlobalSearchSet is ICollection<Thing> { Count: >=16, Count: <=127 } collection))
		{
			return true;
		}
		tailEligible++;
		if (ShouldColdBypass())
		{
			return true;
		}
		try
		{
			int count = collection.Count;
			if (!TryValidateAndHash(collection, map, count, out var hash, out var failure))
			{
				CountValidationFailure(failure);
				RecordZeroYield();
				return true;
			}
			SourceKey key = new SourceKey(map.uniqueID, count, hash);
			if (!States.TryGetValue(key, out var value) || !MembershipMatches(collection, value.Members, count))
			{
				if (!TryCaptureMembers(collection, map, count, out var members))
				{
					invalidMemberBypass++;
					RecordZeroYield();
					return true;
				}
				if (States.Count >= 2048)
				{
					States.Clear();
					stateResets++;
				}
				value = new SourceState
				{
					SourceId = ++nextSourceId,
					Members = members
				};
				States[key] = value;
				if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, value.SourceId, members))
				{
					registrationRejected++;
					return true;
				}
				registrations++;
				MarkUseful();
				return true;
			}
			MarkUseful();
			if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, value.SourceId, out var source) || source == null || source.Count != count)
			{
				snapshotMisses++;
				return true;
			}
			snapshotHits++;
			int num = DynamicLiveCap();
			if (source.EstimateCandidates(root, maxDistance, num) > num)
			{
				broadBypass++;
				return true;
			}
			if (!source.TryFindClosest(root, map, peMode, traverseParams, maxDistance, validator, num, out var chosen, out var visited, out var _, out var reaches, out var validations, out var _))
			{
				liveFallback++;
				return true;
			}
			__result = chosen;
			accelerated++;
			liveChecks += reaches + validations;
			candidatesVisited += visited;
			return false;
		}
		catch (Exception ex)
		{
			if (failureLogs++ < 4)
			{
				Log.Warning("[RimMT] DoBill worker-tail fabric failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
			}
			return true;
		}
	}

	private static bool ShouldColdBypass()
	{
		if (!coldMode)
		{
			return false;
		}
		if ((++coldProbeSerial & 0x3F) == 0)
		{
			coldProbes++;
			return false;
		}
		coldBypasses++;
		return true;
	}

	private static void RecordZeroYield()
	{
		if (zeroYieldStreak < int.MaxValue)
		{
			zeroYieldStreak++;
		}
		if (!coldMode && zeroYieldStreak >= 256)
		{
			coldMode = true;
			coldProbeSerial = 0;
			coldEnters++;
		}
	}

	private static void MarkUseful()
	{
		zeroYieldStreak = 0;
		if (coldMode)
		{
			coldMode = false;
			coldProbeSerial = 0;
			coldExits++;
		}
	}

	private static bool TryValidateAndHash(ICollection<Thing> collection, Map map, int expectedCount, out int hash, out ValidationFailure failure)
	{
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		hash = 17;
		failure = ValidationFailure.None;
		int num = 0;
		foreach (Thing item in collection)
		{
			if (num++ >= expectedCount)
			{
				failure = ValidationFailure.CountMismatch;
				return false;
			}
			if (item == null || !(item is IBillGiver))
			{
				failure = ValidationFailure.InvalidMember;
				return false;
			}
			if (item is Pawn)
			{
				failure = ValidationFailure.Pawn;
				return false;
			}
			if (!item.Spawned || item.MapHeld != map)
			{
				failure = ValidationFailure.Unspawned;
				return false;
			}
			IntVec3 position = item.Position;
			if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, map))
			{
				failure = ValidationFailure.InvalidPosition;
				return false;
			}
			hash = hash * 31 + item.thingIDNumber;
		}
		if (num == expectedCount)
		{
			return true;
		}
		failure = ValidationFailure.CountMismatch;
		return false;
	}

	private static void CountValidationFailure(ValidationFailure failure)
	{
		switch (failure)
		{
		case ValidationFailure.Pawn:
			pawnBypass++;
			break;
		case ValidationFailure.Unspawned:
			unspawnedBypass++;
			break;
		case ValidationFailure.InvalidPosition:
			invalidPositionBypass++;
			break;
		case ValidationFailure.CountMismatch:
			countMismatchBypass++;
			break;
		default:
			invalidMemberBypass++;
			break;
		}
	}

	private static bool TryCaptureMembers(ICollection<Thing> collection, Map map, int expectedCount, out Thing[] members)
	{
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		members = (Thing[])(object)new Thing[expectedCount];
		int num = 0;
		foreach (Thing item in collection)
		{
			if (num >= expectedCount || item == null || !(item is IBillGiver) || item is Pawn || !item.Spawned || item.MapHeld != map)
			{
				members = null;
				return false;
			}
			IntVec3 position = item.Position;
			if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, map))
			{
				members = null;
				return false;
			}
			members[num++] = item;
		}
		if (num == expectedCount)
		{
			return true;
		}
		members = null;
		return false;
	}

	private static bool MembershipMatches(ICollection<Thing> collection, Thing[] members, int expectedCount)
	{
		if (members == null || members.Length != expectedCount || collection.Count != expectedCount)
		{
			return false;
		}
		int num = 0;
		foreach (Thing item in collection)
		{
			if (num >= expectedCount || item != members[num++])
			{
				return false;
			}
		}
		return num == expectedCount;
	}

	private static int DynamicLiveCap()
	{
		return AdaptiveLoadBalancer.Pressure switch
		{
			LoadPressure.Low => 96, 
			LoadPressure.Normal => 64, 
			LoadPressure.High => 32, 
			_ => 16, 
		};
	}

	internal static string Summary()
	{
		return "DoBill worker-tail fabric: patched=" + patched + ", coldMode=" + coldMode + ", observed=" + observed + ", tailEligible=" + tailEligible + ", registrations=" + registrations + ", registrationRejected=" + registrationRejected + ", snapshotHits=" + snapshotHits + ", snapshotMisses=" + snapshotMisses + ", accelerated=" + accelerated + ", broadBypass=" + broadBypass + ", liveFallback=" + liveFallback + ", validationBypass=[invalidMember=" + invalidMemberBypass + ", pawn=" + pawnBypass + ", unspawned=" + unspawnedBypass + ", invalidPosition=" + invalidPositionBypass + ", countMismatch=" + countMismatchBypass + "], zeroYieldStreak=" + zeroYieldStreak + ", coldEnters=" + coldEnters + ", coldExits=" + coldExits + ", coldBypasses=" + coldBypasses + ", coldProbes=" + coldProbes + ", liveChecks=" + liveChecks + ", candidatesVisited=" + candidatesVisited + ", retainedStates=" + States.Count + ", stateResets=" + stateResets + ", currentLiveCap=" + DynamicLiveCap() + ".";
	}
}
