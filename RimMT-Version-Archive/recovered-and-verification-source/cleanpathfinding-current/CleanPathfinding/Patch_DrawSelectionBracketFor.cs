using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(SelectionDrawer), "DrawSelectionBracketFor")]
internal static class Patch_DrawSelectionBracketFor
{
	private static bool Prepare()
	{
		return ModSettings_CleanPathfinding.doorPathing;
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		int offset = 0;
		bool ran = false;
		foreach (CodeInstruction code in instructions)
		{
			yield return code;
			if (offset != 4 && code.opcode == OpCodes.Stloc_3)
			{
				int num = offset + 1;
				offset = num;
			}
			else if (!ran && offset == 3)
			{
				yield return new CodeInstruction(OpCodes.Ldloc_1, (object)null);
				yield return new CodeInstruction(OpCodes.Call, (object)typeof(DoorPathingUtility).GetMethod("DrawDoorField"));
				ran = true;
			}
		}
		if (!ran)
		{
			Log.Warning("[Clean Pathfinding] Transpiler could not find target for door edge drawer. There may be a mod conflict, or RimWorld updated?");
		}
	}
}
