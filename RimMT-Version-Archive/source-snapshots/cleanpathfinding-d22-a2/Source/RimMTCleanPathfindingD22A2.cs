using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTCleanPathfindingD22A2
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.rimmt.cleanpathfinding.d22a2";
        private static long transpilerPasses;
        private static long transpilerReplacements;
        private static long failures;
        private static bool installed;
        private static bool diagnosticsInstalled;

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo makeNewToils = AccessTools.Method(typeof(JobDriver_ConstructFinishFrame), "MakeNewToils");
                MethodInfo moveNext = makeNewToils == null ? null : AccessTools.EnumeratorMoveNext(makeNewToils);
                if (moveNext == null)
                {
                    Log.Warning("[RimMT Clean Pathfinding A2.4 Endpoint Only] FinishFrame iterator was not found; patch remains inert.");
                    return;
                }

                harmony.Patch(moveNext, transpiler: new HarmonyMethod(
                    typeof(Bootstrap), nameof(FinishFrameEndpointTranspiler))
                {
                    priority = Priority.Last
                });
                installed = true;
                TryPatchDiagnostics(harmony);
                Log.Message("[RimMT Clean Pathfinding A2.4 Endpoint Only] FinishFrame attachment Toils use Touch consistently. PathFinder cost kernel, glow/terrain caches, room queue, per-cell telemetry, and per-frame patches are not installed.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT Clean Pathfinding A2.4 Endpoint Only] Installation failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static IEnumerable<CodeInstruction> FinishFrameEndpointTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            Interlocked.Increment(ref transpilerPasses);
            MethodInfo attachmentGetter = AccessTools.PropertyGetter(
                typeof(JobDriver_ConstructFinishFrame), "IsBuildingAttachment");
            MethodInfo replacement = AccessTools.Method(typeof(Bootstrap), nameof(UseTouchEndpoint));

            foreach (CodeInstruction code in instructions)
            {
                if (attachmentGetter != null && replacement != null && code.Calls(attachmentGetter))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = replacement;
                    Interlocked.Increment(ref transpilerReplacements);
                }
                yield return code;
            }
        }

        public static bool UseTouchEndpoint(JobDriver_ConstructFinishFrame driver)
        {
            return false;
        }

        private static void TryPatchDiagnostics(Harmony harmony)
        {
            Type report = AccessTools.TypeByName("RimMT.Diagnostics.DiagnosticReport");
            MethodInfo build = report == null ? null : AccessTools.Method(report, "Build", Type.EmptyTypes);
            if (build == null || build.ReturnType != typeof(string)) return;
            harmony.Patch(build, postfix: new HarmonyMethod(typeof(Bootstrap), nameof(DiagnosticsPostfix))
            {
                priority = Priority.Last
            });
            diagnosticsInstalled = true;
        }

        public static void DiagnosticsPostfix(ref string __result)
        {
            __result = (__result ?? string.Empty) + Environment.NewLine + Summary() + Environment.NewLine;
        }

        public static string Summary()
        {
            return "RimMT Clean Pathfinding A2.4 Endpoint Only: installed=" + installed +
                ", diagnostics=" + diagnosticsInstalled +
                ", transpilerPasses=" + Interlocked.Read(ref transpilerPasses) +
                ", replacements=" + Interlocked.Read(ref transpilerReplacements) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". PathFinder/Root_Play patches=NONE; runtime hot-path work=NONE.";
        }
    }
}
