using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

[StaticConstructorOnStartup]
internal static class PersistentDoBillIndex092
{
	private sealed class PackageReadiness
	{
		internal readonly IEnumerable<Thing> Active;

		internal readonly bool HasActive;

		internal readonly int SourceCount;

		internal PackageReadiness(IEnumerable<Thing> active, bool hasActive, int sourceCount)
		{
			Active = active;
			HasActive = hasActive;
			SourceCount = sourceCount;
		}
	}

	private sealed class BillMapCache
	{
		private readonly Dictionary<WorkGiverDef, CacheEntry> byDef = new Dictionary<WorkGiverDef, CacheEntry>();

		internal List<Thing> Get(WorkGiver_DoBill giver, Map map)
		{
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0034: Unknown result type (might be due to invalid IL or missing references)
			//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
			if (byDef.TryGetValue(((WorkGiver)giver).def, out var value) && value != null && value.Things != null)
			{
				return value.Things;
			}
			ThingRequest potentialWorkThingRequest = ((WorkGiver_Scanner)giver).PotentialWorkThingRequest;
			List<Thing> list = map.listerThings.ThingsMatching(potentialWorkThingRequest);
			List<Thing> list2 = new List<Thing>((list != null) ? Math.Min(list.Count, 64) : 0);
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					Thing val = list[i];
					if (val != null && val.Spawned && val.Map == map && val is IBillGiver)
					{
						list2.Add(val);
					}
				}
			}
			byDef[((WorkGiver)giver).def] = new CacheEntry(potentialWorkThingRequest, list2);
			indexRebuilds++;
			return list2;
		}

		internal int AddSpawned(Thing thing, Map map)
		{
			//IL_004d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0052: Unknown result type (might be due to invalid IL or missing references)
			if (thing == null || map == null || !thing.Spawned || thing.Map != map || !(thing is IBillGiver))
			{
				return 0;
			}
			int num = 0;
			foreach (KeyValuePair<WorkGiverDef, CacheEntry> item in byDef)
			{
				CacheEntry value = item.Value;
				if (value != null && value.Things != null)
				{
					ThingRequest request = value.Request;
					if (((ThingRequest)(ref request)).Accepts(thing) && !value.Things.Contains(thing))
					{
						value.Things.Add(thing);
						num++;
					}
				}
			}
			return num;
		}

		internal int Remove(Thing thing)
		{
			if (thing == null)
			{
				return 0;
			}
			int num = 0;
			foreach (KeyValuePair<WorkGiverDef, CacheEntry> item in byDef)
			{
				List<Thing> list = ((item.Value == null) ? null : item.Value.Things);
				if (list == null)
				{
					continue;
				}
				for (int num2 = list.Count - 1; num2 >= 0; num2--)
				{
					if (list[num2] == thing)
					{
						list.RemoveAt(num2);
						num++;
					}
				}
			}
			return num;
		}

		internal void Invalidate()
		{
			byDef.Clear();
		}
	}

	private sealed class CacheEntry
	{
		internal readonly ThingRequest Request;

		internal readonly List<Thing> Things;

		internal CacheEntry(ThingRequest request, List<Thing> things)
		{
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			Request = request;
			Things = things;
		}
	}

	private static readonly ConditionalWeakTable<Map, BillMapCache> Caches;

	private static bool sourcePatched;

	private static bool shouldSkipPatched;

	private static int failureLogs;

	[ThreadStatic]
	private static long packageStamp;

	[ThreadStatic]
	private static Dictionary<WorkGiverDef, PackageReadiness> packageReadiness;

	[ThreadStatic]
	private static BillStack[] readinessStackScratch;

	[ThreadStatic]
	private static byte[] readinessResultScratch;

	private static long sourceLookups;

	private static long sourceIndexHits;

	private static long readinessScans;

	private static long inactiveFiltered;

	private static long activeReturned;

	private static long indexRebuilds;

	private static long mutationEvents;

	private static long incrementalAdds;

	private static long incrementalRemoves;

	private static long fallbackClears;

	private static long shouldSkipCalls;

	private static long shouldSkipNoWork;

	private static long shouldSkipContinue;

	private static long readinessActualChecks;

	private static long readinessFalseMemoHits;

	private static long readinessFalseMemoStores;

	private static long packageReadinessBuilds;

	private static long packageReadinessReuses;

	private static long parallelReadinessCalls;

	private static long parallelReadinessItems;

	private static long parallelReadinessActualChecks;

	private static long parallelReadinessWorkerBatches;

	private static long parallelReadinessMainBatches;

	private static long parallelReadinessEnqueueFallbacks;

	private static long parallelReadinessFailures;

	private static long parallelReadinessFallbacks;

	private static long parallelReadinessWaitTicks;

	private static long parallelReadinessMaxWaitTicks;

	private const int ParallelReadinessMinSource = 32;

	private const int ParallelReadinessMinUnknown = 16;

	private const int ParallelReadinessBatchTarget = 32;

	private const int ParallelReadinessMaxPartitions = 8;

	static PersistentDoBillIndex092()
	{
		Caches = new ConditionalWeakTable<Map, BillMapCache>();
		LongEventHandler.ExecuteWhenFinished((Action)Install);
	}

	private static void Install()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Expected O, but got Unknown
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Expected O, but got Unknown
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Expected O, but got Unknown
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0194: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Expected O, but got Unknown
		try
		{
			Harmony val = new Harmony("allen.rimmt");
			MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiver_Scanner), "PotentialWorkThingsGlobal", new Type[1] { typeof(Pawn) }, (Type[])null);
			if (methodInfo != null)
			{
				val.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(typeof(PersistentDoBillIndex092), "PotentialWorkThingsGlobalPostfix", (Type[])null)
				{
					priority = 0
				}, (HarmonyMethod)null, (HarmonyMethod)null);
				sourcePatched = true;
			}
			MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiver_DoBill), "ShouldSkip", new Type[2]
			{
				typeof(Pawn),
				typeof(bool)
			}, (Type[])null);
			if (methodInfo2 != null && !HasUnsafeForeignPatch(methodInfo2))
			{
				val.Patch((MethodBase)methodInfo2, new HarmonyMethod(typeof(PersistentDoBillIndex092), "ShouldSkipPrefix", (Type[])null)
				{
					priority = 800
				}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				shouldSkipPatched = true;
			}
			MethodInfo methodInfo3 = AccessTools.Method(typeof(Thing), "SpawnSetup", new Type[2]
			{
				typeof(Map),
				typeof(bool)
			}, (Type[])null);
			MethodInfo methodInfo4 = AccessTools.Method(typeof(Thing), "DeSpawn", new Type[1] { typeof(DestroyMode) }, (Type[])null);
			if (methodInfo3 != null)
			{
				val.Patch((MethodBase)methodInfo3, (HarmonyMethod)null, new HarmonyMethod(typeof(PersistentDoBillIndex092), "ThingSpawnedPostfix", (Type[])null)
				{
					priority = 0
				}, (HarmonyMethod)null, (HarmonyMethod)null);
			}
			if (methodInfo4 != null)
			{
				val.Patch((MethodBase)methodInfo4, new HarmonyMethod(typeof(PersistentDoBillIndex092), "ThingDeSpawnPrefix", (Type[])null)
				{
					priority = 800
				}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			}
			Log.Message("[RimMT] Unified persistent DoBill index active: source=" + sourcePatched + ", shouldSkip=" + shouldSkipPatched + ". Stable membership is incrementally maintained; inactive bill givers are removed by live AnyShouldDoNow before expensive JobOnThing.");
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] persistent DoBill index install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PotentialWorkThingsGlobalPostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
	{
		if (!sourcePatched || __result != null || __instance == null || pawn == null || ((Thing)pawn).Map == null)
		{
			return;
		}
		WorkGiver_DoBill val = (WorkGiver_DoBill)(object)((__instance is WorkGiver_DoBill) ? __instance : null);
		if (val == null || ((WorkGiver)val).def == null)
		{
			return;
		}
		sourceLookups++;
		try
		{
			List<Thing> list = Caches.GetValue(((Thing)pawn).Map, (Map m) => new BillMapCache()).Get(val, ((Thing)pawn).Map);
			if (list == null)
			{
				return;
			}
			sourceIndexHits++;
			int activeCount = 0;
			int inactiveCount = 0;
			if (TryBuildActiveParallel(list, out var active, out activeCount, out inactiveCount))
			{
				readinessScans += list.Count;
				activeReturned += activeCount;
				inactiveFiltered += inactiveCount;
				__result = active;
				return;
			}
			int count = list.Count;
			List<Thing> list2 = null;
			for (int num = 0; num < list.Count; num++)
			{
				Thing val2 = list[num];
				IBillGiver val3 = (IBillGiver)(object)((val2 is IBillGiver) ? val2 : null);
				BillStack val4 = ((val3 == null) ? null : val3.BillStack);
				if (val4 != null && !PackageReadinessShouldDoNow(val4))
				{
					inactiveCount++;
					if (list2 == null)
					{
						list2 = new List<Thing>(Math.Min(list.Count, 32));
						for (int num2 = 0; num2 < num; num2++)
						{
							list2.Add(list[num2]);
						}
					}
				}
				else
				{
					activeCount++;
					list2?.Add(val2);
				}
			}
			readinessScans += count;
			activeReturned += activeCount;
			inactiveFiltered += inactiveCount;
			IEnumerable<Thing> enumerable2;
			if (list2 != null)
			{
				IEnumerable<Thing> enumerable = list2;
				enumerable2 = enumerable;
			}
			else
			{
				IEnumerable<Thing> enumerable = list;
				enumerable2 = enumerable;
			}
			__result = enumerable2;
		}
		catch (Exception ex)
		{
			if (failureLogs++ < 4)
			{
				Log.Warning("[RimMT] DoBill source/readiness gate failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	private static bool TryBuildActiveParallel(List<Thing> things, out IEnumerable<Thing> active, out int activeCount, out int inactiveCount)
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Invalid comparison between Unknown and I4
		active = null;
		activeCount = 0;
		inactiveCount = 0;
		if (things == null || things.Count < 32 || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || RimMTRuntime.Scheduler == null)
		{
			return false;
		}
		int count = things.Count;
		BillStack[] stacks = EnsureReadinessStackScratch(count);
		byte[] results = EnsureReadinessResultScratch(count);
		bool inScope = JobSearchPackageContext093T28.InScope;
		int num = 0;
		int num2 = 0;
		for (int i = 0; i < count; i++)
		{
			Thing obj = things[i];
			IBillGiver val = (IBillGiver)(object)((obj is IBillGiver) ? obj : null);
			BillStack val2 = ((val == null) ? null : val.BillStack);
			stacks[i] = val2;
			if (val2 == null)
			{
				results[i] = 1;
			}
			else if (inScope && JobSearchPackageContext093T28.IsBillStackKnownInactive(val2))
			{
				results[i] = 4;
				num2++;
			}
			else
			{
				results[i] = 3;
				num++;
			}
		}
		if (num < 16)
		{
			return false;
		}
		int num3 = Math.Min(8, Math.Min(RimMTRuntime.Scheduler.WorkerCount, Math.Max(1, (count + 32 - 1) / 32)));
		int num4 = (count + num3 - 1) / num3;
		CountdownEvent done = new CountdownEvent(num3);
		Exception firstFailure = null;
		for (int j = 0; j < num3; j++)
		{
			int from = j * num4;
			int to = Math.Min(count, from + num4);
			Action action = delegate
			{
				try
				{
					for (int k = from; k < to; k++)
					{
						if (results[k] == 3)
						{
							results[k] = (byte)(stacks[k].AnyShouldDoNow ? 1 : 2);
						}
					}
				}
				catch (Exception value)
				{
					Interlocked.CompareExchange(ref firstFailure, value, null);
				}
				finally
				{
					done.Signal();
				}
			};
			if (RimMTRuntime.Scheduler.TryEnqueue("parallel.doBillReadiness", JobPriority.High, action))
			{
				Interlocked.Increment(ref parallelReadinessWorkerBatches);
				continue;
			}
			Interlocked.Increment(ref parallelReadinessEnqueueFallbacks);
			Interlocked.Increment(ref parallelReadinessMainBatches);
			action();
		}
		long timestamp = Stopwatch.GetTimestamp();
		done.Wait();
		long num5 = Stopwatch.GetTimestamp() - timestamp;
		done.Dispose();
		if (num5 > 0)
		{
			Interlocked.Add(ref parallelReadinessWaitTicks, num5);
			UpdateMaximum(ref parallelReadinessMaxWaitTicks, num5);
		}
		if (firstFailure != null)
		{
			Interlocked.Increment(ref parallelReadinessFailures);
			Interlocked.Increment(ref parallelReadinessFallbacks);
			return false;
		}
		readinessFalseMemoHits += num2;
		List<Thing> list = null;
		for (int num6 = 0; num6 < count; num6++)
		{
			bool flag = results[num6] == 1;
			if (results[num6] == 3)
			{
				flag = true;
			}
			if (!flag)
			{
				inactiveCount++;
				if (inScope && stacks[num6] != null && results[num6] == 2)
				{
					JobSearchPackageContext093T28.MarkBillStackInactive(stacks[num6]);
					readinessFalseMemoStores++;
				}
				if (list == null)
				{
					list = new List<Thing>(Math.Min(count, 32));
					for (int num7 = 0; num7 < num6; num7++)
					{
						list.Add(things[num7]);
					}
				}
			}
			else
			{
				activeCount++;
				list?.Add(things[num6]);
			}
		}
		readinessActualChecks += num;
		Interlocked.Increment(ref parallelReadinessCalls);
		Interlocked.Add(ref parallelReadinessItems, num);
		Interlocked.Add(ref parallelReadinessActualChecks, num);
		IEnumerable<Thing> enumerable2;
		if (list != null)
		{
			IEnumerable<Thing> enumerable = list;
			enumerable2 = enumerable;
		}
		else
		{
			IEnumerable<Thing> enumerable = things;
			enumerable2 = enumerable;
		}
		active = enumerable2;
		return true;
	}

	private static BillStack[] EnsureReadinessStackScratch(int count)
	{
		if (readinessStackScratch == null || readinessStackScratch.Length < count)
		{
			readinessStackScratch = (BillStack[])(object)new BillStack[NextCapacity(count)];
		}
		return readinessStackScratch;
	}

	private static byte[] EnsureReadinessResultScratch(int count)
	{
		if (readinessResultScratch == null || readinessResultScratch.Length < count)
		{
			readinessResultScratch = new byte[NextCapacity(count)];
		}
		return readinessResultScratch;
	}

	private static int NextCapacity(int count)
	{
		int num = 32;
		while (num < count && num < 16384)
		{
			num <<= 1;
		}
		return Math.Max(count, num);
	}

	private static void UpdateMaximum(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}

	public static bool ShouldSkipPrefix(WorkGiver_DoBill __instance, Pawn pawn, ref bool __result)
	{
		if (!shouldSkipPatched || __instance == null || pawn == null || ((Thing)pawn).Map == null || ((WorkGiver)__instance).def == null)
		{
			return true;
		}
		shouldSkipCalls++;
		try
		{
			List<Thing> list = Caches.GetValue(((Thing)pawn).Map, (Map m) => new BillMapCache()).Get(__instance, ((Thing)pawn).Map);
			for (int num = 0; num < list.Count; num++)
			{
				Thing val = list[num];
				IBillGiver val2 = (IBillGiver)(object)((val is IBillGiver) ? val : null);
				if (val2 != null && (object)val != pawn && val2.BillStack != null && PackageReadinessShouldDoNow(val2.BillStack))
				{
					__result = false;
					shouldSkipContinue++;
					return false;
				}
			}
			__result = true;
			shouldSkipNoWork++;
			return false;
		}
		catch
		{
			return true;
		}
	}

	public static void ThingSpawnedPostfix(Thing __instance, Map map)
	{
		if (!(__instance is IBillGiver) || map == null || !Caches.TryGetValue(map, out var value) || value == null)
		{
			return;
		}
		mutationEvents++;
		try
		{
			incrementalAdds += value.AddSpawned(__instance, map);
		}
		catch (Exception ex)
		{
			value.Invalidate();
			fallbackClears++;
			if (failureLogs++ < 4)
			{
				Log.Warning("[RimMT] DoBill incremental spawn update failed closed to cache clear: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	public static void ThingDeSpawnPrefix(Thing __instance)
	{
		if (!(__instance is IBillGiver))
		{
			return;
		}
		Map val = null;
		try
		{
			val = __instance.Map;
		}
		catch
		{
		}
		if (val == null || !Caches.TryGetValue(val, out var value) || value == null)
		{
			return;
		}
		mutationEvents++;
		try
		{
			incrementalRemoves += value.Remove(__instance);
		}
		catch (Exception ex)
		{
			value.Invalidate();
			fallbackClears++;
			if (failureLogs++ < 4)
			{
				Log.Warning("[RimMT] DoBill incremental despawn update failed closed to cache clear: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	private static PackageReadiness GetPackageReadiness(WorkGiver_DoBill giver, Map map, List<Thing> things)
	{
		if (giver == null || ((WorkGiver)giver).def == null || map == null || things == null)
		{
			return null;
		}
		long currentScopeStartTicks = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
		if (currentScopeStartTicks <= 0)
		{
			return BuildPackageReadiness(things);
		}
		if (packageReadiness == null)
		{
			packageReadiness = new Dictionary<WorkGiverDef, PackageReadiness>();
		}
		if (packageStamp != currentScopeStartTicks)
		{
			packageStamp = currentScopeStartTicks;
			packageReadiness.Clear();
		}
		if (packageReadiness.TryGetValue(((WorkGiver)giver).def, out var value) && value != null && value.SourceCount == things.Count)
		{
			packageReadinessReuses++;
			return value;
		}
		value = BuildPackageReadiness(things);
		packageReadiness[((WorkGiver)giver).def] = value;
		packageReadinessBuilds++;
		return value;
	}

	private static PackageReadiness BuildPackageReadiness(List<Thing> things)
	{
		int count = things.Count;
		int num = 0;
		int num2 = 0;
		List<Thing> list = null;
		for (int i = 0; i < things.Count; i++)
		{
			Thing val = things[i];
			IBillGiver val2 = (IBillGiver)(object)((val is IBillGiver) ? val : null);
			BillStack val3 = ((val2 == null) ? null : val2.BillStack);
			if (val3 == null || PackageReadinessShouldDoNow(val3))
			{
				num++;
				list?.Add(val);
				continue;
			}
			num2++;
			if (list == null)
			{
				list = new List<Thing>(Math.Min(things.Count, 32));
				for (int j = 0; j < i; j++)
				{
					list.Add(things[j]);
				}
			}
		}
		readinessScans += count;
		activeReturned += num;
		inactiveFiltered += num2;
		IEnumerable<Thing> active;
		if (list != null)
		{
			IEnumerable<Thing> enumerable = list;
			active = enumerable;
		}
		else
		{
			IEnumerable<Thing> enumerable = things;
			active = enumerable;
		}
		return new PackageReadiness(active, num > 0, things.Count);
	}

	private static bool PackageReadinessShouldDoNow(BillStack stack)
	{
		if (stack == null)
		{
			return true;
		}
		if (!JobSearchPackageContext093T28.InScope)
		{
			readinessActualChecks++;
			return stack.AnyShouldDoNow;
		}
		if (JobSearchPackageContext093T28.IsBillStackKnownInactive(stack))
		{
			readinessFalseMemoHits++;
			return false;
		}
		readinessActualChecks++;
		bool anyShouldDoNow = stack.AnyShouldDoNow;
		if (!anyShouldDoNow)
		{
			JobSearchPackageContext093T28.MarkBillStackInactive(stack);
			readinessFalseMemoStores++;
		}
		return anyShouldDoNow;
	}

	private static bool HasUnsafeForeignPatch(MethodBase target)
	{
		Patches patchInfo = Harmony.GetPatchInfo(target);
		if (patchInfo == null)
		{
			return false;
		}
		if (!HasForeign(patchInfo.Prefixes) && !HasForeign(patchInfo.Postfixes) && !HasForeign(patchInfo.Transpilers))
		{
			return HasForeign(patchInfo.Finalizers);
		}
		return true;
	}

	private static bool HasForeign(IList<Patch> patches)
	{
		if (patches == null)
		{
			return false;
		}
		for (int i = 0; i < patches.Count; i++)
		{
			Patch val = patches[i];
			if (val != null && !string.Equals(val.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	internal static string Summary()
	{
		long num = readinessScans;
		long num2 = inactiveFiltered;
		double num3 = ((num <= 0) ? 0.0 : ((double)num2 * 100.0 / (double)num));
		long num4 = Interlocked.Read(ref parallelReadinessCalls);
		double num5 = ((num4 == 0L) ? 0.0 : ((double)Interlocked.Read(ref parallelReadinessItems) / (double)num4));
		double num6 = ((num4 == 0L) ? 0.0 : ((double)Interlocked.Read(ref parallelReadinessWaitTicks) * 1000.0 / (double)Stopwatch.Frequency / (double)num4));
		double num7 = (double)Interlocked.Read(ref parallelReadinessMaxWaitTicks) * 1000.0 / (double)Stopwatch.Frequency;
		return "DoBill persistent index/readiness: sourceLookups=" + sourceLookups + ", sourceIndexHits=" + sourceIndexHits + ", readinessScans=" + num + ", inactiveFiltered=" + num2 + ", filterRate=" + num3.ToString("F2") + "%, activeReturned=" + activeReturned + ", rebuilds=" + indexRebuilds + ", mutationEvents=" + mutationEvents + ", incrementalAdds=" + incrementalAdds + ", incrementalRemoves=" + incrementalRemoves + ", fallbackClears=" + fallbackClears + ", shouldSkipCalls=" + shouldSkipCalls + ", shouldSkipNoWork=" + shouldSkipNoWork + ", shouldSkipContinue=" + shouldSkipContinue + ", readinessActualChecks=" + readinessActualChecks + ", readinessFalseMemoHits=" + readinessFalseMemoHits + ", readinessFalseMemoStores=" + readinessFalseMemoStores + ", readinessAvoidRate=" + ((readinessActualChecks + readinessFalseMemoHits <= 0) ? "0.00" : ((double)readinessFalseMemoHits * 100.0 / (double)(readinessActualChecks + readinessFalseMemoHits)).ToString("F2")) + "%, packageReadinessBuilds=" + packageReadinessBuilds + ", packageReadinessReuses=" + packageReadinessReuses + ", parallel[calls/items/actualChecks/avgItems]=" + num4 + "/" + Interlocked.Read(ref parallelReadinessItems) + "/" + Interlocked.Read(ref parallelReadinessActualChecks) + "/" + num5.ToString("F1") + ", batches[worker/main]=" + Interlocked.Read(ref parallelReadinessWorkerBatches) + "/" + Interlocked.Read(ref parallelReadinessMainBatches) + ", enqueueFallbacks=" + Interlocked.Read(ref parallelReadinessEnqueueFallbacks) + ", failures/fallbacks=" + Interlocked.Read(ref parallelReadinessFailures) + "/" + Interlocked.Read(ref parallelReadinessFallbacks) + ", waitMs[avg/max]=" + num6.ToString("F3") + "/" + num7.ToString("F3") + ".";
	}
}
