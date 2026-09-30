using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class PlayerHumanResidualPatches093T14
    {
        private const int MaxProbeSites = 18;
        private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(PlayerHumanResidualAttribution093T14), "Active");
        private static readonly MethodInfo StartStageMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "StartStage");
        private static readonly MethodInfo EndStageMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "EndStage");
        private static readonly MethodInfo OriginalEndMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "OriginalEnd");

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            MethodBase target = AccessTools.Method(typeof(Pawn), "Tick");
            if (target == null)
            {
                PlayerHumanResidualAttribution093T14.SuppressTranspiler("Pawn.Tick target missing");
                Log.Warning("[RimMT] T14 PlayerHuman residual attribution failed closed: Pawn.Tick not found.");
                return;
            }
            try
            {
                HarmonyMethod transpiler = new HarmonyMethod(typeof(PlayerHumanResidualPatches093T14), nameof(Transpiler)) { priority = Priority.Last };
                harmony.Patch(target, transpiler: transpiler);
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
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            PlayerHumanResidualAttribution093T14.ResetProbeRegistry();

            int[] stages = new int[list.Count];
            for (int i = 0; i < stages.Length; i++) stages[i] = -1;
            int explicitSites = 0;
            int otherSites = 0;
            int skippedEh = 0;
            int returns = 0;

            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode == OpCodes.Ret) returns++;
                MethodInfo method = (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) ? ci.operand as MethodInfo : null;
                if (method == null || method.ReturnType != typeof(void)) continue;
                int stage = Classify(method);
                if (stage < 0) continue;
                if (ci.blocks != null && ci.blocks.Count != 0)
                {
                    skippedEh++;
                    continue;
                }
                stages[i] = stage;
                if (stage == (int)PlayerHumanResidualStage093T14.OtherTracker) otherSites++;
                else explicitSites++;
            }

            if (explicitSites > MaxProbeSites || returns <= 0 || ActiveField == null || StartStageMethod == null || EndStageMethod == null || OriginalEndMethod == null)
            {
                string reason = "shape unsafe: explicitSites=" + explicitSites + ", returns=" + returns;
                PlayerHumanResidualAttribution093T14.SuppressTranspiler(reason);
                PlayerHumanResidualAttribution093T14.SetTranspilerShape(returns, skippedEh);
                return list;
            }

            int remainingForOther = Math.Max(0, MaxProbeSites - explicitSites);
            if (otherSites > remainingForOther)
            {
                int kept = 0;
                for (int i = 0; i < stages.Length; i++)
                {
                    if (stages[i] != (int)PlayerHumanResidualStage093T14.OtherTracker) continue;
                    if (kept < remainingForOther) kept++;
                    else stages[i] = -1;
                }
            }

            List<CodeInstruction> output = new List<CodeInstruction>(list.Count + 128);
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                int stage = stages[i];
                if (stage >= 0)
                {
                    MethodInfo method = ci.operand as MethodInfo;
                    string fullName = method == null ? "<unknown>" : ((method.DeclaringType == null ? "<null>" : method.DeclaringType.FullName) + "." + method.Name);
                    PlayerHumanResidualAttribution093T14.RegisterProbe(stage, fullName);

                    Label callLabel = generator.DefineLabel();
                    CodeInstruction activeCheck = new CodeInstruction(OpCodes.Ldsfld, ActiveField);
                    MoveLabels(ci, activeCheck);
                    ci.labels.Add(callLabel);

                    output.Add(activeCheck);
                    output.Add(new CodeInstruction(OpCodes.Brfalse_S, callLabel));
                    output.Add(new CodeInstruction(OpCodes.Ldc_I4, stage));
                    output.Add(new CodeInstruction(OpCodes.Call, StartStageMethod));
                    output.Add(ci);

                    Label afterEnd = generator.DefineLabel();
                    output.Add(new CodeInstruction(OpCodes.Ldsfld, ActiveField));
                    output.Add(new CodeInstruction(OpCodes.Brfalse_S, afterEnd));
                    output.Add(new CodeInstruction(OpCodes.Ldc_I4, stage));
                    output.Add(new CodeInstruction(OpCodes.Call, EndStageMethod));
                    CodeInstruction nop = new CodeInstruction(OpCodes.Nop);
                    nop.labels.Add(afterEnd);
                    output.Add(nop);
                    continue;
                }

                if (ci.opcode == OpCodes.Ret && (ci.blocks == null || ci.blocks.Count == 0))
                {
                    Label retLabel = generator.DefineLabel();
                    CodeInstruction activeCheck = new CodeInstruction(OpCodes.Ldsfld, ActiveField);
                    MoveLabels(ci, activeCheck);
                    ci.labels.Add(retLabel);
                    output.Add(activeCheck);
                    output.Add(new CodeInstruction(OpCodes.Brfalse_S, retLabel));
                    output.Add(new CodeInstruction(OpCodes.Call, OriginalEndMethod));
                    output.Add(ci);
                    continue;
                }

                output.Add(ci);
            }

            PlayerHumanResidualAttribution093T14.SetTranspilerShape(returns, skippedEh);
            return output;
        }

        private static int Classify(MethodInfo method)
        {
            if (method == null || method.DeclaringType == null) return -1;
            string type = method.DeclaringType.FullName ?? string.Empty;
            string name = method.Name ?? string.Empty;

            if (type == "Verse.ThingWithComps" && (name == "Tick" || name.StartsWith("TickInterval", StringComparison.Ordinal)))
                return (int)PlayerHumanResidualStage093T14.BaseComps;

            if (type.IndexOf("Pawn_JobTracker", StringComparison.Ordinal) >= 0 || type.IndexOf("Pawn_PathFollower", StringComparison.Ordinal) >= 0)
                return -1;

            if (name.IndexOf("Tick", StringComparison.OrdinalIgnoreCase) < 0) return -1;

            if (type.IndexOf("Pawn_HealthTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.Health;
            if (type.IndexOf("Pawn_NeedsTracker", StringComparison.Ordinal) >= 0 || type.IndexOf("Pawn_MindState", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.NeedsMind;
            if (type.IndexOf("Pawn_StanceTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.Stance;
            if (type.IndexOf("Pawn_EquipmentTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_ApparelTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_InventoryTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.GearInventory;
            if (type.IndexOf("Pawn_AbilityTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_GeneTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_PsychicEntropyTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_RoyaltyTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.AbilityGene;
            if (type.IndexOf("Pawn_RelationsTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_InteractionsTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_GuestTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.Social;

            if ((type.StartsWith("Verse.Pawn_", StringComparison.Ordinal) || type.StartsWith("RimWorld.Pawn_", StringComparison.Ordinal)) &&
                type.IndexOf("PathFollower", StringComparison.Ordinal) < 0 && type.IndexOf("JobTracker", StringComparison.Ordinal) < 0)
                return (int)PlayerHumanResidualStage093T14.OtherTracker;

            return -1;
        }

        private static void MoveLabels(CodeInstruction from, CodeInstruction to)
        {
            if (from == null || to == null || from.labels == null || from.labels.Count == 0) return;
            to.labels.AddRange(from.labels);
            from.labels.Clear();
        }
    }
}
