using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace ToddlersToyMoteHotfix15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                new Harmony("allen.toddlers.toymotehotfix15").PatchAll();
                Log.Message("[Toddlers Toy Mote Hotfix 1.5] active: null toy motes are skipped.");
            }
            catch (Exception ex)
            {
                Log.Error("[Toddlers Toy Mote Hotfix 1.5] failed to initialize: " + ex);
            }
        }
    }

    [HarmonyPatch]
    internal static class ToddlerPlayToysTickPatch
    {
        private static MethodBase TargetMethod()
        {
            Type driver = AccessTools.TypeByName("Toddlers.JobDriver_ToddlerPlayToys");
            return driver == null ? null : AccessTools.Method(driver, "<PlayToil>b__2_1");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo maintain = AccessTools.Method(typeof(Mote), nameof(Mote.Maintain));
            MethodInfo safeMaintain = AccessTools.Method(typeof(ToddlerPlayToysTickPatch), nameof(SafeMaintain));
            bool replaced = false;

            foreach (CodeInstruction instruction in instructions)
            {
                if (!replaced && instruction.Calls(maintain))
                {
                    CodeInstruction replacement = new CodeInstruction(OpCodes.Call, safeMaintain);
                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    replaced = true;
                    yield return replacement;
                }
                else
                {
                    yield return instruction;
                }
            }

            if (!replaced)
                Log.Error("[Toddlers Toy Mote Hotfix 1.5] target Mote.Maintain call was not found; patch is inert.");
        }

        private static void SafeMaintain(Mote mote)
        {
            if (mote != null)
                mote.Maintain();
        }
    }
}
