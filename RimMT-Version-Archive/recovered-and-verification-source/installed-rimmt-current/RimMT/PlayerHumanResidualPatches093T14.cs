using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class PlayerHumanResidualPatches093T14
{
	private const int MaxProbeSites = 18;

	private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(PlayerHumanResidualAttribution093T14), "Active");

	private static readonly MethodInfo StartStageMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "StartStage", (Type[])null, (Type[])null);

	private static readonly MethodInfo EndStageMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "EndStage", (Type[])null, (Type[])null);

	private static readonly MethodInfo OriginalEndMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "OriginalEnd", (Type[])null, (Type[])null);

	internal static void Apply(Harmony harmony)
	{
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		MethodBase methodBase = AccessTools.Method(typeof(Pawn), "Tick", (Type[])null, (Type[])null);
		if (methodBase == null)
		{
			PlayerHumanResidualAttribution093T14.SuppressTranspiler("Pawn.Tick target missing");
			Log.Warning("[RimMT] T14 PlayerHuman residual attribution failed closed: Pawn.Tick not found.");
			return;
		}
		try
		{
			HarmonyMethod val = new HarmonyMethod(typeof(PlayerHumanResidualPatches093T14), "Transpiler", (Type[])null)
			{
				priority = 0
			};
			harmony.Patch(methodBase, (HarmonyMethod)null, (HarmonyMethod)null, val, (HarmonyMethod)null);
			Log.Message("[RimMT] T14 PlayerHuman residual attribution installed on Pawn.Tick. Stopwatch work is limited to PlayerHumanlike T2 periodic samples thinned to 1/256 ticks; no tracker method is separately Harmony-patched.");
		}
		catch (Exception ex)
		{
			PlayerHumanResidualAttribution093T14.SuppressTranspiler(ex.GetType().Name + ": " + ex.Message);
			Log.Warning("[RimMT] T14 PlayerHuman residual attribution failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
	{
		//IL_03a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a8: Expected O, but got Unknown
		//IL_03d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e0: Expected O, but got Unknown
		//IL_03ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f6: Expected O, but got Unknown
		//IL_025f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0266: Expected O, but got Unknown
		//IL_0294: Unknown result type (might be due to invalid IL or missing references)
		//IL_029e: Expected O, but got Unknown
		//IL_02ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b6: Expected O, but got Unknown
		//IL_02c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cc: Expected O, but got Unknown
		//IL_02e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f3: Expected O, but got Unknown
		//IL_0301: Unknown result type (might be due to invalid IL or missing references)
		//IL_030b: Expected O, but got Unknown
		//IL_0319: Unknown result type (might be due to invalid IL or missing references)
		//IL_0323: Expected O, but got Unknown
		//IL_032f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0339: Expected O, but got Unknown
		//IL_033f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0346: Expected O, but got Unknown
		List<CodeInstruction> list = new List<CodeInstruction>(instructions);
		PlayerHumanResidualAttribution093T14.ResetProbeRegistry();
		int[] array = new int[list.Count];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = -1;
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		for (int j = 0; j < list.Count; j++)
		{
			CodeInstruction val = list[j];
			if (val.opcode == OpCodes.Ret)
			{
				num4++;
			}
			MethodInfo methodInfo = ((val.opcode == OpCodes.Call || val.opcode == OpCodes.Callvirt) ? (val.operand as MethodInfo) : null);
			if (methodInfo == null || methodInfo.ReturnType != typeof(void))
			{
				continue;
			}
			int num5 = Classify(methodInfo);
			if (num5 < 0)
			{
				continue;
			}
			if (val.blocks != null && val.blocks.Count != 0)
			{
				num3++;
				continue;
			}
			array[j] = num5;
			if (num5 == 7)
			{
				num2++;
			}
			else
			{
				num++;
			}
		}
		if (num > 18 || num4 <= 0 || ActiveField == null || StartStageMethod == null || EndStageMethod == null || OriginalEndMethod == null)
		{
			PlayerHumanResidualAttribution093T14.SuppressTranspiler("shape unsafe: explicitSites=" + num + ", returns=" + num4);
			PlayerHumanResidualAttribution093T14.SetTranspilerShape(num4, num3);
			return list;
		}
		int num6 = Math.Max(0, 18 - num);
		if (num2 > num6)
		{
			int num7 = 0;
			for (int k = 0; k < array.Length; k++)
			{
				if (array[k] == 7)
				{
					if (num7 < num6)
					{
						num7++;
					}
					else
					{
						array[k] = -1;
					}
				}
			}
		}
		List<CodeInstruction> list2 = new List<CodeInstruction>(list.Count + 128);
		for (int l = 0; l < list.Count; l++)
		{
			CodeInstruction val2 = list[l];
			int num8 = array[l];
			if (num8 >= 0)
			{
				MethodInfo methodInfo2 = val2.operand as MethodInfo;
				string methodName = ((methodInfo2 == null) ? "<unknown>" : (((methodInfo2.DeclaringType == null) ? "<null>" : methodInfo2.DeclaringType.FullName) + "." + methodInfo2.Name));
				PlayerHumanResidualAttribution093T14.RegisterProbe(num8, methodName);
				Label label = generator.DefineLabel();
				CodeInstruction val3 = new CodeInstruction(OpCodes.Ldsfld, (object)ActiveField);
				MoveLabels(val2, val3);
				val2.labels.Add(label);
				list2.Add(val3);
				list2.Add(new CodeInstruction(OpCodes.Brfalse_S, (object)label));
				list2.Add(new CodeInstruction(OpCodes.Ldc_I4, (object)num8));
				list2.Add(new CodeInstruction(OpCodes.Call, (object)StartStageMethod));
				list2.Add(val2);
				Label label2 = generator.DefineLabel();
				list2.Add(new CodeInstruction(OpCodes.Ldsfld, (object)ActiveField));
				list2.Add(new CodeInstruction(OpCodes.Brfalse_S, (object)label2));
				list2.Add(new CodeInstruction(OpCodes.Ldc_I4, (object)num8));
				list2.Add(new CodeInstruction(OpCodes.Call, (object)EndStageMethod));
				CodeInstruction val4 = new CodeInstruction(OpCodes.Nop, (object)null);
				val4.labels.Add(label2);
				list2.Add(val4);
			}
			else if (val2.opcode == OpCodes.Ret && (val2.blocks == null || val2.blocks.Count == 0))
			{
				Label label3 = generator.DefineLabel();
				CodeInstruction val5 = new CodeInstruction(OpCodes.Ldsfld, (object)ActiveField);
				MoveLabels(val2, val5);
				val2.labels.Add(label3);
				list2.Add(val5);
				list2.Add(new CodeInstruction(OpCodes.Brfalse_S, (object)label3));
				list2.Add(new CodeInstruction(OpCodes.Call, (object)OriginalEndMethod));
				list2.Add(val2);
			}
			else
			{
				list2.Add(val2);
			}
		}
		PlayerHumanResidualAttribution093T14.SetTranspilerShape(num4, num3);
		return list2;
	}

	private static int Classify(MethodInfo method)
	{
		if (method == null || method.DeclaringType == null)
		{
			return -1;
		}
		string text = method.DeclaringType.FullName ?? string.Empty;
		string text2 = method.Name ?? string.Empty;
		if (text == "Verse.ThingWithComps" && (text2 == "Tick" || text2.StartsWith("TickInterval", StringComparison.Ordinal)))
		{
			return 0;
		}
		if (text.IndexOf("Pawn_JobTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_PathFollower", StringComparison.Ordinal) >= 0)
		{
			return -1;
		}
		if (text2.IndexOf("Tick", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return -1;
		}
		if (text.IndexOf("Pawn_HealthTracker", StringComparison.Ordinal) >= 0)
		{
			return 1;
		}
		if (text.IndexOf("Pawn_NeedsTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_MindState", StringComparison.Ordinal) >= 0)
		{
			return 2;
		}
		if (text.IndexOf("Pawn_StanceTracker", StringComparison.Ordinal) >= 0)
		{
			return 3;
		}
		if (text.IndexOf("Pawn_EquipmentTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_ApparelTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_InventoryTracker", StringComparison.Ordinal) >= 0)
		{
			return 4;
		}
		if (text.IndexOf("Pawn_AbilityTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_GeneTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_PsychicEntropyTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_RoyaltyTracker", StringComparison.Ordinal) >= 0)
		{
			return 5;
		}
		if (text.IndexOf("Pawn_RelationsTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_InteractionsTracker", StringComparison.Ordinal) >= 0 || text.IndexOf("Pawn_GuestTracker", StringComparison.Ordinal) >= 0)
		{
			return 6;
		}
		if ((text.StartsWith("Verse.Pawn_", StringComparison.Ordinal) || text.StartsWith("RimWorld.Pawn_", StringComparison.Ordinal)) && text.IndexOf("PathFollower", StringComparison.Ordinal) < 0 && text.IndexOf("JobTracker", StringComparison.Ordinal) < 0)
		{
			return 7;
		}
		return -1;
	}

	private static void MoveLabels(CodeInstruction from, CodeInstruction to)
	{
		if (from != null && to != null && from.labels != null && from.labels.Count != 0)
		{
			to.labels.AddRange(from.labels);
			from.labels.Clear();
		}
	}
}
