using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class AggressiveParallelScanner093T34D
{
	private sealed class WorkerUnsafeApiException : InvalidOperationException
	{
		internal WorkerUnsafeApiException(string message)
			: base(message)
		{
		}
	}

	private struct CandidateResult
	{
		internal readonly Thing Thing;

		internal readonly float DistanceSquared;

		internal readonly bool Accepted;

		internal CandidateResult(Thing thing, float distanceSquared)
		{
			Thing = thing;
			DistanceSquared = distanceSquared;
			Accepted = true;
		}
	}

	internal const string FeatureId = "parallel.aggressiveScanner";

	private const int MinCandidates = 48;

	private const int MaxPartitions = 8;

	private static bool installed;

	private static long globalCalls;

	private static long reachableCalls;

	private static long candidates;

	private static long workerBatches;

	private static long mainThreadBatches;

	private static long validatorCalls;

	private static long reachabilityCalls;

	private static long acceptedCandidates;

	private static long enqueueFallbacks;

	private static long failures;

	private static long fallbackAfterFailure;

	private static long unresolvedScannerBypass;

	private static long defaultJobOnThingBypass;

	private static long quarantinedScannerBypass;

	private static long nestedReachabilityAborts;

	private static long waitTicks;

	private static long maxWaitTicks;

	[ThreadStatic]
	private static int workerValidatorDepth;

	private static readonly ConcurrentDictionary<Type, byte> QuarantinedScanners = new ConcurrentDictionary<Type, byte>();

	internal static void Apply(Harmony harmony)
	{
		//IL_0255: Unknown result type (might be due to invalid IL or missing references)
		//IL_025c: Expected O, but got Unknown
		//IL_0278: Unknown result type (might be due to invalid IL or missing references)
		//IL_027f: Expected O, but got Unknown
		//IL_029b: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a2: Expected O, but got Unknown
		//IL_02be: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c5: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(GenClosest), "ClosestThing_Global_NewTemp", new Type[6]
			{
				typeof(IntVec3),
				typeof(IEnumerable),
				typeof(float),
				typeof(Predicate<Thing>),
				typeof(Func<Thing, float>),
				typeof(bool)
			}, (Type[])null);
			MethodBase methodBase2 = AccessTools.Method(typeof(GenClosest), "ClosestThing_Global_Reachable_NewTemp", new Type[9]
			{
				typeof(IntVec3),
				typeof(Map),
				typeof(IEnumerable<Thing>),
				typeof(PathEndMode),
				typeof(TraverseParms),
				typeof(float),
				typeof(Predicate<Thing>),
				typeof(Func<Thing, float>),
				typeof(bool)
			}, (Type[])null);
			MethodBase methodBase3 = AccessTools.Method(typeof(GenClosest), "ClosestThingReachable", new Type[13]
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
			MethodBase methodBase4 = AccessTools.Method(typeof(Reachability), "CanReach", new Type[4]
			{
				typeof(IntVec3),
				typeof(LocalTargetInfo),
				typeof(PathEndMode),
				typeof(TraverseParms)
			}, (Type[])null);
			if (methodBase == null || methodBase2 == null || methodBase3 == null || methodBase4 == null)
			{
				throw new MissingMethodException("GenClosest worker targets not found");
			}
			HarmonyMethod val = new HarmonyMethod(typeof(AggressiveParallelScanner093T34D), "GlobalPrefix", (Type[])null);
			val.priority = 1400;
			HarmonyMethod val2 = new HarmonyMethod(typeof(AggressiveParallelScanner093T34D), "ReachablePrefix", (Type[])null);
			val2.priority = 1400;
			HarmonyMethod val3 = new HarmonyMethod(typeof(AggressiveParallelScanner093T34D), "BroadReachablePrefix", (Type[])null);
			val3.priority = 1400;
			HarmonyMethod val4 = new HarmonyMethod(typeof(AggressiveParallelScanner093T34D), "ReachabilityWorkerGuardPrefix", (Type[])null);
			val4.priority = 1800;
			harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			harmony.Patch(methodBase2, val2, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			harmony.Patch(methodBase3, val3, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			harmony.Patch(methodBase4, val4, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = true;
			Log.Message("[RimMT] T34-D.1.2 worker fast path installed: overridden HasJobOnThing validators execute on workers without T20 argument-array allocation or transaction counters; direct and nested Reachability calls are blocked before Region state and force scanner quarantine.");
		}
		catch (Exception ex)
		{
			installed = false;
			FeatureGate.Suppress("parallel.aggressiveScanner", "T34-D install failed: " + ex.GetType().Name);
			Log.Error("[RimMT] T34-D aggressive parallel scanner install failed: " + ex);
		}
	}

	public static void ReachabilityWorkerGuardPrefix()
	{
		if (workerValidatorDepth <= 0)
		{
			return;
		}
		Interlocked.Increment(ref nestedReachabilityAborts);
		throw new WorkerUnsafeApiException("Reachability.CanReach entered from a T34-D worker validator");
	}

	public static bool GlobalPrefix(IntVec3 center, IEnumerable searchSet, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter, bool lookInHaulSources, ref Thing __result)
	{
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		if (!CanRun() || lookInHaulSources)
		{
			return true;
		}
		IList<Thing> list = TryGetList(searchSet);
		if (list == null || list.Count < 48)
		{
			return true;
		}
		if (!CanParallelizeValidator(validator, out var scanner))
		{
			return true;
		}
		Interlocked.Increment(ref globalCalls);
		if (!TryRun(list, center, null, (PathEndMode)0, default(TraverseParms), maxDistance, validator, priorityGetter, requireReachability: false, scanner, out var chosen))
		{
			return true;
		}
		__result = chosen;
		return false;
	}

	public static bool ReachablePrefix(IntVec3 center, Map map, IEnumerable<Thing> searchSet, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter, bool canLookInHaulableSources, ref Thing __result)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		if (!CanRun() || canLookInHaulableSources || map == null || map.Disposed)
		{
			return true;
		}
		IList<Thing> list = TryGetList(searchSet);
		if (list == null || list.Count < 48)
		{
			return true;
		}
		if (!CanParallelizeValidator(validator, out var scanner))
		{
			return true;
		}
		Interlocked.Increment(ref reachableCalls);
		if (!TryRun(list, center, map, peMode, traverseParams, maxDistance, validator, priorityGetter, requireReachability: true, scanner, out var chosen))
		{
			return true;
		}
		__result = chosen;
		return false;
	}

	public static bool BroadReachablePrefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet, ref Thing __result)
	{
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		if (!CanRun() || map == null || map.Disposed)
		{
			return true;
		}
		object obj = customGlobalSearchSet;
		if (obj == null)
		{
			try
			{
				obj = map.listerThings.ThingsMatching(thingReq);
			}
			catch
			{
				return true;
			}
		}
		IList<Thing> list = TryGetList(obj);
		if (list == null || list.Count < 48)
		{
			return true;
		}
		if (!CanParallelizeValidator(validator, out var scanner))
		{
			return true;
		}
		Interlocked.Increment(ref reachableCalls);
		if (!TryRun(list, root, map, peMode, traverseParams, maxDistance, validator, null, requireReachability: true, scanner, out var chosen))
		{
			return true;
		}
		__result = chosen;
		return false;
	}

	private static bool CanParallelizeValidator(Predicate<Thing> validator, out WorkGiver_Scanner scanner)
	{
		scanner = ResolveScanner(validator);
		if (scanner == null)
		{
			Interlocked.Increment(ref unresolvedScannerBypass);
			return false;
		}
		Type type = ((object)scanner).GetType();
		if (QuarantinedScanners.ContainsKey(type))
		{
			Interlocked.Increment(ref quarantinedScannerBypass);
			return false;
		}
		MethodInfo methodInfo = AccessTools.Method(type, "HasJobOnThing", new Type[3]
		{
			typeof(Pawn),
			typeof(Thing),
			typeof(bool)
		}, (Type[])null);
		if (methodInfo == null || methodInfo.DeclaringType == typeof(WorkGiver_Scanner))
		{
			Interlocked.Increment(ref defaultJobOnThingBypass);
			return false;
		}
		return true;
	}

	private static WorkGiver_Scanner ResolveScanner(Predicate<Thing> validator)
	{
		if (validator == null || validator.Target == null)
		{
			return null;
		}
		try
		{
			object target = validator.Target;
			FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (FieldInfo fieldInfo in fields)
			{
				if (typeof(WorkGiver_Scanner).IsAssignableFrom(fieldInfo.FieldType))
				{
					object value = fieldInfo.GetValue(target);
					WorkGiver_Scanner val = (WorkGiver_Scanner)((value is WorkGiver_Scanner) ? value : null);
					if (val != null)
					{
						return val;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static IList<Thing> TryGetList(object source)
	{
		if (source is IList<Thing> result)
		{
			return result;
		}
		if (source is IList<Pawn> list)
		{
			Thing[] array = (Thing[])(object)new Thing[list.Count];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = (Thing)(object)list[i];
			}
			return array;
		}
		if (source is IList<Building> list2)
		{
			Thing[] array2 = (Thing[])(object)new Thing[list2.Count];
			for (int j = 0; j < array2.Length; j++)
			{
				array2[j] = (Thing)(object)list2[j];
			}
			return array2;
		}
		if (source is IList<IAttackTarget> list3)
		{
			Thing[] array3 = (Thing[])(object)new Thing[list3.Count];
			for (int k = 0; k < array3.Length; k++)
			{
				ref Thing reference = ref array3[k];
				IAttackTarget obj = list3[k];
				reference = (Thing)(object)((obj is Thing) ? obj : null);
			}
			return array3;
		}
		return null;
	}

	private static bool CanRun()
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Invalid comparison between Unknown and I4
		if (installed && FeatureGate.IsEnabled("parallel.aggressiveScanner") && RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2 && JobGiverGlobalNearest04181.InJobGiverScope)
		{
			return RimMTRuntime.Scheduler != null;
		}
		return false;
	}

	private static bool TryRun(IList<Thing> list, IntVec3 center, Map map, PathEndMode peMode, TraverseParms traverseParms, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter, bool requireReachability, WorkGiver_Scanner scanner, out Thing chosen)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_020e: Unknown result type (might be due to invalid IL or missing references)
		//IL_021f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0225: Unknown result type (might be due to invalid IL or missing references)
		//IL_022b: Unknown result type (might be due to invalid IL or missing references)
		chosen = null;
		int count = list.Count;
		Interlocked.Add(ref candidates, count);
		CandidateResult[] results = new CandidateResult[count];
		int num = Math.Min(Math.Min(8, RimMTRuntime.Scheduler.WorkerCount), Math.Max(1, count / 48));
		int num2 = (count + num - 1) / num;
		CountdownEvent done = new CountdownEvent(num);
		Exception firstFailure = null;
		for (int i = 0; i < num; i++)
		{
			int from = i * num2;
			int to = Math.Min(count, from + num2);
			Action action = delegate
			{
				//IL_0034: Unknown result type (might be due to invalid IL or missing references)
				//IL_004a: Unknown result type (might be due to invalid IL or missing references)
				//IL_0055: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					workerValidatorDepth++;
					EvaluateRange(list, results, from, to, center, map, peMode, traverseParms, maxDistance, validator, priorityGetter, requireReachability);
				}
				catch (Exception value)
				{
					Interlocked.CompareExchange(ref firstFailure, value, null);
				}
				finally
				{
					if (workerValidatorDepth > 0)
					{
						workerValidatorDepth--;
					}
					done.Signal();
				}
			};
			if (RimMTRuntime.Scheduler.TryEnqueue("parallel.aggressiveScanner", JobPriority.High, action))
			{
				Interlocked.Increment(ref workerBatches);
				continue;
			}
			Interlocked.Increment(ref enqueueFallbacks);
			Interlocked.Increment(ref mainThreadBatches);
			action();
		}
		long timestamp = Stopwatch.GetTimestamp();
		done.Wait();
		long num3 = Stopwatch.GetTimestamp() - timestamp;
		if (num3 > 0)
		{
			Interlocked.Add(ref waitTicks, num3);
			UpdateMax(ref maxWaitTicks, num3);
		}
		done.Dispose();
		if (firstFailure != null)
		{
			Interlocked.Increment(ref failures);
			Interlocked.Increment(ref fallbackAfterFailure);
			if (scanner != null)
			{
				QuarantinedScanners.TryAdd(((object)scanner).GetType(), 0);
			}
			return false;
		}
		float num4 = 2.1474836E+09f;
		float num5 = float.MinValue;
		for (int num6 = 0; num6 < count; num6++)
		{
			CandidateResult candidateResult = results[num6];
			if (!candidateResult.Accepted)
			{
				continue;
			}
			if (requireReachability)
			{
				Interlocked.Increment(ref reachabilityCalls);
				if (!map.reachability.CanReach(center, LocalTargetInfo.op_Implicit(candidateResult.Thing.SpawnedParentOrMe), peMode, traverseParms))
				{
					continue;
				}
			}
			if (priorityGetter != null)
			{
				float num7 = priorityGetter(candidateResult.Thing);
				if (num7 < num5 || (Mathf.Approximately(num7, num5) && candidateResult.DistanceSquared >= num4))
				{
					continue;
				}
				num5 = num7;
			}
			else if (candidateResult.DistanceSquared >= num4)
			{
				continue;
			}
			chosen = candidateResult.Thing;
			num4 = candidateResult.DistanceSquared;
		}
		return true;
	}

	private static void EvaluateRange(IList<Thing> list, CandidateResult[] results, int from, int to, IntVec3 center, Map map, PathEndMode peMode, TraverseParms traverseParms, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter, bool requireReachability)
	{
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		float num = maxDistance * maxDistance;
		for (int i = from; i < to; i++)
		{
			Thing val = list[i];
			if (val == null)
			{
				continue;
			}
			if (requireReachability)
			{
				if (!val.Spawned)
				{
					continue;
				}
			}
			else if (!val.Spawned && !HaulAIUtility.IsInHaulableInventory(val))
			{
				continue;
			}
			IntVec3 val2 = center - val.PositionHeld;
			float num2 = ((IntVec3)(ref val2)).LengthHorizontalSquared;
			if (num2 > num)
			{
				continue;
			}
			if (validator != null)
			{
				Interlocked.Increment(ref validatorCalls);
				if (!validator(val))
				{
					continue;
				}
			}
			results[i] = new CandidateResult(val, num2);
			Interlocked.Increment(ref acceptedCandidates);
		}
	}

	private static void UpdateMax(ref long target, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, num) != num)
		{
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref globalCalls) + Interlocked.Read(ref reachableCalls);
		double num2 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref waitTicks) * 1000.0 / (double)Stopwatch.Frequency / (double)num));
		double num3 = (double)Interlocked.Read(ref maxWaitTicks) * 1000.0 / (double)Stopwatch.Frequency;
		return "T34-D.1.2 worker fast path: installed=" + installed + ", calls[global/reachable]=" + Interlocked.Read(ref globalCalls) + "/" + Interlocked.Read(ref reachableCalls) + ", candidates=" + Interlocked.Read(ref candidates) + ", batches[worker/main]=" + Interlocked.Read(ref workerBatches) + "/" + Interlocked.Read(ref mainThreadBatches) + ", live[validatorWorker/reachMain/accepted]=" + Interlocked.Read(ref validatorCalls) + "/" + Interlocked.Read(ref reachabilityCalls) + "/" + Interlocked.Read(ref acceptedCandidates) + ", enqueueFallbacks=" + Interlocked.Read(ref enqueueFallbacks) + ", failures/fallbacks=" + Interlocked.Read(ref failures) + "/" + Interlocked.Read(ref fallbackAfterFailure) + ", bypass[unresolved/defaultJobOnThing/quarantined]=" + Interlocked.Read(ref unresolvedScannerBypass) + "/" + Interlocked.Read(ref defaultJobOnThingBypass) + "/" + Interlocked.Read(ref quarantinedScannerBypass) + ", quarantinedTypes=" + QuarantinedScanners.Count + ", nestedReachabilityAborts=" + Interlocked.Read(ref nestedReachabilityAborts) + ", waitMs[avg/max]=" + num2.ToString("F3") + "/" + num3.ToString("F3") + ". Overridden WorkGiver validators execute on workers; Reachability and priority execute on the main thread; failed scanner types fall back and remain quarantined.";
	}
}
