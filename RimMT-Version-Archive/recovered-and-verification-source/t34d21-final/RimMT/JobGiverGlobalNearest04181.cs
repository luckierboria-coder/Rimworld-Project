using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class JobGiverGlobalNearest04181
{
	private sealed class PackageContext
	{
		internal readonly Pawn Pawn;

		internal readonly Dictionary<PlanKey, SearchPlan> Plans = new Dictionary<PlanKey, SearchPlan>();

		internal PackageContext(Pawn pawn)
		{
			Pawn = pawn;
		}
	}

	private sealed class SearchPlan
	{
		internal readonly Thing[] Members;

		internal readonly bool[] Spawned;

		internal readonly IntVec3[] Positions;

		internal readonly Candidate[] Ordered;

		internal readonly Dictionary<float, Thing[]> Prefixes = new Dictionary<float, Thing[]>();

		internal SearchPlan(Thing[] members, bool[] spawned, IntVec3[] positions, Candidate[] ordered)
		{
			Members = members;
			Spawned = spawned;
			Positions = positions;
			Ordered = ordered;
		}
	}

	private struct PlanKey : IEquatable<PlanKey>
	{
		internal readonly object Source;

		internal readonly int X;

		internal readonly int Z;

		internal PlanKey(object source, int x, int z)
		{
			Source = source;
			X = x;
			Z = z;
		}

		public bool Equals(PlanKey other)
		{
			if (Source == other.Source && X == other.X)
			{
				return Z == other.Z;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is PlanKey)
			{
				return Equals((PlanKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (((RuntimeHelpers.GetHashCode(Source) * 397) ^ X) * 397) ^ Z;
		}
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

	private const int MinSourceCount = 64;

	private const int MaxSourceCount = 16384;

	private const int MaxPlansPerPackage = 64;

	private const int MaxPrefixesPerPlan = 16;

	[ThreadStatic]
	private static int jobGiverDepth;

	[ThreadStatic]
	private static long jobGiverStartTicks;

	[ThreadStatic]
	private static PackageContext current;

	internal static bool InJobGiverScope => jobGiverDepth > 0;

	internal static long CurrentScopeStartTicks
	{
		get
		{
			if (jobGiverDepth <= 0)
			{
				return 0L;
			}
			return jobGiverStartTicks;
		}
	}

	internal static void Apply(Harmony harmony)
	{
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Expected O, but got Unknown
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			if (!JobSearchPackageContext093T28.Installed)
			{
				return;
			}
			bool flag = false;
			bool flag2 = false;
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (!(methodInfo == null))
				{
					ParameterInfo[] parameters = methodInfo.GetParameters();
					if (methodInfo.Name == "ClosestThing_Global" && parameters.Length == 5 && parameters[0].ParameterType == typeof(IntVec3))
					{
						harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(JobGiverGlobalNearest04181), "GlobalPrefix", (Type[])null)
						{
							priority = 875
						}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
						flag = true;
					}
					else if (methodInfo.Name == "ClosestThing_Global_Reachable" && parameters.Length == 8 && parameters[0].ParameterType == typeof(IntVec3))
					{
						harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(JobGiverGlobalNearest04181), "GlobalReachablePrefix", (Type[])null)
						{
							priority = 875
						}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
						flag2 = true;
					}
				}
			}
			Log.Message("[RimMT] T28-coordinated nearest-first + JS2 package-local search-plan reuse active: global=" + flag + ", reachable=" + flag2 + ".");
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] Unified nearest-first install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void JobGiverPrefix(Pawn __0)
	{
		if (jobGiverDepth == 0)
		{
			jobGiverStartTicks = Stopwatch.GetTimestamp();
			current = new PackageContext(__0);
		}
		jobGiverDepth++;
	}

	public static Exception JobGiverFinalizer(Exception __exception)
	{
		if (jobGiverDepth > 0)
		{
			jobGiverDepth--;
		}
		if (jobGiverDepth == 0)
		{
			jobGiverStartTicks = 0L;
			current = null;
		}
		return __exception;
	}

	public static void GlobalPrefix(object[] __args)
	{
		if (__args != null && __args.Length >= 5)
		{
			TryReorder(__args, 0, 1, 2, 4);
		}
	}

	public static void GlobalReachablePrefix(object[] __args)
	{
		if (__args != null && __args.Length >= 8)
		{
			TryReorder(__args, 0, 2, 5, 7);
		}
	}

	private static void TryReorder(object[] args, int centerIndex, int setIndex, int maxDistanceIndex, int priorityIndex)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Invalid comparison between Unknown and I4
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		if (!InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || args[priorityIndex] != null || !(args[setIndex] is IList list))
		{
			return;
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
		if (count < 64 || count > 16384)
		{
			return;
		}
		IntVec3 val;
		float num;
		try
		{
			val = (IntVec3)args[centerIndex];
			num = Convert.ToSingle(args[maxDistanceIndex]);
		}
		catch
		{
			return;
		}
		if (float.IsNaN(num) || num < 0f)
		{
			return;
		}
		PackageContext packageContext = current;
		if (packageContext == null)
		{
			return;
		}
		PlanKey key = new PlanKey(list, val.x, val.z);
		if (packageContext.Plans.TryGetValue(key, out var value) && !ValidatePlan(list, value))
		{
			packageContext.Plans.Remove(key);
			value = null;
		}
		if (value == null)
		{
			if (packageContext.Plans.Count >= 64)
			{
				return;
			}
			value = BuildPlan(list, val, count);
			if (value == null)
			{
				return;
			}
			packageContext.Plans[key] = value;
		}
		if (!value.Prefixes.TryGetValue(num, out var value2))
		{
			value2 = BuildPrefix(value, num);
			if (value.Prefixes.Count < 16)
			{
				value.Prefixes[num] = value2;
			}
		}
		args[setIndex] = value2;
	}

	private static SearchPlan BuildPlan(IList source, IntVec3 center, int count)
	{
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Thing[] array = (Thing[])(object)new Thing[count];
			bool[] array2 = new bool[count];
			IntVec3[] array3 = (IntVec3[])(object)new IntVec3[count];
			Candidate[] array4 = new Candidate[count];
			int num = 0;
			for (int i = 0; i < count; i++)
			{
				object obj = source[i];
				Thing val = (Thing)((obj is Thing) ? obj : null);
				if (val == null)
				{
					return null;
				}
				array[i] = val;
				array2[i] = val.Spawned;
				array3[i] = val.Position;
				if (array2[i] && ((IntVec3)(ref array3[i])).IsValid)
				{
					long num2 = (long)array3[i].x - (long)center.x;
					long num3 = (long)array3[i].z - (long)center.z;
					array4[num++] = new Candidate(val, num2 * num2 + num3 * num3, i);
				}
			}
			if (num > 1)
			{
				Array.Sort(array4, 0, num, CandidateComparer.Instance);
			}
			Candidate[] array5 = new Candidate[num];
			Array.Copy(array4, array5, num);
			return new SearchPlan(array, array2, array3, array5);
		}
		catch
		{
			return null;
		}
	}

	private static bool ValidatePlan(IList source, SearchPlan plan)
	{
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		if (source == null || plan == null)
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
		if (count != plan.Members.Length)
		{
			return false;
		}
		for (int i = 0; i < count; i++)
		{
			object obj2 = source[i];
			Thing val = (Thing)((obj2 is Thing) ? obj2 : null);
			if (val != plan.Members[i] || val == null)
			{
				return false;
			}
			bool spawned = val.Spawned;
			if (spawned != plan.Spawned[i])
			{
				return false;
			}
			if (spawned && val.Position != plan.Positions[i])
			{
				return false;
			}
		}
		return true;
	}

	private static Thing[] BuildPrefix(SearchPlan plan, float maxDistance)
	{
		double num = (double)maxDistance * (double)maxDistance;
		Candidate[] ordered = plan.Ordered;
		int i;
		for (i = 0; i < ordered.Length && (double)ordered[i].DistanceSquared <= num; i++)
		{
		}
		Thing[] array = (Thing[])(object)new Thing[i];
		for (int j = 0; j < i; j++)
		{
			array[j] = ordered[j].Thing;
		}
		return array;
	}
}
