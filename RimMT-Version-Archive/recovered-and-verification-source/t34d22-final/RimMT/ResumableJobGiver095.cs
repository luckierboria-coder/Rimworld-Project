using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class ResumableJobGiver095
{
	private sealed class ResumeState
	{
		internal Pawn Pawn;

		internal WorkGiver_Scanner Scanner;

		internal Map Map;

		internal IntVec3 Root;

		internal float MaxDistance;

		internal int CreatedTick;

		internal Thing[] Members;

		internal List<Thing> Passed;

		internal int NextIndex;

		internal int Slices;
	}

	private const int MinSourceCount = 16;

	private const int MaxSourceCount = 8192;

	private const int MaxStates = 128;

	private const int MaxStateAgeTicks = 300;

	private const int BudgetCheckMask = 3;

	private static readonly Dictionary<Pawn, ResumeState> States = new Dictionary<Pawn, ResumeState>();

	private static readonly Dictionary<MethodBase, bool> AuthorityCache = new Dictionary<MethodBase, bool>();

	[ThreadStatic]
	private static Pawn currentPawn;

	[ThreadStatic]
	private static bool suspendedThisPackage;

	private static bool patched;

	private static long observed;

	private static long hotAdmissions;

	private static long statesCreated;

	private static long stateReplacements;

	private static long resumes;

	private static long suspensions;

	private static long completed;

	private static long completedNull;

	private static long candidatesChecked;

	private static long validatorRejected;

	private static long sourceInvalidations;

	private static long staleInvalidations;

	private static long capacityBypass;

	private static long customEnumerableBypass;

	private static long shapeBypass;

	private static long authorityBypass;

	private static long priorityBlocks;

	private static long failures;

	private static long totalSlices;

	private static long maxSliceTicks;

	private static long finalSearchCalls;

	private static long finalSearchOver5;

	private static long finalSearchOver10;

	private static long finalSearchOver20;

	private static long maxFinalSearchTicks;

	internal static void Apply(Harmony harmony)
	{
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Expected O, but got Unknown
		//IL_0095: Expected O, but got Unknown
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Expected O, but got Unknown
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", (Type[])null, (Type[])null);
			MethodBase methodBase2 = AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver", (Type[])null, (Type[])null);
			if (methodBase == null || methodBase2 == null)
			{
				throw new MissingMethodException("JobGiver_Work package/control methods were not found");
			}
			harmony.Patch(methodBase, new HarmonyMethod(typeof(ResumableJobGiver095), "PackagePrefix", (Type[])null)
			{
				priority = 850
			}, (HarmonyMethod)null, (HarmonyMethod)null, new HarmonyMethod(typeof(ResumableJobGiver095), "PackageFinalizer", (Type[])null)
			{
				priority = 0
			});
			harmony.Patch(methodBase2, (HarmonyMethod)null, new HarmonyMethod(typeof(ResumableJobGiver095), "PawnCanUsePostfix", (Type[])null)
			{
				priority = 0
			}, (HarmonyMethod)null, (HarmonyMethod)null);
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			int num = 0;
			foreach (MethodInfo methodInfo in methods)
			{
				if (IsSupportedOverload(methodInfo))
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(ResumableJobGiver095), "ClosestPrefix", (Type[])null)
					{
						priority = 1000
					}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			patched = num > 0;
			Log.Message("[RimMT] V0.9.5 Resumable JobGiver installed on " + num + " ClosestThingReachable overload(s): recurring-hot exact JobGiver_Work validators are main-thread sliced; final Reachability/validator/Job authority remains live.");
		}
		catch (Exception ex)
		{
			patched = false;
			Log.Warning("[RimMT] V0.9.5 Resumable JobGiver failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
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

	public static void PackagePrefix(Pawn __0)
	{
		currentPawn = __0;
		suspendedThisPackage = false;
		if (States.Count > 128)
		{
			PurgeInvalidStates();
		}
	}

	public static Exception PackageFinalizer(Exception __exception)
	{
		currentPawn = null;
		suspendedThisPackage = false;
		return __exception;
	}

	public static void PawnCanUsePostfix(Pawn __0, ref bool __result)
	{
		if (suspendedThisPackage && currentPawn != null && __0 == currentPawn)
		{
			__result = false;
			priorityBlocks++;
		}
	}

	public static bool ClosestPrefix(MethodBase __originalMethod, IntVec3 __0, Map __1, ThingRequest __2, PathEndMode __3, TraverseParms __4, float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Invalid comparison between Unknown and I4
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0175: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0216: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_022e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0236: Unknown result type (might be due to invalid IL or missing references)
		//IL_023e: Unknown result type (might be due to invalid IL or missing references)
		if (!patched || suspendedThisPackage || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		observed++;
		Pawn pawn = __4.pawn;
		if (pawn == null || currentPawn == null || pawn != currentPawn || __1 == null || __1.Disposed || !((Thing)pawn).Spawned || ((Thing)pawn).Map != __1 || !((IntVec3)(ref __0)).IsValid || !GenGrid.InBounds(__0, __1) || __5 <= 0f || __6 == null)
		{
			shapeBypass++;
			return true;
		}
		WorkGiver_Scanner val = ResolveExactJobGiverScanner(__6);
		if (!IsSupportedScanner(val))
		{
			shapeBypass++;
			return true;
		}
		if (!JobGiverTailTelemetry094.IsRecurringHot(val))
		{
			return true;
		}
		hotAdmissions++;
		if (!IsAuthoritySafe(__originalMethod))
		{
			authorityBypass++;
			return true;
		}
		if (!TryGetStableSource(__1, __2, __7, out var source))
		{
			if (__7 != null)
			{
				customEnumerableBypass++;
			}
			else
			{
				shapeBypass++;
			}
			return true;
		}
		int count;
		try
		{
			count = source.Count;
		}
		catch
		{
			sourceInvalidations++;
			return true;
		}
		if (count < 16)
		{
			return true;
		}
		if (count > 8192)
		{
			capacityBypass++;
			return true;
		}
		States.TryGetValue(pawn, out var value);
		ResumeState resumeState = null;
		if (value != null && value.Scanner == val)
		{
			if (!ValidateState(value, source, pawn, __1, __0))
			{
				States.Remove(pawn);
				sourceInvalidations++;
			}
			else
			{
				resumeState = value;
				resumes++;
			}
		}
		if (resumeState == null)
		{
			resumeState = CreateState(pawn, val, source, __1, __0, __5);
			if (resumeState == null)
			{
				return true;
			}
		}
		try
		{
			long timestamp = Stopwatch.GetTimestamp();
			long num = SliceBudgetTicks();
			int num2 = 0;
			while (resumeState.NextIndex < resumeState.Members.Length)
			{
				Thing val2 = resumeState.Members[resumeState.NextIndex++];
				if (val2 != null && val2.Spawned && val2.Map == __1)
				{
					IntVec3 position = val2.Position;
					if (((IntVec3)(ref position)).IsValid)
					{
						long num3 = (long)position.x - (long)__0.x;
						long num4 = (long)position.z - (long)__0.z;
						double num5 = (double)__5 * (double)__5;
						if ((double)(num3 * num3 + num4 * num4) <= num5)
						{
							candidatesChecked++;
							if (__6(val2))
							{
								resumeState.Passed.Add(val2);
							}
							else
							{
								validatorRejected++;
							}
						}
					}
				}
				num2++;
				if ((num2 & 3) == 0 && resumeState.NextIndex < resumeState.Members.Length && Stopwatch.GetTimestamp() - timestamp >= num)
				{
					resumeState.Slices++;
					totalSlices++;
					long num6 = Stopwatch.GetTimestamp() - timestamp;
					if (num6 > maxSliceTicks)
					{
						maxSliceTicks = num6;
					}
					StoreState(pawn, resumeState, value);
					suspendedThisPackage = true;
					suspensions++;
					__result = null;
					return false;
				}
			}
			resumeState.Slices++;
			totalSlices++;
			long num7 = Stopwatch.GetTimestamp() - timestamp;
			if (num7 > maxSliceTicks)
			{
				maxSliceTicks = num7;
			}
			States.Remove(pawn);
			completed++;
			if (resumeState.Passed.Count == 0)
			{
				completedNull++;
				__result = null;
				return false;
			}
			long timestamp2 = Stopwatch.GetTimestamp();
			Thing val3 = GenClosest.ClosestThing_Global_Reachable(__0, __1, (IEnumerable<Thing>)resumeState.Passed, __3, __4, __5, __6, (Func<Thing, float>)null);
			long num8 = Stopwatch.GetTimestamp() - timestamp2;
			finalSearchCalls++;
			if (num8 > maxFinalSearchTicks)
			{
				maxFinalSearchTicks = num8;
			}
			if (num8 >= Stopwatch.Frequency * 5 / 1000)
			{
				finalSearchOver5++;
			}
			if (num8 >= Stopwatch.Frequency * 10 / 1000)
			{
				finalSearchOver10++;
			}
			if (num8 >= Stopwatch.Frequency * 20 / 1000)
			{
				finalSearchOver20++;
			}
			if (val3 == null)
			{
				completedNull++;
			}
			__result = val3;
			return false;
		}
		catch (Exception ex)
		{
			failures++;
			States.Remove(pawn);
			if (failures <= 4)
			{
				Log.Warning("[RimMT] V0.9.5 resumable slice failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
			}
			return true;
		}
	}

	private static WorkGiver_Scanner ResolveExactJobGiverScanner(Predicate<Thing> validator)
	{
		if (validator == null)
		{
			return null;
		}
		try
		{
			MethodInfo method = validator.Method;
			Type type = ((method == null) ? null : method.DeclaringType);
			if (type == null || type.DeclaringType != typeof(JobGiver_Work))
			{
				return null;
			}
			return JobGiverTailTelemetry094.TryResolveScanner(validator);
		}
		catch
		{
			return null;
		}
	}

	private static bool IsSupportedScanner(WorkGiver_Scanner scanner)
	{
		if (scanner == null || ((WorkGiver)scanner).def == null)
		{
			return false;
		}
		try
		{
			if (!((WorkGiver)scanner).def.scanThings || ((WorkGiver)scanner).def.scanCells)
			{
				return false;
			}
			if (scanner.Prioritized || scanner.AllowUnreachable)
			{
				return false;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetStableSource(Map map, ThingRequest request, IEnumerable<Thing> custom, out IList<Thing> source)
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		source = null;
		try
		{
			if (custom != null)
			{
				source = custom as IList<Thing>;
				return source != null;
			}
			if (((ThingRequest)(ref request)).IsUndefined)
			{
				return false;
			}
			source = map.listerThings.ThingsMatching(request);
			return source != null;
		}
		catch
		{
			source = null;
			return false;
		}
	}

	private static ResumeState CreateState(Pawn pawn, WorkGiver_Scanner scanner, IList<Thing> source, Map map, IntVec3 root, float maxDistance)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			int count = source.Count;
			Thing[] array = (Thing[])(object)new Thing[count];
			for (int i = 0; i < count; i++)
			{
				array[i] = source[i];
			}
			ResumeState result = new ResumeState
			{
				Pawn = pawn,
				Scanner = scanner,
				Map = map,
				Root = root,
				MaxDistance = maxDistance,
				CreatedTick = CurrentGameTick(),
				Members = array,
				Passed = new List<Thing>(Math.Min(count, 256)),
				NextIndex = 0,
				Slices = 0
			};
			statesCreated++;
			return result;
		}
		catch
		{
			return null;
		}
	}

	private static bool ValidateState(ResumeState state, IList<Thing> source, Pawn pawn, Map map, IntVec3 root)
	{
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		if (state == null || state.Pawn != pawn || state.Map != map || state.Root != root || state.Members == null)
		{
			return false;
		}
		int num = CurrentGameTick();
		if (num >= 0 && state.CreatedTick >= 0 && num - state.CreatedTick > 300)
		{
			staleInvalidations++;
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
		if (count != state.Members.Length)
		{
			return false;
		}
		for (int i = 0; i < count; i++)
		{
			if (source[i] != state.Members[i])
			{
				return false;
			}
		}
		return true;
	}

	private static void StoreState(Pawn pawn, ResumeState state, ResumeState prior)
	{
		if (States.TryGetValue(pawn, out var value) && value != state)
		{
			stateReplacements++;
		}
		else if (prior != null && prior != state)
		{
			stateReplacements++;
		}
		States[pawn] = state;
	}

	private static bool IsAuthoritySafe(MethodBase method)
	{
		if (method == null)
		{
			return false;
		}
		if (AuthorityCache.TryGetValue(method, out var value))
		{
			return value;
		}
		bool flag = true;
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(method);
			if (patchInfo != null)
			{
				flag = OwnedByRimMT(patchInfo.Prefixes) && OwnedByRimMT(patchInfo.Postfixes) && OwnedByRimMT(patchInfo.Transpilers) && OwnedByRimMT(patchInfo.Finalizers);
			}
		}
		catch
		{
			flag = false;
		}
		AuthorityCache[method] = flag;
		return flag;
	}

	private static bool OwnedByRimMT(IEnumerable<Patch> patches)
	{
		if (patches == null)
		{
			return true;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null && !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	private static long SliceBudgetTicks()
	{
		double num = AdaptiveLoadBalancer.Pressure switch
		{
			LoadPressure.Low => 5.0, 
			LoadPressure.Normal => 4.0, 
			LoadPressure.High => 2.5, 
			_ => 1.5, 
		};
		return Math.Max(1L, (long)((double)Stopwatch.Frequency * num / 1000.0));
	}

	private static int CurrentGameTick()
	{
		try
		{
			return (Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame;
		}
		catch
		{
			return -1;
		}
	}

	private static void PurgeInvalidStates()
	{
		List<Pawn> list = null;
		int num = CurrentGameTick();
		foreach (KeyValuePair<Pawn, ResumeState> state in States)
		{
			Pawn key = state.Key;
			ResumeState value = state.Value;
			bool flag = key == null || value == null || ((Thing)key).Destroyed || !((Thing)key).Spawned || value.Map == null || value.Map.Disposed || ((Thing)key).Map != value.Map;
			if (!flag && num >= 0 && value.CreatedTick >= 0 && num - value.CreatedTick > 300)
			{
				flag = true;
			}
			if (flag)
			{
				if (list == null)
				{
					list = new List<Pawn>();
				}
				list.Add(key);
			}
		}
		if (list != null)
		{
			for (int i = 0; i < list.Count; i++)
			{
				States.Remove(list[i]);
			}
		}
	}

	internal static string Summary()
	{
		double num = (double)maxSliceTicks * 1000000.0 / (double)Stopwatch.Frequency;
		double num2 = (double)maxFinalSearchTicks * 1000000.0 / (double)Stopwatch.Frequency;
		double num3 = ((completed <= 0) ? 0.0 : ((double)totalSlices / (double)completed));
		return "Resumable JobGiver V0.9.5: patched=" + patched + ", observed=" + observed + ", hotAdmissions=" + hotAdmissions + ", activeStates=" + States.Count + ", statesCreated=" + statesCreated + ", replacements=" + stateReplacements + ", resumes=" + resumes + ", suspensions=" + suspensions + ", completed=" + completed + ", completedNull=" + completedNull + ", candidatesChecked=" + candidatesChecked + ", validatorRejected=" + validatorRejected + ", sourceInvalidations=" + sourceInvalidations + ", staleInvalidations=" + staleInvalidations + ", capacityBypass=" + capacityBypass + ", customEnumerableBypass=" + customEnumerableBypass + ", shapeBypass=" + shapeBypass + ", authorityBypass=" + authorityBypass + ", priorityBlocks=" + priorityBlocks + ", avgSlicesPerComplete=" + num3.ToString("F2") + ", maxSliceUs=" + num.ToString("F1") + ", finalSearchCalls=" + finalSearchCalls + " [>5ms=" + finalSearchOver5 + ", >10ms=" + finalSearchOver10 + ", >20ms=" + finalSearchOver20 + "], maxFinalSearchUs=" + num2.ToString("F1") + ", failures=" + failures + ", budgetsMs=[Low=5.0,Normal=4.0,High=2.5,Critical=1.5]";
	}
}
