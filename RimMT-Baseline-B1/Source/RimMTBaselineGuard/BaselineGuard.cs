using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMTBaselineGuard
{
    [StaticConstructorOnStartup]
    internal static class BaselineGuard
    {
        static BaselineGuard()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Type t = AccessTools.TypeByName("RimMT.TickTailTraceTD1");
                if (t != null)
                {
                    FieldInfo capture = t.GetField("captureActive", BindingFlags.Static | BindingFlags.NonPublic);
                    if (capture != null && (bool)capture.GetValue(null))
                    {
                        MethodInfo stop = t.GetMethod("StopCapture", BindingFlags.Static | BindingFlags.NonPublic);
                        if (stop != null) stop.Invoke(null, null);
                    }
                    FieldInfo completed = t.GetField("completed", BindingFlags.Static | BindingFlags.NonPublic);
                    FieldInfo attempted = t.GetField("installAttempted", BindingFlags.Static | BindingFlags.NonPublic);
                    if (completed != null) completed.SetValue(null, true);
                    if (attempted != null) attempted.SetValue(null, true);
                }
                Log.Message("[RimMT] V0.9.1 Baseline B1 guard active: TD1 automatic profiling disabled; production S5.1 thresholds unchanged.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Baseline B1 guard could not disable TD1: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
