using HarmonyLib;
using RimWorld;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(Area_Allowed), "set_RenamableLabel")]
internal static class Patch_Area_Allowed_SetLabel
{
	private static string originalName;

	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	public static void Prefix(Area_Allowed __instance)
	{
		originalName = ((Area)__instance).Label;
	}

	public static void Postfix(Area_Allowed __instance)
	{
		MapComponent_DoorPathing value2;
		if (originalName == "Avoid")
		{
			if (((Area)__instance).Label != "Avoid" && DoorPathingUtility.compCache.TryGetValue(((Area)__instance).Map.uniqueID, out var value))
			{
				value.DeregisterAvoidArea((Area)(object)__instance);
			}
		}
		else if (((Area)__instance).Label == "Avoid" && DoorPathingUtility.compCache.TryGetValue(((Area)__instance).Map.uniqueID, out value2))
		{
			value2.RegisterAvoidArea((Area)(object)__instance);
		}
	}
}
