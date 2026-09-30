using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class MobileSourceRescue093T18
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

	internal const string FeatureId = "ai.mobileSourceRescue";

	private const int MinSourceCount = 96;

	private const int MaxSourceCount = 1024;

	[ThreadStatic]
	private static Candidate[] scratch;

	private static volatile bool installed;

	private static volatile bool globalInstalled;

	private static volatile bool enabled = true;

	private static volatile bool authoritySafe = true;

	private static volatile bool globalAuthoritySafe = true;

	private static MethodBase target;

	private static MethodBase globalTarget;

	private static long observed;

	private static long eligible;

	private static long accelerated;

	private static long acceleratedNull;

	private static long candidatesSeen;

	private static long candidatesWithinDistance;

	private static long validatorRejected;

	private static long reachRejected;

	private static long shapeBypass;

	private static long sizeBypass;

	private static long nonMobileBypass;

	private static long invalidMemberBypass;

	private static long foreignPatchBypass;

	private static long sortTicks;

	private static long maxSortTicks;

	private static long maxSourceCount;

	private static long globalObserved;

	private static long globalEligible;

	private static long globalAccelerated;

	private static long globalAcceleratedNull;

	private static long globalCandidatesSeen;

	private static long globalCandidatesWithinDistance;

	private static long globalValidatorRejected;

	private static long globalShapeBypass;

	private static long globalSizeBypass;

	private static long globalNonMobileBypass;

	private static long globalInvalidMemberBypass;

	private static long globalForeignPatchBypass;

	private static long globalPriorityBypass;

	private static long globalSortTicks;

	private static long globalMaxSortTicks;

	private static long globalMaxSourceCount;

	private static long failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Expected O, but got Unknown
		//IL_01cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e7: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			target = AccessTools.Method(typeof(GenClosest), "ClosestThingReachable", new Type[13]
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
			if (target != null)
			{
				authoritySafe = !HasForeignPatches(target);
				harmony.Patch(target, new HarmonyMethod(typeof(MobileSourceRescue093T18), "Prefix", (Type[])null)
				{
					priority = 1050
				}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				installed = true;
			}
			else
			{
				Log.Warning("[RimMT] T18 mobile-source rescue unavailable: exact ClosestThingReachable overload not found.");
			}
			globalTarget = AccessTools.Method(typeof(GenClosest), "ClosestThing_Global", new Type[5]
			{
				typeof(IntVec3),
				typeof(IEnumerable),
				typeof(float),
				typeof(Predicate<Thing>),
				typeof(Func<Thing, float>)
			}, (Type[])null);
			if (globalTarget != null)
			{
				globalAuthoritySafe = !HasForeignPatches(globalTarget);
				harmony.Patch(globalTarget, new HarmonyMethod(typeof(MobileSourceRescue093T18), "GlobalPrefix", (Type[])null)
				{
					priority = 1040
				}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				globalInstalled = true;
			}
			else
			{
				Log.Warning("[RimMT] T19 mobile-global rescue unavailable: exact ClosestThing_Global overload not found.");
			}
			Log.Message("[RimMT] T18/T19 mobile-source rescue installed. Reachable path=" + installed + " authoritySafe=" + authoritySafe + "; Global path=" + globalInstalled + " authoritySafe=" + globalAuthoritySafe + ". Scope is JobGiver large Pawn-containing IList sources; original validator and live Reachability remain authoritative.");
		}
		catch (Exception ex)
		{
			installed = false;
			globalInstalled = false;
			failures++;
			Log.Warning("[RimMT] T18/T19 mobile-source rescue install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void SetEnabled(bool value)
	{
		enabled = value;
	}

	public static bool Prefix(object[] __args, ref Thing __result)
	{
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Invalid comparison between Unknown and I4
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_018c: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_0195: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Invalid comparison between Unknown and I4
		//IL_019e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a1: Invalid comparison between Unknown and I4
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Invalid comparison between Unknown and I4
		//IL_0250: Unknown result type (might be due to invalid IL or missing references)
		//IL_0255: Unknown result type (might be due to invalid IL or missing references)
		//IL_0260: Unknown result type (might be due to invalid IL or missing references)
		//IL_0392: Unknown result type (might be due to invalid IL or missing references)
		//IL_0395: Unknown result type (might be due to invalid IL or missing references)
		//IL_039a: Unknown result type (might be due to invalid IL or missing references)
		//IL_039b: Unknown result type (might be due to invalid IL or missing references)
		//IL_028b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0293: Unknown result type (might be due to invalid IL or missing references)
		//IL_029b: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a3: Unknown result type (might be due to invalid IL or missing references)
		observed++;
		if (!enabled || !installed || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		if (!authoritySafe)
		{
			foreignPatchBypass++;
			return true;
		}
		if (__args == null || __args.Length < 13)
		{
			shapeBypass++;
			return true;
		}
		IntVec3 val;
		Map val2;
		ThingRequest val3;
		PathEndMode val4;
		TraverseParms val5;
		float num;
		Predicate<Thing> predicate;
		IList list;
		int num2;
		int num3;
		bool flag;
		RegionType val6;
		bool flag2;
		try
		{
			val = (IntVec3)__args[0];
			object obj = __args[1];
			val2 = (Map)((obj is Map) ? obj : null);
			val3 = (ThingRequest)__args[2];
			val4 = (PathEndMode)__args[3];
			val5 = (TraverseParms)__args[4];
			num = Convert.ToSingle(__args[5]);
			predicate = __args[6] as Predicate<Thing>;
			list = __args[7] as IList;
			num2 = Convert.ToInt32(__args[8]);
			num3 = Convert.ToInt32(__args[9]);
			flag = Convert.ToBoolean(__args[10]);
			val6 = (RegionType)__args[11];
			flag2 = Convert.ToBoolean(__args[12]);
		}
		catch
		{
			shapeBypass++;
			return true;
		}
		Pawn pawn = val5.pawn;
		if (val2 == null || val2.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != val2 || !((IntVec3)(ref val)).IsValid || !GenGrid.InBounds(val, val2) || num <= 0f || float.IsNaN(num) || !((ThingRequest)(ref val3)).IsUndefined || list == null || num2 != 0 || (num3 >= 0 && !flag) || (int)val6 != 14 || flag2)
		{
			shapeBypass++;
			return true;
		}
		TraverseMode mode = val5.mode;
		if ((int)mode != 0 && (int)mode != 1 && (int)mode != 2)
		{
			shapeBypass++;
			return true;
		}
		int count;
		try
		{
			count = list.Count;
		}
		catch
		{
			shapeBypass++;
			return true;
		}
		if (count < 96 || count > 1024)
		{
			sizeBypass++;
			return true;
		}
		Candidate[] array = EnsureScratch(count);
		int num4 = 0;
		bool flag3 = false;
		double num5 = (double)num * (double)num;
		try
		{
			for (int i = 0; i < count; i++)
			{
				object obj4 = list[i];
				Thing val7 = (Thing)((obj4 is Thing) ? obj4 : null);
				if (val7 == null || !val7.Spawned || val7.Map != val2)
				{
					invalidMemberBypass++;
					return true;
				}
				IntVec3 position = val7.Position;
				if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, val2))
				{
					invalidMemberBypass++;
					return true;
				}
				if (val7 is Pawn)
				{
					flag3 = true;
				}
				long num6 = (long)position.x - (long)val.x;
				long num7 = (long)position.z - (long)val.z;
				long num8 = num6 * num6 + num7 * num7;
				if ((double)num8 <= num5)
				{
					array[num4++] = new Candidate(val7, num8, i);
				}
			}
		}
		catch
		{
			invalidMemberBypass++;
			return true;
		}
		if (!flag3)
		{
			nonMobileBypass++;
			return true;
		}
		eligible++;
		candidatesSeen += count;
		candidatesWithinDistance += num4;
		UpdateMax(ref maxSourceCount, count);
		SortCandidates(array, num4, ref sortTicks, ref maxSortTicks);
		int num9 = 0;
		int num10 = 0;
		for (int j = 0; j < num4; j++)
		{
			Thing thing = array[j].Thing;
			if (predicate != null && !predicate(thing))
			{
				num9++;
				continue;
			}
			if (!val2.reachability.CanReach(val, new LocalTargetInfo(thing), val4, val5))
			{
				num10++;
				continue;
			}
			validatorRejected += num9;
			reachRejected += num10;
			__result = thing;
			accelerated++;
			return false;
		}
		validatorRejected += num9;
		reachRejected += num10;
		__result = null;
		accelerated++;
		acceleratedNull++;
		return false;
	}

	public static bool GlobalPrefix(object[] __args, ref Thing __result)
	{
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Invalid comparison between Unknown and I4
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
		globalObserved++;
		if (!enabled || !globalInstalled || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		if (!globalAuthoritySafe)
		{
			globalForeignPatchBypass++;
			return true;
		}
		if (__args == null || __args.Length < 5)
		{
			globalShapeBypass++;
			return true;
		}
		IntVec3 val;
		IList list;
		float num;
		Predicate<Thing> predicate;
		Func<Thing, float> func;
		try
		{
			val = (IntVec3)__args[0];
			list = __args[1] as IList;
			num = Convert.ToSingle(__args[2]);
			predicate = __args[3] as Predicate<Thing>;
			func = __args[4] as Func<Thing, float>;
		}
		catch
		{
			globalShapeBypass++;
			return true;
		}
		if (list == null || !((IntVec3)(ref val)).IsValid || num <= 0f || float.IsNaN(num))
		{
			globalShapeBypass++;
			return true;
		}
		if (func != null)
		{
			globalPriorityBypass++;
			return true;
		}
		int count;
		try
		{
			count = list.Count;
		}
		catch
		{
			globalShapeBypass++;
			return true;
		}
		if (count < 96 || count > 1024)
		{
			globalSizeBypass++;
			return true;
		}
		Candidate[] array = EnsureScratch(count);
		int num2 = 0;
		bool flag = false;
		double num3 = (double)num * (double)num;
		try
		{
			for (int i = 0; i < count; i++)
			{
				object obj3 = list[i];
				Thing val2 = (Thing)((obj3 is Thing) ? obj3 : null);
				if (val2 == null || !val2.Spawned)
				{
					globalInvalidMemberBypass++;
					return true;
				}
				IntVec3 positionHeld = val2.PositionHeld;
				if (!((IntVec3)(ref positionHeld)).IsValid)
				{
					globalInvalidMemberBypass++;
					return true;
				}
				if (val2 is Pawn)
				{
					flag = true;
				}
				long num4 = (long)positionHeld.x - (long)val.x;
				long num5 = (long)positionHeld.z - (long)val.z;
				long num6 = num4 * num4 + num5 * num5;
				if ((double)num6 <= num3)
				{
					array[num2++] = new Candidate(val2, num6, i);
				}
			}
		}
		catch
		{
			globalInvalidMemberBypass++;
			return true;
		}
		if (!flag)
		{
			globalNonMobileBypass++;
			return true;
		}
		globalEligible++;
		globalCandidatesSeen += count;
		globalCandidatesWithinDistance += num2;
		UpdateMax(ref globalMaxSourceCount, count);
		SortCandidates(array, num2, ref globalSortTicks, ref globalMaxSortTicks);
		int num7 = 0;
		for (int j = 0; j < num2; j++)
		{
			Thing thing = array[j].Thing;
			if (predicate != null && !predicate(thing))
			{
				num7++;
				continue;
			}
			globalValidatorRejected += num7;
			__result = thing;
			globalAccelerated++;
			return false;
		}
		globalValidatorRejected += num7;
		__result = null;
		globalAccelerated++;
		globalAcceleratedNull++;
		return false;
	}

	private static Candidate[] EnsureScratch(int count)
	{
		Candidate[] array = scratch;
		if (array == null || array.Length < count)
		{
			int num;
			for (num = 128; num < count; num <<= 1)
			{
			}
			array = (scratch = new Candidate[num]);
		}
		return array;
	}

	private static void SortCandidates(Candidate[] candidates, int count, ref long totalTicks, ref long maxTicks)
	{
		if (count > 1)
		{
			long timestamp = Stopwatch.GetTimestamp();
			Array.Sort(candidates, 0, count, CandidateComparer.Instance);
			long value = Stopwatch.GetTimestamp() - timestamp;
			Interlocked.Add(ref totalTicks, value);
			UpdateMax(ref maxTicks, value);
		}
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

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref eligible);
		double num2 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref candidatesSeen) / (double)num));
		double num3 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref candidatesWithinDistance) / (double)num));
		double num4 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref sortTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num5 = (double)Interlocked.Read(ref maxSortTicks) * 1000000.0 / (double)Stopwatch.Frequency;
		long num6 = Interlocked.Read(ref globalEligible);
		double num7 = ((num6 == 0L) ? 0.0 : ((double)Interlocked.Read(ref globalCandidatesSeen) / (double)num6));
		double num8 = ((num6 == 0L) ? 0.0 : ((double)Interlocked.Read(ref globalCandidatesWithinDistance) / (double)num6));
		double num9 = ((num6 == 0L) ? 0.0 : ((double)Interlocked.Read(ref globalSortTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num6));
		double num10 = (double)Interlocked.Read(ref globalMaxSortTicks) * 1000000.0 / (double)Stopwatch.Frequency;
		return "T18 mobile-source rescue: installed=" + installed + ", enabled=" + enabled + ", authoritySafe=" + authoritySafe + ", observed=" + Interlocked.Read(ref observed) + ", eligible=" + num + ", accelerated=" + Interlocked.Read(ref accelerated) + ", acceleratedNull=" + Interlocked.Read(ref acceleratedNull) + ", avgSourceCount=" + num2.ToString("F1") + ", avgWithinDistance=" + num3.ToString("F1") + ", maxSourceCount=" + Interlocked.Read(ref maxSourceCount) + ", validatorRejected=" + Interlocked.Read(ref validatorRejected) + ", reachRejected=" + Interlocked.Read(ref reachRejected) + ", shapeBypass=" + Interlocked.Read(ref shapeBypass) + ", sizeBypass=" + Interlocked.Read(ref sizeBypass) + ", nonMobileBypass=" + Interlocked.Read(ref nonMobileBypass) + ", invalidMemberBypass=" + Interlocked.Read(ref invalidMemberBypass) + ", foreignPatchBypass=" + Interlocked.Read(ref foreignPatchBypass) + ", avgSortUs=" + num4.ToString("F2") + ", maxSortUs=" + num5.ToString("F2") + ", failures=" + Interlocked.Read(ref failures) + ". Scope=JobGiver_Work + exact 13-arg ClosestThingReachable + custom IList[96..1024] containing Pawn; stable distance/source-order; original validator and live CanReach remain final authority." + Environment.NewLine + "T19 mobile-global rescue: installed=" + globalInstalled + ", enabled=" + enabled + ", authoritySafe=" + globalAuthoritySafe + ", observed=" + Interlocked.Read(ref globalObserved) + ", eligible=" + num6 + ", accelerated=" + Interlocked.Read(ref globalAccelerated) + ", acceleratedNull=" + Interlocked.Read(ref globalAcceleratedNull) + ", avgSourceCount=" + num7.ToString("F1") + ", avgWithinDistance=" + num8.ToString("F1") + ", maxSourceCount=" + Interlocked.Read(ref globalMaxSourceCount) + ", validatorRejected=" + Interlocked.Read(ref globalValidatorRejected) + ", shapeBypass=" + Interlocked.Read(ref globalShapeBypass) + ", sizeBypass=" + Interlocked.Read(ref globalSizeBypass) + ", nonMobileBypass=" + Interlocked.Read(ref globalNonMobileBypass) + ", invalidMemberBypass=" + Interlocked.Read(ref globalInvalidMemberBypass) + ", priorityBypass=" + Interlocked.Read(ref globalPriorityBypass) + ", foreignPatchBypass=" + Interlocked.Read(ref globalForeignPatchBypass) + ", avgSortUs=" + num9.ToString("F2") + ", maxSortUs=" + num10.ToString("F2") + ". Scope=JobGiver_Work + exact 5-arg ClosestThing_Global + IList[96..1024] containing Pawn + priorityGetter=null + all members spawned; unsupported haul/inventory semantics fail open.";
	}
}
