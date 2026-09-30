using HarmonyLib;
using RimWorld;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(Building_Door), "DeSpawn")]
internal static class Patch_Building_DoorDeSpawn
{
	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	public static void Prefix(Building_Door __instance)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		if (DoorPathingUtility.compCache.TryGetValue(((Thing)__instance).Map.uniqueID, out var value))
		{
			value.doorCostGrid[((Thing)__instance).Map.cellIndices.CellToIndex(((Thing)__instance).Position)] = 0;
		}
	}
}
