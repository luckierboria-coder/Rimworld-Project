using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class HaulMergeCheapNegative094
{
	private static readonly Dictionary<Type, bool> AuthorityCache = new Dictionary<Type, bool>();

	internal static bool IsCandidate(WorkGiver_Scanner scanner)
	{
		if (scanner != null && ((object)scanner).GetType() == typeof(WorkGiver_Merge) && ((WorkGiver)scanner).def != null)
		{
			return ((Def)((WorkGiver)scanner).def).defName == "HaulMerge";
		}
		return false;
	}

	internal static bool IsAuthoritySafe(WorkGiver_Scanner scanner)
	{
		if (!IsCandidate(scanner))
		{
			return false;
		}
		Type type = ((object)scanner).GetType();
		if (AuthorityCache.TryGetValue(type, out var value))
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
					if (HasHarmonyAuthority(type2.GetMethod(array[i], BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, types, null)))
					{
						flag = false;
						break;
					}
					type2 = type2.BaseType;
				}
			}
			if (flag)
			{
				MethodInfo methodInfo = AccessTools.Method(typeof(HaulAIUtility), "PawnCanAutomaticallyHaul", new Type[3]
				{
					typeof(Pawn),
					typeof(Thing),
					typeof(bool)
				}, (Type[])null);
				MethodInfo methodInfo2 = AccessTools.Method(typeof(HaulAIUtility), "PawnCanAutomaticallyHaulFast", new Type[3]
				{
					typeof(Pawn),
					typeof(Thing),
					typeof(bool)
				}, (Type[])null);
				if (methodInfo == null || methodInfo2 == null || HasHarmonyAuthority(methodInfo) || HasHarmonyAuthority(methodInfo2))
				{
					flag = false;
				}
			}
		}
		catch
		{
			flag = false;
		}
		AuthorityCache[type] = flag;
		return flag;
	}

	private static bool HasHarmonyAuthority(MethodBase method)
	{
		if (method == null)
		{
			return false;
		}
		Patches patchInfo = Harmony.GetPatchInfo(method);
		if (patchInfo != null)
		{
			if (patchInfo.Prefixes.Count == 0 && patchInfo.Postfixes.Count == 0 && patchInfo.Transpilers.Count == 0)
			{
				return patchInfo.Finalizers.Count != 0;
			}
			return true;
		}
		return false;
	}

	internal static bool Pass(Pawn worker, Thing thing)
	{
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (worker == null || thing == null || thing.def == null)
			{
				return true;
			}
			if (thing.stackCount == thing.def.stackLimit)
			{
				return false;
			}
			if (!thing.def.EverHaulable)
			{
				return false;
			}
			if (ForbidUtility.IsForbidden(thing, worker))
			{
				return false;
			}
			Map map = thing.Map;
			if (map == null || map.designationManager == null)
			{
				return true;
			}
			if (!thing.def.alwaysHaulable && map.designationManager.DesignationOn(thing, DesignationDefOf.Haul) == null && !StoreUtility.IsInValidStorage(thing))
			{
				return false;
			}
			UnfinishedThing val = (UnfinishedThing)(object)((thing is UnfinishedThing) ? thing : null);
			if (val != null && val.BoundBill != null)
			{
				Building val2 = (Building)((((Bill)val.BoundBill).billStack == null) ? null : /*isinst with value type is only supported in some contexts*/);
				if (val2 == null)
				{
					goto IL_00ff;
				}
				if (((Thing)val2).Spawned)
				{
					CellRect val3 = GenAdj.OccupiedRect((Thing)(object)val2);
					val3 = ((CellRect)(ref val3)).ExpandedBy(1);
					if (((CellRect)(ref val3)).Contains(((Thing)val).Position))
					{
						goto IL_00ff;
					}
				}
			}
			if (worker.health == null || worker.health.capacities == null)
			{
				return true;
			}
			if (!worker.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
			{
				return false;
			}
			if (thing.def.IsNutritionGivingIngestible && thing.def.ingestible != null && thing.def.ingestible.HumanEdible && !SocialProperness.IsSociallyProper(thing, worker, false, true))
			{
				return false;
			}
			if (FireUtility.IsBurning(thing))
			{
				return false;
			}
			return true;
			IL_00ff:
			return false;
		}
		catch
		{
			return true;
		}
	}
}
