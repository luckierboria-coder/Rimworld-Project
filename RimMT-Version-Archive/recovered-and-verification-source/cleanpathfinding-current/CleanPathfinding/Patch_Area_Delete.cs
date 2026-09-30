using HarmonyLib;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(Area), "Delete")]
internal static class Patch_Area_Delete
{
	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	public static void Prefix(Area __instance)
	{
		if (__instance.Label == "Avoid" && DoorPathingUtility.compCache.TryGetValue(__instance.Map.uniqueID, out var value))
		{
			value.DeregisterAvoidArea(__instance);
		}
	}
}
