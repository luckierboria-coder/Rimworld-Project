using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace CleanPathfinding;

[HarmonyPatch]
public class Patch_RegionCostCalculator_GetRegionBestDistances_Fix
{
	private static MethodBase _target;

	private static bool Prepare()
	{
		_target = AccessTools.Method(typeof(RegionCostCalculator), "GetRegionBestDistances", (Type[])null, (Type[])null);
		return ModSettings_CleanPathfinding.enableRegionDistanceFix && _target != null;
	}

	private static MethodBase TargetMethod()
	{
		return _target;
	}

	private static int FindIndexPrev(List<CodeInstruction> ci, OpCode opcode)
	{
		for (int num = ci.Count - 1; num >= 0; num--)
		{
			if (ci[num].opcode == opcode)
			{
				return num;
			}
		}
		return -1;
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		List<CodeInstruction> original = new List<CodeInstruction>(instructions);
		MethodInfo allowsMethod = AccessTools.Method(typeof(Region), "Allows", new Type[2]
		{
			typeof(TraverseParms),
			typeof(bool)
		}, (Type[])null);
		MethodInfo getOtherRegionMethod = AccessTools.Method(typeof(RegionLink), "GetOtherRegion", new Type[1] { typeof(Region) }, (Type[])null);
		if (allowsMethod != null && getOtherRegionMethod != null)
		{
			CodeInstruction instructionWithJump = null;
			bool nextThingLabel = false;
			foreach (CodeInstruction v in original)
			{
				if (nextThingLabel)
				{
					instructionWithJump = v;
					break;
				}
				int num;
				if (v.opcode == OpCodes.Callvirt)
				{
					object operand = v.operand;
					if (operand is MethodInfo mi)
					{
						num = ((mi == allowsMethod) ? 1 : 0);
						goto IL_0172;
					}
				}
				num = 0;
				goto IL_0172;
				IL_0172:
				if (num != 0)
				{
					nextThingLabel = true;
				}
			}
			if (instructionWithJump != null)
			{
				List<CodeInstruction> modified = new List<CodeInstruction>();
				bool done = false;
				bool hasLogged = false;
				foreach (CodeInstruction v2 in original)
				{
					int num2;
					if (!done && v2.opcode == OpCodes.Callvirt)
					{
						object operand = v2.operand;
						if (operand is MethodInfo mi2)
						{
							num2 = ((mi2 == allowsMethod) ? 1 : 0);
							goto IL_0269;
						}
					}
					num2 = 0;
					goto IL_0269;
					IL_0269:
					if (num2 != 0)
					{
						int injectionPoint = FindIndexPrev(modified, OpCodes.Callvirt);
						if (injectionPoint == -1)
						{
							Log.Message("Failed to find GetOtherRegion callvirt for RegionCostCalculator.GetRegionBestDistances fix? skipping this patch!");
							hasLogged = true;
							break;
						}
						int beqIndex = FindIndexPrev(modified, OpCodes.Beq_S);
						if (beqIndex == -1)
						{
							Log.Message("Failed to find BEQ for RegionCostCalculator.GetRegionBestDistances fix? skipping this patch!");
							hasLogged = true;
							break;
						}
						List<CodeInstruction> getOtherRegionInstructions = new List<CodeInstruction>();
						int endIndex = injectionPoint;
						int startIndex = beqIndex + 1;
						for (int i = startIndex; i <= endIndex; i++)
						{
							getOtherRegionInstructions.Add(original[i]);
						}
						getOtherRegionInstructions.Add(new CodeInstruction(OpCodes.Brfalse, instructionWithJump.operand));
						modified.InsertRange(beqIndex + 1, getOtherRegionInstructions);
						done = true;
					}
					modified.Add(v2);
				}
				if (done)
				{
					foreach (CodeInstruction item in modified)
					{
						yield return item;
					}
					yield break;
				}
				if (!hasLogged)
				{
					Log.Message("Failed to inject our jump point for RegionCostCalculator.GetRegionBestDistances fix? skipping this patch!");
				}
			}
			else
			{
				Log.Message("Failed to find jump for RegionCostCalculator.GetRegionBestDistances fix? skipping this patch!");
			}
		}
		else
		{
			string msg = ((allowsMethod == null && getOtherRegionMethod == null) ? "Allows, GetOtherRegion" : ((allowsMethod == null) ? "Allow" : "GetOtherRegion"));
			Log.Message("Failed to find " + msg + " for RegionCostCalculator.GetRegionBestDistances fix? skipping this patch!");
		}
		ModSettings_CleanPathfinding.regionDistanceFixFailure = true;
		foreach (CodeInstruction item2 in original)
		{
			yield return item2;
		}
	}
}
