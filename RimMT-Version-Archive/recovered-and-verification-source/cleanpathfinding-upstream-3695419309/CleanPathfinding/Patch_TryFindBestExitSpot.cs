using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CleanPathfinding;

[HarmonyPatch(typeof(RCellFinder), "TryFindBestExitSpot")]
internal static class Patch_TryFindBestExitSpot
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		if (!ModSettings_CleanPathfinding.exitTuning)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
			}
			yield break;
		}
		bool ran = false;
		foreach (CodeInstruction code in instructions)
		{
			yield return code;
			if (code.opcode == OpCodes.Ldc_I4_S)
			{
				yield return new CodeInstruction(OpCodes.Ldsfld, (object)AccessTools.Field(typeof(ModSettings_CleanPathfinding), "exitRange"));
				yield return new CodeInstruction(OpCodes.Add, (object)null);
				ran = true;
			}
		}
		if (!ran)
		{
			Log.Warning("[Clean Pathfinding] Transpiler could not find target for exit finding patch. There may be a mod conflict, or RimWorld updated?");
		}
	}
}
