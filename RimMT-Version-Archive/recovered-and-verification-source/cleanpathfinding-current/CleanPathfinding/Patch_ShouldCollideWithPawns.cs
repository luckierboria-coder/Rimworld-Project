using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CleanPathfinding;

[HarmonyPatch(typeof(PawnUtility), "ShouldCollideWithPawns")]
internal static class Patch_ShouldCollideWithPawns
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
	{
		if (!ModSettings_CleanPathfinding.optimizeCollider || instructions.Count() != 32)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
			}
			if (ModSettings_CleanPathfinding.optimizeCollider)
			{
				Log.Warning("[Clean Pathfinding] Could not apply collider optimization patch. There may be a mod conflict, or RimWorld updated?");
			}
			yield break;
		}
		Label endLabel = generator.DefineLabel();
		CodeInstruction endInstruction = new CodeInstruction(OpCodes.Ldc_I4_0, (object)null);
		endInstruction.labels.Add(endLabel);
		Label shamblerLabel = generator.DefineLabel();
		CodeInstruction shamblerInstruction = new CodeInstruction(OpCodes.Ldarg_0, (object)null);
		shamblerInstruction.labels.Add(shamblerLabel);
		yield return new CodeInstruction(OpCodes.Ldarg_0, (object)null);
		yield return new CodeInstruction(OpCodes.Callvirt, (object)AccessTools.Method(typeof(Pawn), "get_IsShambler", (Type[])null, (Type[])null));
		yield return new CodeInstruction(OpCodes.Brtrue_S, (object)shamblerLabel);
		yield return new CodeInstruction(OpCodes.Ldarg_0, (object)null);
		yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(Pawn), "mindState"));
		yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(Pawn_MindState), "anyCloseHostilesRecently"));
		yield return new CodeInstruction(OpCodes.Brfalse_S, (object)endLabel);
		yield return new CodeInstruction(OpCodes.Ldarg_0, (object)null);
		yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(Pawn), "kindDef"));
		yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(PawnKindDef), "collidesWithPawns"));
		yield return new CodeInstruction(OpCodes.Brfalse_S, (object)endLabel);
		yield return new CodeInstruction(OpCodes.Ldarg_0, (object)null);
		yield return new CodeInstruction(OpCodes.Call, (object)AccessTools.Method(typeof(InvisibilityUtility), "IsPsychologicallyInvisible", (Type[])null, (Type[])null));
		yield return new CodeInstruction(OpCodes.Brtrue_S, (object)endLabel);
		yield return shamblerInstruction;
		yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(Pawn), "health"));
		yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(Pawn_HealthTracker), "healthState"));
		yield return new CodeInstruction(OpCodes.Ldc_I4_1, (object)null);
		yield return new CodeInstruction(OpCodes.Cgt, (object)null);
		yield return new CodeInstruction(OpCodes.Ret, (object)null);
		yield return endInstruction;
		yield return new CodeInstruction(OpCodes.Ret, (object)null);
	}
}
