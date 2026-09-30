using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTCleanPathfindingD22A1
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.rimmt.cleanpathfinding.d22a1";
        private static long finishFrameOnCell;
        private static long unwalkableEndpointChangedToTouch;
        private static long failures;
        private static bool installed;
        private static bool diagnosticsInstalled;

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo startPath = AccessTools.Method(typeof(Pawn_PathFollower), "StartPath");
                if (startPath == null)
                {
                    Log.Warning("[RimMT Path Endpoint Compatibility A1.1] Pawn_PathFollower.StartPath was not found; patch remains inert.");
                    return;
                }

                harmony.Patch(startPath, prefix: new HarmonyMethod(typeof(Bootstrap), nameof(StartPathPrefix))
                {
                    priority = Priority.First
                });
                installed = true;
                TryPatchDiagnostics(harmony);
                Log.Message("[RimMT Path Endpoint Compatibility A1.1] FinishFrame unwalkable OnCell endpoints change to Touch once at StartPath. The former per-cell glow Harmony cache is retired.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT Path Endpoint Compatibility A1.1] Installation failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void StartPathPrefix(Pawn ___pawn, LocalTargetInfo dest, ref PathEndMode peMode)
        {
            try
            {
                if (___pawn == null || peMode != PathEndMode.OnCell)
                    return;

                Job job = ___pawn.CurJob;
                if (job == null || job.def != JobDefOf.FinishFrame || !(dest.Thing is Frame))
                    return;

                Interlocked.Increment(ref finishFrameOnCell);
                Map map = ___pawn.Map;
                IntVec3 cell = dest.Cell;
                if (map == null || !cell.IsValid || cell.Walkable(map))
                    return;

                peMode = PathEndMode.Touch;
                Interlocked.Increment(ref unwalkableEndpointChangedToTouch);
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
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
            return "RimMT Path Endpoint Compatibility A1.1: installed=" + installed +
                ", diagnostics=" + diagnosticsInstalled +
                ", finishFrameOnCell=" + Interlocked.Read(ref finishFrameOnCell) +
                ", unwalkableEndpointChangedToTouch=" + Interlocked.Read(ref unwalkableEndpointChangedToTouch) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". No per-cell path-cost Harmony patches are installed.";
        }
    }
}
