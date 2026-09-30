using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

[StaticConstructorOnStartup]
internal static class CompositeWorkPruners092
{
	private sealed class SowMapCache
	{
		private int tick = int.MinValue;

		internal readonly Dictionary<IntVec3, ThingDef> Wanted = new Dictionary<IntVec3, ThingDef>();

		internal void Prepare(int currentTick)
		{
			if (tick != currentTick)
			{
				tick = currentTick;
				Wanted.Clear();
			}
		}
	}

	private sealed class TendMapCache
	{
		private int tick = int.MinValue;

		internal bool Any;

		internal bool AnyHumanlike;

		internal bool AnyAnimal;

		internal bool UrgentAny;

		internal bool UrgentHumanlike;

		internal bool UrgentAnimal;

		internal void RefreshIfNeeded(Map map)
		{
			int num = CurrentTick();
			if (tick == num)
			{
				return;
			}
			tick = num;
			Any = (AnyHumanlike = (AnyAnimal = (UrgentAny = (UrgentHumanlike = (UrgentAnimal = false)))));
			IReadOnlyList<Pawn> allPawnsSpawned = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < allPawnsSpawned.Count; i++)
			{
				Pawn val = allPawnsSpawned[i];
				if (val == null || val.Dead)
				{
					continue;
				}
				bool flag;
				bool flag2;
				try
				{
					flag = HealthAIUtility.ShouldBeTendedNowByPlayer(val);
					flag2 = flag && HealthAIUtility.ShouldBeTendedNowByPlayerUrgent(val);
				}
				catch
				{
					continue;
				}
				if (!flag)
				{
					continue;
				}
				Any = true;
				if (val.RaceProps != null && val.RaceProps.Humanlike)
				{
					AnyHumanlike = true;
				}
				if (val.RaceProps != null && val.RaceProps.Animal)
				{
					AnyAnimal = true;
				}
				if (flag2)
				{
					UrgentAny = true;
					if (val.RaceProps != null && val.RaceProps.Humanlike)
					{
						UrgentHumanlike = true;
					}
					if (val.RaceProps != null && val.RaceProps.Animal)
					{
						UrgentAnimal = true;
					}
				}
			}
		}
	}

	private static readonly Thing[] EmptyThings;

	private static readonly ConditionalWeakTable<Map, TendMapCache> TendCaches;

	private static readonly ConditionalWeakTable<Map, SowMapCache> SowCaches;

	private static bool buildRoof;

	private static bool tend;

	private static bool harvest;

	private static bool sow;

	private static bool clearSnow;

	static CompositeWorkPruners092()
	{
		EmptyThings = (Thing[])(object)new Thing[0];
		TendCaches = new ConditionalWeakTable<Map, TendMapCache>();
		SowCaches = new ConditionalWeakTable<Map, SowMapCache>();
		LongEventHandler.ExecuteWhenFinished((Action)Install);
	}

	private static void Install()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		try
		{
			Harmony harmony = new Harmony("allen.rimmt");
			InstallBuildRoof(harmony);
			InstallTend(harmony);
			InstallGrowers(harmony);
			InstallClearSnow(harmony);
			if (tend)
			{
				InstallSharedThingSource(harmony);
			}
			Log.Message("[RimMT] Unified S5.3 pruners: BuildRoof=" + buildRoof + ", Tend=" + tend + ", Harvest=" + harvest + ", Sow=" + sow + ", ClearSnow=" + clearSnow + ". DoBill is handled by the persistent RC2 index.");
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] Unified S5.3 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void InstallBuildRoof(Harmony harmony)
	{
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Expected O, but got Unknown
		MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiver_BuildRoof), "PotentialWorkCellsGlobal", new Type[1] { typeof(Pawn) }, (Type[])null);
		MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiver_BuildRoof), "HasJobOnCell", new Type[3]
		{
			typeof(Pawn),
			typeof(IntVec3),
			typeof(bool)
		}, (Type[])null);
		if (!(methodInfo == null) && !(methodInfo2 == null) && !HasUnsafeForeignPatch(methodInfo2, null))
		{
			harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(typeof(CompositeWorkPruners092), "BuildRoofCellsPostfix", (Type[])null)
			{
				priority = 0
			}, (HarmonyMethod)null, (HarmonyMethod)null);
			buildRoof = true;
		}
	}

	private static void InstallTend(Harmony harmony)
	{
		MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiver_Tend), "HasJobOnThing", new Type[3]
		{
			typeof(Pawn),
			typeof(Thing),
			typeof(bool)
		}, (Type[])null);
		MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiver_TendOtherUrgent), "HasJobOnThing", new Type[3]
		{
			typeof(Pawn),
			typeof(Thing),
			typeof(bool)
		}, (Type[])null);
		if (!(methodInfo == null) && !(methodInfo2 == null) && !HasUnsafeForeignPatch(methodInfo, null) && !HasUnsafeForeignPatch(methodInfo2, null))
		{
			tend = true;
		}
	}

	private static void InstallGrowers(Harmony harmony)
	{
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Expected O, but got Unknown
		MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiver_Grower), "PotentialWorkCellsGlobal", new Type[1] { typeof(Pawn) }, (Type[])null);
		if (!(methodInfo == null))
		{
			MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiver_GrowerHarvest), "HasJobOnCell", new Type[3]
			{
				typeof(Pawn),
				typeof(IntVec3),
				typeof(bool)
			}, (Type[])null);
			if (methodInfo2 != null && !HasUnsafeForeignPatch(methodInfo2, IsKnownSafeHarvestPatch))
			{
				harvest = true;
			}
			MethodInfo methodInfo3 = AccessTools.Method(typeof(WorkGiver_GrowerSow), "JobOnCell", new Type[3]
			{
				typeof(Pawn),
				typeof(IntVec3),
				typeof(bool)
			}, (Type[])null);
			MethodInfo methodInfo4 = AccessTools.Method(typeof(WorkGiver_Grower), "CalculateWantedPlantDef", new Type[2]
			{
				typeof(IntVec3),
				typeof(Map)
			}, (Type[])null);
			if (methodInfo3 != null && methodInfo4 != null && !HasUnsafeForeignPatch(methodInfo3, null) && !HasUnsafeForeignPatch(methodInfo4, null))
			{
				sow = true;
			}
			if (harvest || sow)
			{
				harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(typeof(CompositeWorkPruners092), "GrowerCellsPostfix", (Type[])null)
				{
					priority = 0
				}, (HarmonyMethod)null, (HarmonyMethod)null);
			}
		}
	}

	private static void InstallClearSnow(Harmony harmony)
	{
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Expected O, but got Unknown
		MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiver_ClearSnow), "PotentialWorkCellsGlobal", new Type[1] { typeof(Pawn) }, (Type[])null);
		MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiver_ClearSnow), "HasJobOnCell", new Type[3]
		{
			typeof(Pawn),
			typeof(IntVec3),
			typeof(bool)
		}, (Type[])null);
		if (!(methodInfo == null) && !(methodInfo2 == null) && !HasUnsafeForeignPatch(methodInfo2, null))
		{
			harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(typeof(CompositeWorkPruners092), "ClearSnowCellsPostfix", (Type[])null)
			{
				priority = 0
			}, (HarmonyMethod)null, (HarmonyMethod)null);
			clearSnow = true;
		}
	}

	private static void InstallSharedThingSource(Harmony harmony)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Expected O, but got Unknown
		MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiver_Scanner), "PotentialWorkThingsGlobal", new Type[1] { typeof(Pawn) }, (Type[])null);
		if (methodInfo == null)
		{
			tend = false;
			return;
		}
		harmony.Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(typeof(CompositeWorkPruners092), "PotentialWorkThingsGlobalPostfix", (Type[])null)
		{
			priority = -10
		}, (HarmonyMethod)null, (HarmonyMethod)null);
	}

	public static void BuildRoofCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
	{
		if (buildRoof && pawn != null && ((Thing)pawn).Map != null && __result != null)
		{
			__result = FilterBuildRoof(__result, ((Thing)pawn).Map);
		}
	}

	private static IEnumerable<IntVec3> FilterBuildRoof(IEnumerable<IntVec3> source, Map map)
	{
		foreach (IntVec3 item in source)
		{
			if (!GridsUtility.Roofed(item, map))
			{
				yield return item;
			}
		}
	}

	public static void GrowerCellsPostfix(WorkGiver_Grower __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
	{
		if (__instance != null && pawn != null && ((Thing)pawn).Map != null && __result != null)
		{
			if (harvest && ((object)__instance).GetType() == typeof(WorkGiver_GrowerHarvest))
			{
				__result = FilterHarvest(__result, ((Thing)pawn).Map);
			}
			else if (sow && ((object)__instance).GetType() == typeof(WorkGiver_GrowerSow))
			{
				__result = FilterSow(__result, ((Thing)pawn).Map);
			}
		}
	}

	private static IEnumerable<IntVec3> FilterHarvest(IEnumerable<IntVec3> source, Map map)
	{
		foreach (IntVec3 item in source)
		{
			Plant plant = GridsUtility.GetPlant(item, map);
			if (plant != null && plant.HarvestableNow && (int)plant.LifeStage == 2 && plant.CanYieldNow())
			{
				yield return item;
			}
		}
	}

	private static IEnumerable<IntVec3> FilterSow(IEnumerable<IntVec3> source, Map map)
	{
		SowMapCache cache = SowCaches.GetValue(map, (Map m) => new SowMapCache());
		cache.Prepare(CurrentTick());
		foreach (IntVec3 item in source)
		{
			if (!cache.Wanted.TryGetValue(item, out var value))
			{
				value = WorkGiver_Grower.CalculateWantedPlantDef(item, map);
				cache.Wanted[item] = value;
			}
			if (value == null)
			{
				continue;
			}
			List<Thing> thingList = GridsUtility.GetThingList(item, map);
			bool flag = false;
			for (int num = 0; num < thingList.Count; num++)
			{
				Thing val = thingList[num];
				if (val != null && val.def == value)
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				yield return item;
			}
		}
	}

	public static void ClearSnowCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
	{
		if (clearSnow && pawn != null && ((Thing)pawn).Map != null && __result != null)
		{
			__result = FilterSnow(__result, ((Thing)pawn).Map);
		}
	}

	private static IEnumerable<IntVec3> FilterSnow(IEnumerable<IntVec3> source, Map map)
	{
		foreach (IntVec3 item in source)
		{
			if (map.snowGrid.GetDepth(item) >= 0.2f)
			{
				yield return item;
			}
		}
	}

	public static void PotentialWorkThingsGlobalPostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
	{
		if (!tend || __result != null || __instance == null || pawn == null || ((Thing)pawn).Map == null || !(__instance is WorkGiver_Tend))
		{
			return;
		}
		string name = ((object)__instance).GetType().Name;
		if (name.IndexOf("TendOther", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			TendMapCache value = TendCaches.GetValue(((Thing)pawn).Map, (Map m) => new TendMapCache());
			value.RefreshIfNeeded(((Thing)pawn).Map);
			bool flag = __instance is WorkGiver_TendOtherUrgent || name.IndexOf("Urgent", StringComparison.OrdinalIgnoreCase) >= 0;
			bool flag2 = name.IndexOf("Humanlike", StringComparison.OrdinalIgnoreCase) >= 0;
			bool flag3 = name.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!((!flag2) ? ((!flag3) ? (flag ? value.UrgentAny : value.Any) : (flag ? value.UrgentAnimal : value.AnyAnimal)) : (flag ? value.UrgentHumanlike : value.AnyHumanlike)))
			{
				__result = EmptyThings;
			}
		}
	}

	private static bool IsKnownSafeHarvestPatch(Patch patch)
	{
		if (patch == null || patch.PatchMethod == null)
		{
			return false;
		}
		string text = ((patch.PatchMethod.DeclaringType == null) ? string.Empty : patch.PatchMethod.DeclaringType.FullName);
		string text2 = patch.PatchMethod.Name ?? string.Empty;
		if (text2.IndexOf("HasJobOnCellHarvestPostfix", StringComparison.OrdinalIgnoreCase) < 0)
		{
			if (text.IndexOf("AlienRace", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return text2.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return false;
		}
		return true;
	}

	private static bool HasUnsafeForeignPatch(MethodBase target, Func<Patch, bool> safeForeign)
	{
		Patches val = ((target == null) ? null : Harmony.GetPatchInfo(target));
		if (val == null)
		{
			return false;
		}
		if (!Check(val.Prefixes, safeForeign) && !Check(val.Postfixes, safeForeign) && !Check(val.Transpilers, safeForeign))
		{
			return Check(val.Finalizers, safeForeign);
		}
		return true;
	}

	private static bool Check(IList<Patch> patches, Func<Patch, bool> safeForeign)
	{
		if (patches == null)
		{
			return false;
		}
		for (int i = 0; i < patches.Count; i++)
		{
			Patch val = patches[i];
			if (val != null && !string.Equals(val.owner, "allen.rimmt", StringComparison.Ordinal) && (safeForeign == null || !safeForeign(val)))
			{
				return true;
			}
		}
		return false;
	}

	private static int CurrentTick()
	{
		try
		{
			return (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
		}
		catch
		{
			return 0;
		}
	}
}
