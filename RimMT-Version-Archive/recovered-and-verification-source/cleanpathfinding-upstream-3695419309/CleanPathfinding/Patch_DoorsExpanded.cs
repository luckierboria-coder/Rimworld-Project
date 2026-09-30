using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch]
internal static class Patch_DoorsExpanded
{
	private static MethodBase target;

	private static bool Prepare()
	{
		target = AccessTools.DeclaredMethod(AccessTools.TypeByName("DoorsExpanded.Building_DoorExpanded"), "GetGizmos", (Type[])null, (Type[])null);
		return target != null;
	}

	private static MethodBase TargetMethod()
	{
		DoorPathingUtility.usingDoorsExpanded = true;
		return target;
	}

	private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Building __instance)
	{
		if (!ModSettings_CleanPathfinding.doorPathing)
		{
			foreach (Gizmo value in values)
			{
				yield return value;
			}
			yield break;
		}
		foreach (Gizmo gizmo in DoorPathingUtility.GetGizmos(values, __instance))
		{
			yield return gizmo;
		}
	}
}
