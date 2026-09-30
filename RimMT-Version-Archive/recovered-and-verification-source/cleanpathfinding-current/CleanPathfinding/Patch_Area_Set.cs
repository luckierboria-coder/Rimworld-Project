using HarmonyLib;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(Area), "Set")]
internal static class Patch_Area_Set
{
	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	public static void Postfix(Area __instance, IntVec3 c, bool val)
	{
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		if (__instance.Label == "Avoid" && DoorPathingUtility.compCache.TryGetValue(__instance.Map.uniqueID, out var value))
		{
			value.UpdateAvoidArea(c, val);
		}
	}
}
