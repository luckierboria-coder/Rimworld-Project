using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Allen.FeedBabiesFaster15
{
    [StaticConstructorOnStartup]
    public static class FeedBabiesFaster15Bootstrap
    {
        static FeedBabiesFaster15Bootstrap()
        {
            new Harmony("allen.feedbabiesfaster15").PatchAll();
        }
    }

    internal static class FeedSpeedTranspiler
    {
        internal const float VanillaDivisor = 5000f;
        internal const float FastDivisor = 500f;

        internal static IEnumerable<CodeInstruction> ReplaceFeedDivisor(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4
                    && instruction.operand is float
                    && Math.Abs((float)instruction.operand - VanillaDivisor) < 0.001f)
                {
                    instruction.operand = FastDivisor;
                }

                yield return instruction;
            }
        }
    }

    // Vanilla 1.5:
    // baby food gained per tick = Min(MaxFood / 5000f, NutritionWanted).
    // The original 1.6 mod replaces 5000 with 500, making breastfeeding 10x faster.
    [HarmonyPatch(typeof(ChildcareUtility), "SuckleFromLactatingPawn")]
    internal static class SuckleFromLactatingPawn_DivisorPatch
    {
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return FeedSpeedTranspiler.ReplaceFeedDivisor(instructions);
        }
    }

    // In vanilla this constant lives inside the compiler-generated tickAction
    // lambda created by FeedBabyFoodFromInventory(), not necessarily in the
    // parent method itself. Method numbering changes between RimWorld builds,
    // so discover it by semantic name instead of hard-coding b__15_1 etc.
    [HarmonyPatch]
    internal static class FeedBabyFoodFromInventory_DivisorPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(JobDriver_BottleFeedBaby).GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name.IndexOf("FeedBabyFoodFromInventory", StringComparison.Ordinal) >= 0)
                {
                    yield return method;
                }
            }
        }

        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return FeedSpeedTranspiler.ReplaceFeedDivisor(instructions);
        }
    }
}
