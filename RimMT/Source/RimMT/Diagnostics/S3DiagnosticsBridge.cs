using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    // Diagnostic-only bridge so S3 telemetry is emitted after the existing report without changing
    // the stable diagnostics body. These postfixes run only when a startup/runtime report is requested.
    internal static class S3DiagnosticsBridge
    {
        private static bool patched;
        private static int failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;
            try
            {
                MethodInfo postfix = AccessTools.Method(typeof(S3DiagnosticsBridge), nameof(Postfix));
                MethodBase startup = AccessTools.Method(typeof(RimMTDiagnostics), nameof(RimMTDiagnostics.LogStartupReport));
                MethodBase runtime = AccessTools.Method(typeof(RimMTDiagnostics), nameof(RimMTDiagnostics.LogRuntimeReport));
                if (startup != null)
                    harmony.Patch(startup, postfix: new HarmonyMethod(postfix) { priority = Priority.Last - 200 });
                if (runtime != null)
                    harmony.Patch(runtime, postfix: new HarmonyMethod(postfix) { priority = Priority.Last - 200 });
                patched = startup != null || runtime != null;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warning("[RimMT] S3 diagnostics bridge failed; gameplay is unaffected. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Postfix()
        {
            Log.Message("[RimMT] S3 learned-admission supplemental report: " + JobGiverLearnedAdmission0419S3.Summary() +
                " DiagnosticsBridge[patched=" + patched + ", failures=" + failures + "]");
        }
    }
}
