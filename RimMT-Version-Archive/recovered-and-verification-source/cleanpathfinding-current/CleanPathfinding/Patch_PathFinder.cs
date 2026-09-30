using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace CleanPathfinding;

[HarmonyPatch(typeof(PathFinder), "FindPath", new Type[]
{
	typeof(IntVec3),
	typeof(LocalTargetInfo),
	typeof(TraverseParms),
	typeof(PathEndMode),
	typeof(PathFinderCostTuning)
})]
internal static class Patch_PathFinder
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		int offset = -1;
		int objectsFound = 0;
		bool ran = false;
		bool searchForObjects = false;
		bool thresholdReplaced = false;
		FieldInfo method_regionModeThreshold = AccessTools.Field(typeof(ModSettings_CleanPathfinding), "regionModeThreshold");
		FieldInfo field_extraDraftedPerceivedPathCost = AccessTools.Field(typeof(TerrainDef), "extraDraftedPerceivedPathCost");
		FieldInfo field_extraNonDraftedPerceivedPathCost = AccessTools.Field(typeof(TerrainDef), "extraNonDraftedPerceivedPathCost");
		object[] objects = new object[3];
		foreach (CodeInstruction code in instructions)
		{
			if (!thresholdReplaced && code.opcode == OpCodes.Ldc_I4 && CodeInstructionExtensions.OperandIs(code, (object)100000))
			{
				code.opcode = OpCodes.Ldsfld;
				code.operand = method_regionModeThreshold;
			}
			yield return code;
			if (!searchForObjects && code.opcode == OpCodes.Ldfld && CodeInstructionExtensions.OperandIs(code, (MemberInfo)field_extraDraftedPerceivedPathCost))
			{
				searchForObjects = true;
				continue;
			}
			if (searchForObjects && objectsFound < 3 && code.opcode == OpCodes.Ldloc_S)
			{
				objects[objectsFound++] = code.operand;
			}
			if (offset == -1 && code.opcode == OpCodes.Ldfld && CodeInstructionExtensions.OperandIs(code, (MemberInfo)field_extraNonDraftedPerceivedPathCost))
			{
				offset = 0;
				continue;
			}
			int num2;
			if (offset > -1)
			{
				int num = offset + 1;
				offset = num;
				num2 = ((num == 3) ? 1 : 0);
			}
			else
			{
				num2 = 0;
			}
			if (num2 != 0)
			{
				yield return new CodeInstruction(OpCodes.Ldloc_0, (object)null);
				yield return new CodeInstruction(OpCodes.Ldloc_S, objects[1]);
				yield return new CodeInstruction(OpCodes.Ldloc_S, objects[2]);
				yield return new CodeInstruction(OpCodes.Ldelem_Ref, (object)null);
				yield return new CodeInstruction(OpCodes.Ldloc_S, objects[0]);
				yield return new CodeInstruction(OpCodes.Ldarg_0, (object)null);
				yield return new CodeInstruction(OpCodes.Ldfld, (object)AccessTools.Field(typeof(PathFinder), "map"));
				yield return new CodeInstruction(OpCodes.Ldloc_S, objects[2]);
				yield return new CodeInstruction(OpCodes.Call, (object)typeof(CleanPathfindingUtility).GetMethod("AdjustCosts"));
				yield return new CodeInstruction(OpCodes.Stloc_S, objects[0]);
				ran = true;
			}
		}
		if (!ran)
		{
			Log.Warning("[Clean Pathfinding] Transpiler could not find target. There may be a mod conflict, or RimWorld updated?");
		}
	}
}
