using HarmonyLib;
using RimWorld;
using System;
using Verse;
using Verse.AI;

namespace Allen.SmarterRaiderAI.Patch15
{
    public sealed class AllenSraiPatchMod : Mod
    {
        private static Harmony harmony;

        public AllenSraiPatchMod(ModContentPack content) : base(content)
        {
            harmony = new Harmony("allen.smarterraiderai.patch15");

            ReplacePogoPrefix(typeof(AvoidGrid), "Regenerate", typeof(AvoidGridReplacement), nameof(AvoidGridReplacement.Prefix));
            ReplacePogoPrefix(typeof(JobGiver_AITrashBuildingsDistant), "TryGiveJob", typeof(JobGiverTrashReplacement), nameof(JobGiverTrashReplacement.Prefix));
            ReplacePogoPrefix(typeof(JobGiver_AISapper), "TryGiveJob", typeof(JobGiverSapperReplacement), nameof(JobGiverSapperReplacement.Prefix));

            Log.Message("[Smarter Raider AI - Allen Patch 1.5] loaded; original PogoAI.dll left untouched.");
        }

        private static void ReplacePogoPrefix(Type targetType, string targetMethodName, Type replacementType, string replacementMethodName)
        {
            var target = AccessTools.Method(targetType, targetMethodName);
            if (target == null)
            {
                throw new MissingMethodException(targetType.FullName, targetMethodName);
            }

            harmony.Unpatch(target, HarmonyPatchType.Prefix, "pogo.ai");

            var replacement = AccessTools.Method(replacementType, replacementMethodName);
            if (replacement == null)
            {
                throw new MissingMethodException(replacementType.FullName, replacementMethodName);
            }

            var prefix = new HarmonyMethod(replacement)
            {
                priority = Priority.First
            };
            harmony.Patch(target, prefix: prefix);
        }
    }
}
