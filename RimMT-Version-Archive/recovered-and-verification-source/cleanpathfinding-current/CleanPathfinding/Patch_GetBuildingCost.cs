using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CleanPathfinding;

[HarmonyPatch(typeof(PathFinder), "GetBuildingCost")]
public static class Patch_GetBuildingCost
{
	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	private static int Postfix(int __result, Building b, Pawn pawn)
	{
		MapComponent_DoorPathing value;
		DoorPathingUtility.DoorType value2;
		return (((Thing)(pawn?)).factionInt?.def.isPlayer != true || ((DoorPathingUtility.usingDoorsExpanded || !(b is Building_Door)) && (!DoorPathingUtility.usingDoorsExpanded || b == null)) || ((Thing)b).Map == null || !DoorPathingUtility.compCache.TryGetValue(((Thing)b).Map.uniqueID, out value) || !value.doorRegistry.TryGetValue(((Thing)b).thingIDNumber, out value2) || value2 != DoorPathingUtility.DoorType.Exclusive) ? __result : 0;
	}
}
