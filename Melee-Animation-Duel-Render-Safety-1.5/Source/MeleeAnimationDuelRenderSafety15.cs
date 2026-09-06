using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Allen.MeleeAnimationDuelRenderSafety15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony("allen.meleeanimation.duelrendersafety15");
                harmony.PatchAll();
                Log.Message("[MA Duel Render Safety 1.5] ACTIVE: PawnDrawParms.pawn repair + null weapon DrawColor guard installed.");
            }
            catch (Exception ex)
            {
                Log.Error("[MA Duel Render Safety 1.5] Failed to install: " + ex);
            }
        }
    }

    /// <summary>
    /// Melee Animation's special animation render path rebuilds PawnDrawParms before
    /// calling PawnRenderTree.ParallelPreDraw(). Some third-party render postfixes
    /// assume parms.pawn is non-null. Restore the invariant after MA has built its
    /// special parms and before ParallelPreDraw consumes them.
    /// </summary>
    [HarmonyPatch]
    internal static class MakeDrawArgsPawnGuard
    {
        private static bool warned;

        private static MethodBase FindTarget()
        {
            Type type = AccessTools.TypeByName("AM.Patches.Patch_PawnRenderer_RenderPawnAt");
            if (type == null)
                return null;

            return AccessTools.GetDeclaredMethods(type)
                .FirstOrDefault(m => m.Name == "MakeDrawArgs" && m.GetParameters().Length == 3);
        }

        public static MethodBase TargetMethod() => FindTarget();

        public static bool Prepare()
        {
            if (FindTarget() != null)
                return true;

            Log.Error("[MA Duel Render Safety 1.5] Could not find Melee Animation MakeDrawArgs; PawnDrawParms guard not applied.");
            return false;
        }

        public static void Postfix(Pawn pawn, ref PawnDrawParms parms)
        {
            if (pawn == null || parms.pawn != null)
                return;

            parms.pawn = pawn;
            if (!warned)
            {
                warned = true;
                Log.Warning("[MA Duel Render Safety 1.5] Repaired a null PawnDrawParms.pawn during Melee Animation rendering. Further repairs are silent.");
            }
        }
    }

    /// <summary>
    /// MA 1.5 uses ov.Weapon.DrawColor in AnimRenderer.Draw without a null check.
    /// A transient/missing animation weapon reference should not abort every render
    /// frame. Replace Thing.DrawColor calls inside this one MA method with a safe
    /// equivalent. Normal non-null weapons are bit-for-bit behaviorally identical.
    /// </summary>
    [HarmonyPatch]
    internal static class AnimRendererDrawWeaponGuard
    {
        private static bool warned;

        private static MethodBase FindTarget()
        {
            Type type = AccessTools.TypeByName("AM.AnimRenderer");
            if (type == null)
                return null;

            // Historical/current 1.5 MA has one instance Draw method with five args.
            return AccessTools.GetDeclaredMethods(type)
                .FirstOrDefault(m => m.Name == "Draw" && !m.IsStatic && m.GetParameters().Length == 5);
        }

        public static MethodBase TargetMethod() => FindTarget();

        public static bool Prepare()
        {
            if (FindTarget() != null)
                return true;

            Log.Error("[MA Duel Render Safety 1.5] Could not find AM.AnimRenderer.Draw; weapon DrawColor guard not applied.");
            return false;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawColor));
            MethodInfo safe = AccessTools.Method(typeof(AnimRendererDrawWeaponGuard), nameof(SafeDrawColor));
            int replaced = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                if (original != null && instruction.Calls(original))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = safe;
                    replaced++;
                }
                yield return instruction;
            }

            if (replaced == 0)
                Log.Warning("[MA Duel Render Safety 1.5] No Thing.DrawColor call was found in AM.AnimRenderer.Draw; MA build may differ. Nothing was rewritten.");
            else
                Log.Message("[MA Duel Render Safety 1.5] Guarded " + replaced + " Thing.DrawColor call(s) in AM.AnimRenderer.Draw.");
        }

        public static Color SafeDrawColor(Thing thing)
        {
            if (thing != null)
                return thing.DrawColor;

            if (!warned)
            {
                warned = true;
                Log.Warning("[MA Duel Render Safety 1.5] Melee Animation attempted to read DrawColor from a null animation weapon. Using white tint; further occurrences are silent.");
            }
            return Color.white;
        }
    }
}
