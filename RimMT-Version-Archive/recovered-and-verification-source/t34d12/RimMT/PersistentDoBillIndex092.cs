using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
			int count = list.Count;
			int num = 0;
			int num2 = 0;
			List<Thing> list2 = null;
			for (int num3 = 0; num3 < list.Count; num3++)
			{
				Thing val2 = list[num3];
				IBillGiver val3 = (IBillGiver)(object)((val2 is IBillGiver) ? val2 : null);
				BillStack val4 = ((val3 == null) ? null : val3.BillStack);
				if (val4 == null || PackageReadinessShouldDoNow(val4))
				{
					num++;
					list2?.Add(val2);
					continue;
				}
				num2++;
				if (list2 == null)
				{
					list2 = new List<Thing>(Math.Min(list.Count, 32));
					for (int num4 = 0; num4 < num3; num4++)
					{
						list2.Add(list[num4]);
					}
				}
			}
			readinessScans += count;
			activeReturned += num;
			inactiveFiltered += num2;
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
		return "DoBill persistent index/readiness: sourceLookups=" + sourceLookups + ", sourceIndexHits=" + sourceIndexHits + ", readinessScans=" + num + ", inactiveFiltered=" + num2 + ", filterRate=" + num3.ToString("F2") + "%, activeReturned=" + activeReturned + ", rebuilds=" + indexRebuilds + ", mutationEvents=" + mutationEvents + ", incrementalAdds=" + incrementalAdds + ", incrementalRemoves=" + incrementalRemoves + ", fallbackClears=" + fallbackClears + ", shouldSkipCalls=" + shouldSkipCalls + ", shouldSkipNoWork=" + shouldSkipNoWork + ", shouldSkipContinue=" + shouldSkipContinue + ", readinessActualChecks=" + readinessActualChecks + ", readinessFalseMemoHits=" + readinessFalseMemoHits + ", readinessFalseMemoStores=" + readinessFalseMemoStores + ", readinessAvoidRate=" + ((readinessActualChecks + readinessFalseMemoHits <= 0) ? "0.00" : ((double)readinessFalseMemoHits * 100.0 / (double)(readinessActualChecks + readinessFalseMemoHits)).ToString("F2")) + "%, packageReadinessBuilds=" + packageReadinessBuilds + ", packageReadinessReuses=" + packageReadinessReuses + ".";
	}
}
