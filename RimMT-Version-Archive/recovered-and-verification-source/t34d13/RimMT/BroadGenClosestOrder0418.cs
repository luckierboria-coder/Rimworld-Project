using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class BroadGenClosestOrder0418
{
	private enum SourceKind
	{
		Thing,
		Building
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

	private const string FeatureId = "parallel.jobPartition";

	private const int MinCandidateCount = 96;

	private const int MaxCandidateCount = 8192;

	private static long observed;

	private static long runOriginalBypass;

	private static long jobGiverOwnedBypass;

	private static long shapeBypass;

	private static long nonListBypass;

	private static long smallSetBypass;

	private static long tooLargeBypass;

	private static long haulableBypass;

	private static long mobileBypass;

	private static long invalidRootMapBypass;

	private static long nullSearchSetBypass;

	private static long nullCandidateBypass;

	private static long unspawnedCandidateBypass;

	private static long wrongMapCandidateBypass;

	private static long invalidPositionBypass;

	private static long reordered;

	private static long candidatesReordered;

	private static long maxCandidates;

	private static long sortTicks;

	private static long maxSortTicks;

	private static long failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Expected O, but got Unknown
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
			if (methodBase == null)
			{
				Log.Warning("[RimMT] V0.4.18 broad GenClosest ordering unavailable: target overload not found.");
				return;
			}
			CompatibilityGuard.RegisterTarget("parallel.jobPartition", methodBase);
			HarmonyMethod val = new HarmonyMethod(typeof(BroadGenClosestOrder0418), "Prefix", (Type[])null);
			val.priority = 850;
			harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			Log.Message("[RimMT] V0.4.18 broad GenClosest ordering installed. Non-JobGiver large supported custom-global searches keep Vanilla authority but receive stable exact-distance nearest-first candidate order; JobGiver calls are delegated to the V0.4.18.1 inner Global layer.");
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			Log.Warning("[RimMT] V0.4.18 broad GenClosest ordering patch failed; Vanilla ordering remains unchanged. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void Prefix(IntVec3 root, Map map, ThingRequest thingReq, ref IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions, bool __runOriginal)
	{
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Invalid comparison between Unknown and I4
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Invalid comparison between Unknown and I4
		//IL_01d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0201: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Unknown result type (might be due to invalid IL or missing references)
		//IL_0211: Unknown result type (might be due to invalid IL or missing references)
		//IL_0219: Unknown result type (might be due to invalid IL or missing references)
		Interlocked.Increment(ref observed);
		if (!__runOriginal)
		{
			Interlocked.Increment(ref runOriginalBypass);
		}
		else if (JobGiverGlobalNearest04181.InJobGiverScope)
		{
			Interlocked.Increment(ref jobGiverOwnedBypass);
		}
		else
		{
			if (!FeatureGate.IsEnabled("parallel.jobPartition") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || RimMTRuntime.MainThreadFrames <= 1)
			{
				return;
			}
			if (map == null || map.Disposed || !((IntVec3)(ref root)).IsValid || !GenGrid.InBounds(root, map))
			{
				Interlocked.Increment(ref invalidRootMapBypass);
				return;
			}
			if (customGlobalSearchSet == null)
			{
				Interlocked.Increment(ref nullSearchSetBypass);
				return;
			}
			if (!((ThingRequest)(ref thingReq)).IsUndefined || (int)traversableRegionTypes != 14 || ignoreEntirelyForbiddenRegions || (searchRegionsMax >= 0 && !forceAllowGlobalSearch))
			{
				Interlocked.Increment(ref shapeBypass);
				return;
			}
			object obj = customGlobalSearchSet;
			IList<Thing> list = obj as IList<Thing>;
			IList<Building> list2 = obj as IList<Building>;
			int count;
			SourceKind sourceKind;
			if (list != null)
			{
				count = list.Count;
				sourceKind = SourceKind.Thing;
			}
			else
			{
				if (list2 == null)
				{
					Interlocked.Increment(ref nonListBypass);
					return;
				}
				count = list2.Count;
				sourceKind = SourceKind.Building;
			}
			if (count < 96)
			{
				Interlocked.Increment(ref smallSetBypass);
				return;
			}
			if (count <= 8192)
			{
				try
				{
					List<Thing> list3 = ((map.listerHaulables == null) ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling());
					if (list3 != null && obj == list3)
					{
						Interlocked.Increment(ref haulableBypass);
					}
					else
					{
						Candidate[] array = new Candidate[count];
						for (int i = 0; i < count; i++)
						{
							Thing val = (Thing)((sourceKind == SourceKind.Thing) ? ((object)list[i]) : ((object)list2[i]));
							if (val == null)
							{
								Interlocked.Increment(ref nullCandidateBypass);
								return;
							}
							if (val is Pawn)
							{
								Interlocked.Increment(ref mobileBypass);
								return;
							}
							if (!val.Spawned)
							{
								Interlocked.Increment(ref unspawnedCandidateBypass);
								return;
							}
							if (val.MapHeld != map)
							{
								Interlocked.Increment(ref wrongMapCandidateBypass);
								return;
							}
							IntVec3 position = val.Position;
							if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, map))
							{
								Interlocked.Increment(ref invalidPositionBypass);
								return;
							}
							long num = (long)position.x - (long)root.x;
							long num2 = (long)position.z - (long)root.z;
							long distanceSquared = num * num + num2 * num2;
							array[i] = new Candidate(val, distanceSquared, i);
						}
						long timestamp = Stopwatch.GetTimestamp();
						Array.Sort(array, CandidateComparer.Instance);
						long value = Stopwatch.GetTimestamp() - timestamp;
						Interlocked.Add(ref sortTicks, value);
						UpdateMax(ref maxSortTicks, value);
						Thing[] array2 = (Thing[])(object)new Thing[count];
						for (int j = 0; j < count; j++)
						{
							array2[j] = array[j].Thing;
						}
						customGlobalSearchSet = array2;
						Interlocked.Increment(ref reordered);
						Interlocked.Add(ref candidatesReordered, count);
						UpdateMax(ref maxCandidates, count);
					}
					return;
				}
				catch (Exception ex)
				{
					Interlocked.Increment(ref failures);
					CircuitBreaker.RecordFailure("parallel.jobPartition", ex);
					Log.Warning("[RimMT] V0.4.18 broad GenClosest ordering failed for one call; Vanilla keeps the original search semantics. " + ex.GetType().Name + ": " + ex.Message);
					return;
				}
			}
			Interlocked.Increment(ref tooLargeBypass);
		}
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
		long num = Interlocked.Read(ref reordered);
		long num2 = Interlocked.Read(ref candidatesReordered);
		double num3 = ((num == 0L) ? 0.0 : ((double)num2 / (double)num));
		double num4 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref sortTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num5 = (double)Interlocked.Read(ref maxSortTicks) * 1000000.0 / (double)Stopwatch.Frequency;
		return "Broad GenClosest ordering V0.4.18: observed=" + Interlocked.Read(ref observed) + ", reordered=" + num + ", runOriginalBypass=" + Interlocked.Read(ref runOriginalBypass) + ", jobGiverOwnedBypass=" + Interlocked.Read(ref jobGiverOwnedBypass) + ", shapeBypass=" + Interlocked.Read(ref shapeBypass) + ", nonListBypass=" + Interlocked.Read(ref nonListBypass) + ", smallSetBypass=" + Interlocked.Read(ref smallSetBypass) + ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) + ", haulableBypass=" + Interlocked.Read(ref haulableBypass) + ", mobileBypass=" + Interlocked.Read(ref mobileBypass) + ", invalidRootMapBypass=" + Interlocked.Read(ref invalidRootMapBypass) + ", nullSearchSetBypass=" + Interlocked.Read(ref nullSearchSetBypass) + ", nullCandidateBypass=" + Interlocked.Read(ref nullCandidateBypass) + ", unspawnedCandidateBypass=" + Interlocked.Read(ref unspawnedCandidateBypass) + ", wrongMapCandidateBypass=" + Interlocked.Read(ref wrongMapCandidateBypass) + ", invalidPositionBypass=" + Interlocked.Read(ref invalidPositionBypass) + ", candidatesReordered=" + num2 + ", avgCandidates=" + num3.ToString("F1") + ", maxCandidates=" + Interlocked.Read(ref maxCandidates) + ", avgSortUs=" + num4.ToString("F2") + ", maxSortUs=" + num5.ToString("F2") + ", failures=" + Interlocked.Read(ref failures) + ". Exact candidate membership is preserved; Vanilla validator/Reachability/final selection remains authoritative.";
	}
}
