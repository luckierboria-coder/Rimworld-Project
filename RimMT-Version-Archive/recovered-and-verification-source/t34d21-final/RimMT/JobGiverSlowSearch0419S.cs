using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class JobGiverSlowSearch0419S
{
	private enum RescueRoute
	{
		StaticLarge,
		TailList,
		CustomTail
	}

	private enum PenPrefilterKind
	{
		None,
		TakeToPen,
		TakeRoamingAnimalsToPen,
		DerivedTakeToPen
	}

	private enum TargetedPrefilterKind
	{
		None,
		HaulCorpses,
		TakeEntityToHoldingPlatform,
		FeedHemogen,
		VisitSickPawn,
		FightFires
	}

	private sealed class HeavyValidatorStats
	{
		internal long Calls;

		internal long Rejects;
	}

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

	internal const string FeatureId = "ai.jobSlowSearch";

	private const int LargeSearchThreshold = 256;

	private const int TailMinSourceCount = 16;

	private const int TailRescueThresholdMs = 32;

	private const int EarlyKnownHeavyThresholdMs = 8;

	private const int TargetedEarlyThresholdMs = 8;

	private const long EarlyKnownHeavyMinCalls = 2L;

	private const long EarlyKnownHeavyMinRejects = 512L;

	private const int MaxSourceCount = 16384;

	private const int HeavyRejectThreshold = 64;

	private const int MaxHeavyValidatorKeys = 24;

	private const int MaxHeavyWorkGiverKeys = 64;

	private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * 32 / 1000);

	private static readonly long EarlyKnownHeavyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * 8 / 1000);

	private static readonly long TargetedEarlyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * 8 / 1000);

	[ThreadStatic]
	private static Candidate[] candidateScratch;

	[ThreadStatic]
	private static bool t8DetermineActive;

	[ThreadStatic]
	private static string t8DetermineTopWorkGiver;

	[ThreadStatic]
	private static int t8DetermineTopRejects;

	private static volatile bool enabled = true;

	private static volatile bool patched;

	private static int failureLogs;

	private static long observed;

	private static long staticLargeEligible;

	private static long tailEligible;

	private static long customTailEligible;

	private static long accelerated;

	private static long acceleratedNull;

	private static long validatorRejected;

	private static long reachRejected;

	private static long staticLargeValidatorRejected;

	private static long tailListValidatorRejected;

	private static long customTailValidatorRejected;

	private static long staticLargeReachRejected;

	private static long tailListReachRejected;

	private static long customTailReachRejected;

	private static long heavyValidatorCalls;

	private static long heavyValidatorRejects;

	private static long heavyWorkGiverResolved;

	private static long heavyWorkGiverUnresolved;

	private static long earlyKnownChecks;

	private static long earlyKnownHits;

	private static long earlyKnownListAdmissions;

	private static long earlyKnownCustomAdmissions;

	private static long penPrefilterCalls;

	private static long penPrefilterRejected;

	private static long penPrefilterTakeToPenRejected;

	private static long penPrefilterRoamingRejected;

	private static long targetedPrefilterCalls;

	private static long targetedPrefilterRejected;

	private static long targetedHaulCorpsesRejected;

	private static long targetedHoldingPlatformRejected;

	private static long targetedFeedHemogenRejected;

	private static long targetedVisitSickRejected;

	private static long targetedFightFiresRejected;

	private static long targetedPrefilterAuthorityBypass;

	private static long targetedEarlyChecks;

	private static long targetedEarlyHits;

	private static long targetedEarlyListAdmissions;

	private static long targetedEarlyCustomAdmissions;

	private static long targetedEarlyAuthorityBypass;

	private static long actualValidatorCalls;

	private static long failures;

	private static readonly Dictionary<string, HeavyValidatorStats> HeavyValidators = new Dictionary<string, HeavyValidatorStats>();

	private static readonly Dictionary<string, HeavyValidatorStats> HeavyWorkGivers = new Dictionary<string, HeavyValidatorStats>();

	private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();

	private static readonly Dictionary<Type, bool> TargetedPrefilterAuthorityCache = new Dictionary<Type, bool>();

	internal static void Apply(Harmony harmony)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			int num = 0;
			foreach (MethodInfo methodInfo in methods)
			{
				if (IsSupportedOverload(methodInfo))
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(JobGiverSlowSearch0419S), "Prefix", (Type[])null)
					{
						priority = 900
					}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			patched = num > 0;
			Log.Message("[RimMT] Unified S4 slow-search rescue active on " + num + " ClosestThingReachable overload(s); heavy validator-tail attribution is thresholded and resolves WorkGiver identity only on >=64-reject calls.");
		}
		catch (Exception ex)
		{
			patched = false;
			Log.Warning("[RimMT] Unified S4 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void SetEnabled(bool value)
	{
		enabled = value;
	}

	private static bool IsSupportedOverload(MethodInfo method)
	{
		if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable")
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length >= 8 && parameters[0].ParameterType == typeof(IntVec3) && parameters[1].ParameterType == typeof(Map) && parameters[2].ParameterType == typeof(ThingRequest) && parameters[3].ParameterType == typeof(PathEndMode) && parameters[4].ParameterType == typeof(TraverseParms) && parameters[5].ParameterType == typeof(float) && parameters[6].ParameterType == typeof(Predicate<Thing>))
		{
			return typeof(IEnumerable<Thing>).IsAssignableFrom(parameters[7].ParameterType);
		}
		return false;
	}

	public static bool Prefix(IntVec3 __0, Map __1, ThingRequest __2, PathEndMode __3, TraverseParms __4, float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Invalid comparison between Unknown and I4
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Invalid comparison between Unknown and I4
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Invalid comparison between Unknown and I4
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		if (!enabled || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		observed++;
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
		long currentScopeStartTicks = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
		if (currentScopeStartTicks <= 0)
		{
			return true;
		}
		if (__7 != null)
		{
			long num = Stopwatch.GetTimestamp() - currentScopeStartTicks;
			if (num < TailRescueThresholdTicks)
			{
				if (num < TargetedEarlyThresholdTicks || !CanUseTargetedEarly(__6))
				{
					return true;
				}
				targetedEarlyCustomAdmissions++;
			}
			customTailEligible++;
			return TryAccelerateCustom(__7, __0, __1, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
		}
		List<Thing> list;
		try
		{
			list = __1.listerThings.ThingsMatching(__2);
		}
		catch
		{
			return true;
		}
		if (list == null)
		{
			return true;
		}
		int count = list.Count;
		if (count > 16384)
		{
			return true;
		}
		if (count >= 256)
		{
			staticLargeEligible++;
			return TryAccelerateList(list, count, __0, __1, __3, __4, __5, __6, RescueRoute.StaticLarge, ref __result);
		}
		if (count < 16)
		{
			return true;
		}
		long num2 = Stopwatch.GetTimestamp() - currentScopeStartTicks;
		if (num2 < TailRescueThresholdTicks)
		{
			if (num2 < TargetedEarlyThresholdTicks || !CanUseTargetedEarly(__6))
			{
				return true;
			}
			targetedEarlyListAdmissions++;
		}
		tailEligible++;
		return TryAccelerateList(list, count, __0, __1, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
	}

	private static bool TryAccelerateList(List<Thing> source, int count, IntVec3 root, Map map, PathEndMode endMode, TraverseParms traverseParms, float maxDistance, Predicate<Thing> validator, RescueRoute route, ref Thing result)
	{
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Candidate[] array = EnsureScratch(count, 0);
			double num = (double)maxDistance * (double)maxDistance;
			int kept = 0;
			for (int i = 0; i < count; i++)
			{
				Thing val = source[i];
				if (val == null || !val.Spawned || val.Map != map)
				{
					continue;
				}
				IntVec3 position = val.Position;
				if (((IntVec3)(ref position)).IsValid)
				{
					long num2 = (long)position.x - (long)root.x;
					long num3 = (long)position.z - (long)root.z;
					long num4 = num2 * num2 + num3 * num3;
					if (!((double)num4 > num))
					{
						array[kept++] = new Candidate(val, num4, i);
					}
				}
			}
			return RunCandidates(array, kept, root, map, endMode, traverseParms, validator, route, ref result);
		}
		catch (Exception ex)
		{
			return Failure(ex);
		}
	}

	private static bool TryAccelerateCustom(IEnumerable<Thing> source, IntVec3 root, Map map, PathEndMode endMode, TraverseParms traverseParms, float maxDistance, Predicate<Thing> validator, RescueRoute route, ref Thing result)
	{
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Candidate[] array = EnsureScratch(256, 0);
			double num = (double)maxDistance * (double)maxDistance;
			int num2 = 0;
			int num3 = 0;
			foreach (Thing item in source)
			{
				int sourceIndex = num3++;
				if (num3 > 16384)
				{
					return true;
				}
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
						array = EnsureScratch(num2 + 1, num2);
					}
					array[num2++] = new Candidate(item, num6, sourceIndex);
				}
			}
			if (num3 < 16)
			{
				return true;
			}
			return RunCandidates(array, num2, root, map, endMode, traverseParms, validator, route, ref result);
		}
		catch (Exception ex)
		{
			return Failure(ex);
		}
	}

	private static bool RunCandidates(Candidate[] candidates, int kept, IntVec3 root, Map map, PathEndMode endMode, TraverseParms traverseParms, Predicate<Thing> validator, RescueRoute route, ref Thing result)
	{
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_023c: Unknown result type (might be due to invalid IL or missing references)
		//IL_023f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0244: Unknown result type (might be due to invalid IL or missing references)
		//IL_0246: Unknown result type (might be due to invalid IL or missing references)
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		WorkGiver_Scanner scanner = TryResolveScanner(validator);
		PenPrefilterKind penPrefilterKind = ResolvePenPrefilter(scanner);
		if (penPrefilterKind != PenPrefilterKind.None && kept > 0)
		{
			int num4 = 0;
			for (int i = 0; i < kept; i++)
			{
				penPrefilterCalls++;
				Candidate candidate = candidates[i];
				if (!PassPenCheapNegative(penPrefilterKind, traverseParms.pawn, candidate.Thing))
				{
					penPrefilterRejected++;
					if (penPrefilterKind == PenPrefilterKind.TakeRoamingAnimalsToPen)
					{
						penPrefilterRoamingRejected++;
					}
					else
					{
						penPrefilterTakeToPenRejected++;
					}
				}
				else
				{
					candidates[num4++] = candidate;
				}
			}
			kept = num4;
		}
		TargetedPrefilterKind targetedPrefilterKind = ResolveTargetedPrefilter(scanner);
		if (targetedPrefilterKind != TargetedPrefilterKind.None && kept > 0)
		{
			if (!IsTargetedPrefilterAuthoritySafe(scanner))
			{
				targetedPrefilterAuthorityBypass++;
			}
			else
			{
				int num5 = 0;
				for (int j = 0; j < kept; j++)
				{
					targetedPrefilterCalls++;
					Candidate candidate2 = candidates[j];
					if (!PassTargetedCheapNegative(targetedPrefilterKind, traverseParms.pawn, candidate2.Thing))
					{
						targetedPrefilterRejected++;
						switch (targetedPrefilterKind)
						{
						case TargetedPrefilterKind.HaulCorpses:
							targetedHaulCorpsesRejected++;
							break;
						case TargetedPrefilterKind.TakeEntityToHoldingPlatform:
							targetedHoldingPlatformRejected++;
							break;
						case TargetedPrefilterKind.FeedHemogen:
							targetedFeedHemogenRejected++;
							break;
						case TargetedPrefilterKind.VisitSickPawn:
							targetedVisitSickRejected++;
							break;
						case TargetedPrefilterKind.FightFires:
							targetedFightFiresRejected++;
							break;
						}
					}
					else
					{
						candidates[num5++] = candidate2;
					}
				}
				kept = num5;
			}
		}
		if (kept > 0 && CarrierMechCheapNegative093T8.TryPrepare(scanner, out var kind))
		{
			int num6 = 0;
			for (int k = 0; k < kept; k++)
			{
				Candidate candidate3 = candidates[k];
				if (!CarrierMechCheapNegative093T8.Reject(kind, traverseParms.pawn, candidate3.Thing))
				{
					candidates[num6++] = candidate3;
				}
			}
			kept = num6;
		}
		if (kept > 1)
		{
			Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
		}
		for (int l = 0; l < kept; l++)
		{
			Thing thing = candidates[l].Thing;
			if (validator != null)
			{
				num++;
				if (!validator(thing))
				{
					num2++;
					continue;
				}
			}
			if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
			{
				num3++;
				continue;
			}
			RecordRoute(route, num, num2, num3, validator);
			result = thing;
			accelerated++;
			return false;
		}
		RecordRoute(route, num, num2, num3, validator);
		result = null;
		accelerated++;
		acceleratedNull++;
		return false;
	}

	private static WorkGiver_Scanner TryResolveScanner(Predicate<Thing> validator)
	{
		if (validator == null)
		{
			return null;
		}
		try
		{
			object target = validator.Target;
			if (target == null)
			{
				return null;
			}
			Type type = target.GetType();
			if (!ScannerFieldCache.TryGetValue(type, out var value))
			{
				value = ResolveScannerField(type);
				ScannerFieldCache[type] = value;
			}
			return (WorkGiver_Scanner)((value == null) ? null : /*isinst with value type is only supported in some contexts*/);
		}
		catch
		{
			return null;
		}
	}

	private static bool IsKnownHeavyWorkGiver(Predicate<Thing> validator)
	{
		earlyKnownChecks++;
		WorkGiver_Scanner val = TryResolveScanner(validator);
		if (val == null)
		{
			return false;
		}
		string text = ((((WorkGiver)val).def == null || string.IsNullOrEmpty(((Def)((WorkGiver)val).def).defName)) ? ((object)val).GetType().FullName : ((Def)((WorkGiver)val).def).defName);
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		if (!HeavyWorkGivers.TryGetValue(text, out var value) || value == null)
		{
			return false;
		}
		if (value.Calls < 2 || value.Rejects < 512)
		{
			return false;
		}
		earlyKnownHits++;
		return true;
	}

	private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
	{
		if (scanner == null)
		{
			return PenPrefilterKind.None;
		}
		Type type = ((object)scanner).GetType();
		if (type == typeof(WorkGiver_TakeRoamingAnimalsToPen))
		{
			return PenPrefilterKind.TakeRoamingAnimalsToPen;
		}
		if (type == typeof(WorkGiver_TakeToPen))
		{
			return PenPrefilterKind.TakeToPen;
		}
		if (scanner is WorkGiver_TakeToPen)
		{
			return PenPrefilterKind.DerivedTakeToPen;
		}
		return PenPrefilterKind.None;
	}

	private static bool PassPenCheapNegative(PenPrefilterKind kind, Pawn worker, Thing thing)
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Pawn val = (Pawn)(object)((thing is Pawn) ? thing : null);
			if (val == null || val.RaceProps == null || !val.RaceProps.Animal)
			{
				return false;
			}
			if (worker == null)
			{
				return true;
			}
			if (ForbidUtility.IsForbidden(((Thing)val).Position, worker))
			{
				return false;
			}
			Map map = ((Thing)val).Map;
			if (map != null && map.designationManager.DesignationOn((Thing)(object)val, DesignationDefOf.ReleaseAnimalToWild) != null)
			{
				return false;
			}
			bool flag = val.MentalStateDef == MentalStateDefOf.Roaming;
			if (kind == PenPrefilterKind.TakeRoamingAnimalsToPen && !flag)
			{
				return false;
			}
			if (kind == PenPrefilterKind.TakeToPen && !flag && val.MentalStateDef != null)
			{
				return false;
			}
			return true;
		}
		catch
		{
			return true;
		}
	}

	private static bool CanUseTargetedEarly(Predicate<Thing> validator)
	{
		targetedEarlyChecks++;
		WorkGiver_Scanner val = TryResolveScanner(validator);
		if (val == null)
		{
			return false;
		}
		if (ResolveTargetedPrefilter(val) == TargetedPrefilterKind.None)
		{
			return false;
		}
		if (!IsTargetedPrefilterAuthoritySafe(val))
		{
			targetedEarlyAuthorityBypass++;
			return false;
		}
		targetedEarlyHits++;
		return true;
	}

	private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
	{
		if (scanner == null)
		{
			return TargetedPrefilterKind.None;
		}
		Type type = ((object)scanner).GetType();
		if (type == typeof(WorkGiver_HaulCorpses))
		{
			return TargetedPrefilterKind.HaulCorpses;
		}
		if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform))
		{
			return TargetedPrefilterKind.TakeEntityToHoldingPlatform;
		}
		if (((WorkGiver)scanner).def != null && ((Def)((WorkGiver)scanner).def).defName == "FeedHemogen" && type == typeof(Workgiver_AdministerHemogen))
		{
			return TargetedPrefilterKind.FeedHemogen;
		}
		if (((WorkGiver)scanner).def != null && ((Def)((WorkGiver)scanner).def).defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
		{
			return TargetedPrefilterKind.VisitSickPawn;
		}
		if (type.FullName == "RimWorld.WorkGiver_FightFires")
		{
			return TargetedPrefilterKind.FightFires;
		}
		return TargetedPrefilterKind.None;
	}

	private static bool IsTargetedPrefilterAuthoritySafe(WorkGiver_Scanner scanner)
	{
		if (scanner == null)
		{
			return false;
		}
		Type type = ((object)scanner).GetType();
		if (TargetedPrefilterAuthorityCache.TryGetValue(type, out var value))
		{
			return value;
		}
		bool flag = true;
		try
		{
			Type[] types = new Type[3]
			{
				typeof(Pawn),
				typeof(Thing),
				typeof(bool)
			};
			string[] array = new string[2] { "HasJobOnThing", "JobOnThing" };
			for (int i = 0; i < array.Length && flag; i++)
			{
				Type type2 = type;
				while (type2 != null && typeof(WorkGiver).IsAssignableFrom(type2))
				{
					MethodInfo method = type2.GetMethod(array[i], BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, types, null);
					if (method != null)
					{
						Patches patchInfo = Harmony.GetPatchInfo((MethodBase)method);
						if (patchInfo != null && (patchInfo.Prefixes.Count != 0 || patchInfo.Postfixes.Count != 0 || patchInfo.Transpilers.Count != 0 || patchInfo.Finalizers.Count != 0))
						{
							flag = false;
							break;
						}
					}
					type2 = type2.BaseType;
				}
			}
		}
		catch
		{
			flag = false;
		}
		TargetedPrefilterAuthorityCache[type] = flag;
		return flag;
	}

	private static bool PassTargetedCheapNegative(TargetedPrefilterKind kind, Pawn worker, Thing thing)
	{
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0295: Unknown result type (might be due to invalid IL or missing references)
		//IL_029a: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0334: Unknown result type (might be due to invalid IL or missing references)
		//IL_0341: Unknown result type (might be due to invalid IL or missing references)
		//IL_0346: Unknown result type (might be due to invalid IL or missing references)
		//IL_034a: Unknown result type (might be due to invalid IL or missing references)
		//IL_034f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0351: Unknown result type (might be due to invalid IL or missing references)
		//IL_0358: Unknown result type (might be due to invalid IL or missing references)
		//IL_0365: Unknown result type (might be due to invalid IL or missing references)
		//IL_036c: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e0: Invalid comparison between Unknown and I4
		try
		{
			switch (kind)
			{
			case TargetedPrefilterKind.HaulCorpses:
			{
				if (!(thing is Corpse))
				{
					return false;
				}
				if (worker == null || ((Thing)worker).Map == null)
				{
					return true;
				}
				Pawn val6 = ((Thing)worker).Map.physicalInteractionReservationManager.FirstReserverOf(new LocalTargetInfo(thing));
				if (val6 != null && val6.RaceProps != null && val6.RaceProps.Animal && ((Thing)val6).Faction != Faction.OfPlayer)
				{
					return false;
				}
				return true;
			}
			case TargetedPrefilterKind.TakeEntityToHoldingPlatform:
			{
				if (thing == null)
				{
					return false;
				}
				CompHoldingPlatformTarget val7 = ThingCompUtility.TryGetComp<CompHoldingPlatformTarget>(thing);
				if (val7 == null || val7.targetHolder == null)
				{
					return false;
				}
				Thing targetHolder = val7.targetHolder;
				if (targetHolder.Destroyed || targetHolder.MapHeld != thing.MapHeld)
				{
					return false;
				}
				if (val7.EntityHolder == null)
				{
					return true;
				}
				if (val7.EntityHolder.HeldPawn != null)
				{
					return false;
				}
				return true;
			}
			case TargetedPrefilterKind.FeedHemogen:
			{
				Pawn val3 = (Pawn)(object)((thing is Pawn) ? thing : null);
				if (val3 == null || val3 == worker)
				{
					return false;
				}
				Gene_Hemogen val4 = ((val3.genes == null) ? null : val3.genes.GetFirstGeneOfType<Gene_Hemogen>());
				if (val4 == null || ((Gene_Resource)val4).ValuePercent >= 0.95f)
				{
					return false;
				}
				return true;
			}
			case TargetedPrefilterKind.VisitSickPawn:
			{
				Pawn val5 = (Pawn)(object)((thing is Pawn) ? thing : null);
				if (val5 == null || worker == null)
				{
					return false;
				}
				if (!val5.IsColonist || val5.IsSlave || worker.IsSlave || worker.RaceProps == null || !worker.RaceProps.Humanlike || val5.Dead || worker == val5 || !RestUtility.InBed(val5) || !RestUtility.Awake(val5) || ForbidUtility.IsForbidden((Thing)(object)val5, worker))
				{
					return false;
				}
				if (val5.needs == null || val5.needs.joy == null || (int)val5.needs.joy.CurCategory > 1)
				{
					return false;
				}
				if (!InteractionUtility.CanReceiveInteraction(val5, (InteractionDef)null))
				{
					return false;
				}
				if (val5.needs.food != null && val5.needs.food.Starving)
				{
					return false;
				}
				if (val5.needs.rest != null && ((Need)val5.needs.rest).CurLevel <= 0.33f)
				{
					return false;
				}
				return true;
			}
			case TargetedPrefilterKind.FightFires:
			{
				Fire val = (Fire)(object)((thing is Fire) ? thing : null);
				if (val == null || worker == null || ((Thing)worker).Map == null)
				{
					return false;
				}
				if (((Thing)val).Spawned && ((Thing)val).Map == ((Thing)worker).Map)
				{
					IntVec3 position = ((Thing)val).Position;
					if (((IntVec3)(ref position)).IsValid)
					{
						Thing parent = ((AttachableThing)val).parent;
						Pawn val2 = (Pawn)(object)((parent is Pawn) ? parent : null);
						if (val2 != null)
						{
							if (val2 == worker)
							{
								return false;
							}
							Faction faction = ((Thing)worker).Faction;
							Faction hostFaction = worker.HostFaction;
							Faction faction2 = ((Thing)val2).Faction;
							Faction hostFaction2 = val2.HostFaction;
							bool flag = faction2 != null && faction2 == faction;
							if (!flag && hostFaction2 != null)
							{
								flag = hostFaction2 == faction || hostFaction2 == hostFaction;
							}
							if (!flag)
							{
								return false;
							}
							if (!((Area)((Thing)worker).Map.areaManager.Home)[((Thing)val).Position])
							{
								IntVec3 position2 = ((Thing)worker).Position;
								IntVec3 position3 = ((Thing)val2).Position;
								if (Math.Abs(position2.x - position3.x) + Math.Abs(position2.z - position3.z) > 15)
								{
									return false;
								}
							}
							return true;
						}
						if (worker.WorkTagIsDisabled((WorkTags)4096))
						{
							return false;
						}
						if (!((Area)((Thing)worker).Map.areaManager.Home)[((Thing)val).Position])
						{
							return false;
						}
						return true;
					}
				}
				return false;
			}
			default:
				return true;
			}
		}
		catch
		{
			return true;
		}
	}

	private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
	{
		actualValidatorCalls += validatorCalls;
		validatorRejected += validatorRejects;
		reachRejected += reachRejects;
		switch (route)
		{
		case RescueRoute.StaticLarge:
			staticLargeValidatorRejected += validatorRejects;
			staticLargeReachRejected += reachRejects;
			break;
		case RescueRoute.TailList:
			tailListValidatorRejected += validatorRejects;
			tailListReachRejected += reachRejects;
			break;
		default:
			customTailValidatorRejected += validatorRejects;
			customTailReachRejected += reachRejects;
			break;
		}
		if (validatorRejects >= 64 && validator != null)
		{
			heavyValidatorCalls++;
			heavyValidatorRejects += validatorRejects;
			RecordHeavyValidatorIdentity(validator, validatorRejects);
			RecordHeavyWorkGiverIdentity(validator, validatorRejects);
		}
	}

	private static void RecordHeavyValidatorIdentity(Predicate<Thing> validator, int rejects)
	{
		if (HeavyValidators.Count >= 24)
		{
			return;
		}
		try
		{
			MethodInfo method = validator.Method;
			string text = ((method == null || method.DeclaringType == null) ? "<unknown>" : method.DeclaringType.FullName);
			string text2 = ((method == null) ? "<unknown>" : method.Name);
			AddHeavyStat(HeavyValidators, text + "." + text2, rejects);
		}
		catch
		{
		}
	}

	private static void RecordHeavyWorkGiverIdentity(Predicate<Thing> validator, int rejects)
	{
		try
		{
			object target = validator.Target;
			if (target == null)
			{
				heavyWorkGiverUnresolved++;
				return;
			}
			Type type = target.GetType();
			if (!ScannerFieldCache.TryGetValue(type, out var value))
			{
				value = ResolveScannerField(type);
				ScannerFieldCache[type] = value;
			}
			WorkGiver_Scanner val = (WorkGiver_Scanner)((value == null) ? null : /*isinst with value type is only supported in some contexts*/);
			if (val == null)
			{
				heavyWorkGiverUnresolved++;
				return;
			}
			heavyWorkGiverResolved++;
			if (HeavyWorkGivers.Count < 64 || (((WorkGiver)val).def != null && HeavyWorkGivers.ContainsKey(((Def)((WorkGiver)val).def).defName)))
			{
				string key = ((((WorkGiver)val).def == null || string.IsNullOrEmpty(((Def)((WorkGiver)val).def).defName)) ? ((object)val).GetType().FullName : ((Def)((WorkGiver)val).def).defName);
				if (t8DetermineActive && rejects > t8DetermineTopRejects)
				{
					t8DetermineTopRejects = rejects;
					t8DetermineTopWorkGiver = key;
				}
				AddHeavyStat(HeavyWorkGivers, key, rejects);
			}
		}
		catch
		{
			heavyWorkGiverUnresolved++;
		}
	}

	private static FieldInfo ResolveScannerField(Type targetType)
	{
		if (targetType == null)
		{
			return null;
		}
		FieldInfo[] fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		FieldInfo fieldInfo = null;
		foreach (FieldInfo fieldInfo2 in fields)
		{
			if (typeof(WorkGiver_Scanner).IsAssignableFrom(fieldInfo2.FieldType))
			{
				return fieldInfo2;
			}
			if (fieldInfo == null && typeof(WorkGiver).IsAssignableFrom(fieldInfo2.FieldType))
			{
				fieldInfo = fieldInfo2;
			}
		}
		return fieldInfo;
	}

	internal static void T8BeginDetermineAttribution()
	{
		t8DetermineActive = true;
		t8DetermineTopWorkGiver = null;
		t8DetermineTopRejects = 0;
	}

	internal static void T8EndDetermineAttribution(long startedTicks)
	{
		if (!t8DetermineActive)
		{
			return;
		}
		t8DetermineActive = false;
		if (startedTicks <= 0)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - startedTicks;
		if (num > 0)
		{
			double num2 = (double)num * (1000.0 / (double)Stopwatch.Frequency);
			if (!(num2 < 20.0))
			{
				CarrierMechCheapNegative093T8.RecordSlowDetermine(t8DetermineTopWorkGiver, t8DetermineTopRejects, num2);
			}
		}
	}

	private static void AddHeavyStat(Dictionary<string, HeavyValidatorStats> table, string key, int rejects)
	{
		if (string.IsNullOrEmpty(key))
		{
			key = "<unknown>";
		}
		if (!table.TryGetValue(key, out var value))
		{
			value = (table[key] = new HeavyValidatorStats());
		}
		value.Calls++;
		value.Rejects += rejects;
	}

	private static Candidate[] EnsureScratch(int required, int preserveCount)
	{
		Candidate[] array = candidateScratch;
		if (array != null && array.Length >= required)
		{
			return array;
		}
		int num = ((array == null) ? 256 : Math.Max(256, array.Length));
		while (num < required && num < 65536)
		{
			num <<= 1;
		}
		if (num < required)
		{
			num = required;
		}
		Candidate[] array2 = new Candidate[num];
		if (array != null && preserveCount > 0)
		{
			Array.Copy(array, array2, Math.Min(preserveCount, array.Length));
		}
		candidateScratch = array2;
		return array2;
	}

	private static bool Failure(Exception ex)
	{
		failures++;
		if (failureLogs++ < 4)
		{
			Log.Warning("[RimMT] Unified S4 accelerated search failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
		}
		return true;
	}

	internal static string Summary()
	{
		string text = BuildTopSummary(HeavyValidators);
		string text2 = BuildTopSummary(HeavyWorkGivers);
		return "S4 slow-search: patched=" + patched + ", enabled=" + enabled + ", observed=" + observed + ", staticLargeEligible=" + staticLargeEligible + ", tailEligible=" + tailEligible + ", customTailEligible=" + customTailEligible + ", accelerated=" + accelerated + ", acceleratedNull=" + acceleratedNull + ", validatorCallsActual=" + actualValidatorCalls + ", validatorRejectedActual=" + validatorRejected + " [static=" + staticLargeValidatorRejected + ", tailList=" + tailListValidatorRejected + ", custom=" + customTailValidatorRejected + "], prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected) + ", reachRejected=" + reachRejected + " [static=" + staticLargeReachRejected + ", tailList=" + tailListReachRejected + ", custom=" + customTailReachRejected + "], heavyValidatorCalls=" + heavyValidatorCalls + ", heavyValidatorRejects=" + heavyValidatorRejects + ", heavyValidators=" + text + ", heavyWorkGivers=" + text2 + ", heavyWorkGiverResolved=" + heavyWorkGiverResolved + ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved + ", earlyKnownChecks=" + earlyKnownChecks + ", earlyKnownHits=" + earlyKnownHits + ", earlyKnownAdmissions=" + (earlyKnownListAdmissions + earlyKnownCustomAdmissions) + " [list=" + earlyKnownListAdmissions + ", custom=" + earlyKnownCustomAdmissions + "], earlyKnownPolicy=OFF, penPrefilterCalls=" + penPrefilterCalls + ", penPrefilterRejected=" + penPrefilterRejected + " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "], targetedPrefilterCalls=" + targetedPrefilterCalls + ", targetedPrefilterRejected=" + targetedPrefilterRejected + " [haulCorpses=" + targetedHaulCorpsesRejected + ", holdingPlatform=" + targetedHoldingPlatformRejected + ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected + ", fightFires=" + targetedFightFiresRejected + "], targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass + ", targetedEarly=" + (targetedEarlyListAdmissions + targetedEarlyCustomAdmissions) + " [checks=" + targetedEarlyChecks + ", hits=" + targetedEarlyHits + ", list=" + targetedEarlyListAdmissions + ", custom=" + targetedEarlyCustomAdmissions + ", authorityBypass=" + targetedEarlyAuthorityBypass + "], failures=" + failures + ", staticThreshold=" + 256 + ", tailThresholdMs=" + 32 + ", tailMinSource=" + 16 + ".";
	}

	private static string BuildTopSummary(Dictionary<string, HeavyValidatorStats> table)
	{
		if (table.Count == 0)
		{
			return "none";
		}
		string text = null;
		string text2 = null;
		string key = null;
		HeavyValidatorStats heavyValidatorStats = null;
		HeavyValidatorStats heavyValidatorStats2 = null;
		HeavyValidatorStats heavyValidatorStats3 = null;
		foreach (KeyValuePair<string, HeavyValidatorStats> item in table)
		{
			HeavyValidatorStats value = item.Value;
			if (value != null)
			{
				if (heavyValidatorStats == null || value.Rejects > heavyValidatorStats.Rejects)
				{
					key = text2;
					heavyValidatorStats3 = heavyValidatorStats2;
					text2 = text;
					heavyValidatorStats2 = heavyValidatorStats;
					text = item.Key;
					heavyValidatorStats = value;
				}
				else if (heavyValidatorStats2 == null || value.Rejects > heavyValidatorStats2.Rejects)
				{
					key = text2;
					heavyValidatorStats3 = heavyValidatorStats2;
					text2 = item.Key;
					heavyValidatorStats2 = value;
				}
				else if (heavyValidatorStats3 == null || value.Rejects > heavyValidatorStats3.Rejects)
				{
					key = item.Key;
					heavyValidatorStats3 = value;
				}
			}
		}
		string text3 = FormatHeavy(text, heavyValidatorStats);
		if (heavyValidatorStats2 != null)
		{
			text3 = text3 + "; " + FormatHeavy(text2, heavyValidatorStats2);
		}
		if (heavyValidatorStats3 != null)
		{
			text3 = text3 + "; " + FormatHeavy(key, heavyValidatorStats3);
		}
		return text3;
	}

	private static string FormatHeavy(string key, HeavyValidatorStats stats)
	{
		return (key ?? "<unknown>") + "(calls=" + stats.Calls + ", rejects=" + stats.Rejects + ")";
	}
}
