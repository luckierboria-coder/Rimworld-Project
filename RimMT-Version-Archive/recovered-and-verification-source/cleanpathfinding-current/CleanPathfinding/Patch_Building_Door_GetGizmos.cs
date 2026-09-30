using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(Building_Door), "GetGizmos")]
internal static class Patch_Building_Door_GetGizmos
{
	private static bool Prepare()
	{
		if (!Mod_CleanPathfinding.patchLedger.ContainsKey("Patch_Building_Door_GetGizmos"))
		{
			Mod_CleanPathfinding.patchLedger.Add("Patch_Building_Door_GetGizmos", ModSettings_CleanPathfinding.doorPathing);
		}
		return ModSettings_CleanPathfinding.doorPathing;
	}

	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Building_Door __instance)
	{
		return DoorPathingUtility.GetGizmos(values, (Building)(object)__instance);
	}
}
