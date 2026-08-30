using System;
using HarmonyLib;
using Verse;

namespace RimMT
{
    [StaticConstructorOnStartup]
    internal static class TickTailTraceTD1ReportBridge
    {
        static TickTailTraceTD1ReportBridge()
        {
            try
            {
                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                var target = AccessTools.Method(typeof(RimMTDiagnostics), nameof(RimMTDiagnostics.LogRuntimeReport));
                if (target == null)
                {
                    Log.Warning("[RimMT] TD1 report bridge could not find RimMTDiagnostics.LogRuntimeReport.");
                    return;
                }
                HarmonyMethod postfix = new HarmonyMethod(typeof(TickTailTraceTD1ReportBridge), nameof(ReportPostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(target, postfix: postfix);
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] TD1 report bridge failed; capture remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] TD1 supplemental report:\n" + TickTailTraceTD1.Summary());
        }
    }
}
