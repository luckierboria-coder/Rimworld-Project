using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(Room), "Notify_RoomShapeChanged")]
internal static class Patch_Notify_RoomShapeChanged
{
	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	public static void Postfix(Room __instance)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Invalid comparison between Unknown and I4
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		if ((int)Current.ProgramState == 1 || !DoorPathingUtility.compCache.TryGetValue(__instance.Map?.uniqueID ?? (-1), out var value))
		{
			return;
		}
		using IEnumerator<IntVec3> enumerator = __instance.Cells.GetEnumerator();
		if (enumerator.MoveNext())
		{
			IntVec3 current = enumerator.Current;
			value.ValidateRoomDoors(current, roomUpdate: true);
		}
	}
}
