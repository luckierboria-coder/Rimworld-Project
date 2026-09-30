using System;
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

internal static class JobSearchPackageContext093T28
{
	internal struct ScopeState
	{
		internal bool Entered;

		internal bool Outermost;

		internal bool GlobalNearestEntered;

		internal PackageContext Shared;

		internal JobSearchTransaction093T20.PackageState T20;

		internal GenClosestTransactionIndex093T22.PackageState T22;
	}

	internal sealed class PackageContext
	{
		internal readonly Pawn Pawn;

		internal readonly Map Map;

		internal readonly IntVec3 StartPosition;

		internal readonly long Generation;

		internal readonly long StartedTimestamp;

		internal readonly Dictionary<object, object> ModuleStates = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);

		internal readonly HashSet<BillStack> InactiveBillStacks = new HashSet<BillStack>();

		internal readonly Dictionary<object, HashSet<object>> GenericNegatives = new Dictionary<object, HashSet<object>>(ReferenceEqualityComparer.Instance);

		internal PackageContext(Pawn pawn, long generation, long startedTimestamp)
		{
			//IL_0056: Unknown result type (might be due to invalid IL or missing references)
			//IL_004f: Unknown result type (might be due to invalid IL or missing references)
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			Pawn = pawn;
			Map = ((pawn == null) ? null : ((Thing)pawn).Map);
			StartPosition = ((pawn == null) ? IntVec3.Invalid : ((Thing)pawn).Position);
			Generation = generation;
			StartedTimestamp = startedTimestamp;
		}
	}

	private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
	{
		internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

		public new bool Equals(object x, object y)
		{
			return x == y;
		}

		public int GetHashCode(object obj)
		{
			if (obj != null)
			{
				return RuntimeHelpers.GetHashCode(obj);
			}
			return 0;
		}
	}

	internal const string FeatureId = "ai.jobSearchPackageContext";

	private const int MaxModuleStates = 24;

	private const int MaxGenericNegativeDomains = 16;

	private const int MaxGenericNegativesPerDomain = 4096;

	[ThreadStatic]
	private static int depth;

	[ThreadStatic]
	private static PackageContext current;

	private static bool installed;

	private static int installFailures;

	private static long nextGeneration;

	private static long packages;

	private static long nestedPackages;

	private static long maxDepth;

	private static long mismatchedExits;

	private static long moduleStateCreates;

	private static long moduleStateCapacityBypass;

	private static long billNegativeHits;

	private static long billNegativeStores;

	private static long genericNegativeHits;

	private static long genericNegativeStores;

	private static long genericNegativeCapacityBypass;

	internal static bool Installed => installed;

	internal static bool InScope
	{
		get
		{
			if (depth > 0)
			{
				return current != null;
			}
			return false;
		}
	}

	internal static Pawn CurrentPawn
	{
		get
		{
			if (!InScope)
			{
				return null;
			}
			return current.Pawn;
		}
	}

	internal static Map CurrentMap
	{
		get
		{
			if (!InScope)
			{
				return null;
			}
			return current.Map;
		}
	}

	internal static long CurrentGeneration
	{
		get
		{
			if (!InScope)
			{
				return 0L;
			}
			return current.Generation;
		}
	}

	internal static long CurrentScopeStartTicks
	{
		get
		{
			if (!InScope)
			{
				return 0L;
			}
			return current.StartedTimestamp;
		}
	}

	internal static long PackageCount => Interlocked.Read(ref packages);

	internal static void Apply(Harmony harmony)
	{
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Expected O, but got Unknown
		//IL_009c: Expected O, but got Unknown
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
				Log.Warning("[RimMT] T28 unified Job Search package boundary unavailable; legacy modules stay fail-closed.");
				return;
			}
			harmony.Patch(methodBase, new HarmonyMethod(typeof(JobSearchPackageContext093T28), "PackagePrefix", (Type[])null)
			{
				priority = 1200
			}, (HarmonyMethod)null, (HarmonyMethod)null, new HarmonyMethod(typeof(JobSearchPackageContext093T28), "PackageFinalizer", (Type[])null)
			{
				priority = -400
			});
			installed = true;
			Log.Message("[RimMT] T28 unified Job Search transaction boundary installed. One Harmony package wrapper now coordinates T20/T21, T22, GlobalNearest and shared false-only package state.");
		}
		catch (Exception ex)
		{
			installFailures++;
			installed = false;
			Log.Warning("[RimMT] T28 unified Job Search boundary failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PackagePrefix(Pawn __0, ref ScopeState __state)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Invalid comparison between Unknown and I4
		__state = default(ScopeState);
		if (RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			__state.Entered = true;
			__state.Outermost = depth == 0;
			depth++;
			UpdateMax(ref maxDepth, depth);
			if (__state.Outermost)
			{
				long generation = Interlocked.Increment(ref nextGeneration);
				current = new PackageContext(__0, generation, Stopwatch.GetTimestamp());
				__state.Shared = current;
				Interlocked.Increment(ref packages);
				ReservationTransaction093T32A.BeginPackage(__0, generation);
			}
			else
			{
				__state.Shared = current;
				Interlocked.Increment(ref nestedPackages);
			}
			JobSearchTransaction093T20.PackagePrefix(__0, ref __state.T20);
			GenClosestTransactionIndex093T22.PackagePrefix(ref __state.T22);
			JobGiverGlobalNearest04181.JobGiverPrefix(__0);
			__state.GlobalNearestEntered = true;
		}
	}

	public static Exception PackageFinalizer(Exception __exception, ScopeState __state)
	{
		if (!__state.Entered)
		{
			return __exception;
		}
		if (__state.GlobalNearestEntered)
		{
			__exception = JobGiverGlobalNearest04181.JobGiverFinalizer(__exception);
		}
		__exception = GenClosestTransactionIndex093T22.PackageFinalizer(__exception, __state.T22);
		__exception = JobSearchTransaction093T20.PackageFinalizer(__exception, __state.T20);
		if (depth > 0)
		{
			depth--;
		}
		if (__state.Outermost)
		{
			if (current != __state.Shared)
			{
				Interlocked.Increment(ref mismatchedExits);
			}
			ReservationTransaction093T32A.EndPackage();
			current = null;
			depth = 0;
		}
		return __exception;
	}

	internal static T GetOrCreateModuleState<T>(object key, Func<T> factory) where T : class
	{
		PackageContext packageContext = current;
		if (packageContext == null || depth <= 0 || key == null || factory == null)
		{
			return null;
		}
		if (packageContext.ModuleStates.TryGetValue(key, out var value))
		{
			return value as T;
		}
		if (packageContext.ModuleStates.Count >= 24)
		{
			Interlocked.Increment(ref moduleStateCapacityBypass);
			return null;
		}
		T val = factory();
		if (val == null)
		{
			return null;
		}
		packageContext.ModuleStates[key] = val;
		Interlocked.Increment(ref moduleStateCreates);
		return val;
	}

	internal static T GetModuleState<T>(object key) where T : class
	{
		PackageContext packageContext = current;
		if (packageContext == null || depth <= 0 || key == null)
		{
			return null;
		}
		if (!packageContext.ModuleStates.TryGetValue(key, out var value))
		{
			return null;
		}
		return value as T;
	}

	internal static bool IsBillStackKnownInactive(BillStack stack)
	{
		PackageContext packageContext = current;
		if (packageContext == null || depth <= 0 || stack == null)
		{
			return false;
		}
		if (!packageContext.InactiveBillStacks.Contains(stack))
		{
			return false;
		}
		Interlocked.Increment(ref billNegativeHits);
		return true;
	}

	internal static void MarkBillStackInactive(BillStack stack)
	{
		PackageContext packageContext = current;
		if (packageContext != null && depth > 0 && stack != null && packageContext.InactiveBillStacks.Add(stack))
		{
			Interlocked.Increment(ref billNegativeStores);
		}
	}

	internal static bool TryGetNegative(object domain, object key)
	{
		PackageContext packageContext = current;
		if (packageContext == null || depth <= 0 || domain == null || key == null)
		{
			return false;
		}
		if (!packageContext.GenericNegatives.TryGetValue(domain, out var value) || value == null)
		{
			return false;
		}
		if (!value.Contains(key))
		{
			return false;
		}
		Interlocked.Increment(ref genericNegativeHits);
		return true;
	}

	internal static void StoreNegative(object domain, object key)
	{
		PackageContext packageContext = current;
		if (packageContext == null || depth <= 0 || domain == null || key == null)
		{
			return;
		}
		if (!packageContext.GenericNegatives.TryGetValue(domain, out var value))
		{
			if (packageContext.GenericNegatives.Count >= 16)
			{
				Interlocked.Increment(ref genericNegativeCapacityBypass);
				return;
			}
			value = new HashSet<object>(ReferenceEqualityComparer.Instance);
			packageContext.GenericNegatives.Add(domain, value);
		}
		if (value.Count >= 4096)
		{
			Interlocked.Increment(ref genericNegativeCapacityBypass);
		}
		else if (value.Add(key))
		{
			Interlocked.Increment(ref genericNegativeStores);
		}
	}

	internal static string Summary()
	{
		return "T28 unified Job Search package context: installed=" + installed + ", packages=" + Interlocked.Read(ref packages) + ", nested=" + Interlocked.Read(ref nestedPackages) + ", maxDepth=" + Interlocked.Read(ref maxDepth) + ", mismatchedExits=" + Interlocked.Read(ref mismatchedExits) + ", moduleStateCreates=" + Interlocked.Read(ref moduleStateCreates) + ", moduleStateCapBypass=" + Interlocked.Read(ref moduleStateCapacityBypass) + ", billNegative[hits/stores]=" + Interlocked.Read(ref billNegativeHits) + "/" + Interlocked.Read(ref billNegativeStores) + ", genericNegative[hits/stores/capBypass]=" + Interlocked.Read(ref genericNegativeHits) + "/" + Interlocked.Read(ref genericNegativeStores) + "/" + Interlocked.Read(ref genericNegativeCapacityBypass) + ", currentGeneration=" + CurrentGeneration + ". One synchronous TryIssueJobPackage boundary only; no Job/reservation/priority/cross-package result is cached.";
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
