using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// One measurement-only postfix on Pawn_JobTracker.DetermineNextJob.
    /// Reads the final ThinkResult only; never reruns think logic or changes the result.
    /// </summary>
    internal static class WaitStallPatches093T27_3
    {
        private static bool installed;
        private static int failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase target = AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob");
                if (target == null)
                {
                    failures++;
                    return;
                }
                harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(WaitStallPatches093T27_3), nameof(Postfix))
                    { priority = Priority.Last - 50 });
                installed = true;
            }
            catch (Exception ex)
            {
                failures++;
                installed = false;
                Log.Warning("[RimMT] T27.3 Wait-stall tracer failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Postfix(Pawn_JobTracker __instance, ThinkResult __result)
        {
            WaitStallTrace093T27_3.Observe(__instance, __result);
        }

        internal static string Summary()
        {
            return "T27.3 Wait-stall patch: installed=" + installed + ", failures=" + failures +
                ". Measurement-only final ThinkResult postfix; no ThinkTree rerun or gameplay mutation.";
        }
    }
}
