using HarmonyLib;
using RimWorld.Planet;

namespace CleanPathfinding;

[HarmonyPatch(typeof(World), "FinalizeInit")]
internal static class Patch_FinalizeInit
{
	private static void Postfix()
	{
		DoorPathingUtility.compCache.Clear();
		CleanPathfindingUtility.cachedMapID = -1;
		CleanPathfindingUtility.cachedComp = null;
		CleanPathfindingUtility.lastFactionID = -1;
	}
}
