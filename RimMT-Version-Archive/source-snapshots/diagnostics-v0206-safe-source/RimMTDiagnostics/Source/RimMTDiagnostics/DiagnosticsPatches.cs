using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    [StaticConstructorOnStartup]
    internal static class DiagnosticsBootstrap
    {
        internal const string HarmonyId = "allen.rimmt.diagnostics";
        internal const string Version = "0.20.6";
        private static int patched;
        private static int missing;

        static DiagnosticsBootstrap()
        {
            try
            {
                RimMTDiagnosticsSettings.SampleEveryTicks = 1;
                RimMTDiagnosticsSettings.EnableWaitTrace = false;
                RimMTDiagnosticsSettings.EnableSearchTiming = false;
                Harmony harmony = new Harmony(HarmonyId);
                Patch(harmony, AccessTools.Method(typeof(TickManager), "DoSingleTick"), nameof(DiagnosticsPatches.TickPrefix), nameof(DiagnosticsPatches.TickPostfix));
                Patch(harmony, AccessTools.Method(typeof(Map), "MapPostTick"), nameof(DiagnosticsPatches.MapPostPrefix), nameof(DiagnosticsPatches.MapPostPostfix));
                Patch(harmony, AccessTools.Method(typeof(World), "WorldTick"), nameof(DiagnosticsPatches.WorldPrefix), nameof(DiagnosticsPatches.WorldPostfix));
                Patch(harmony, AccessTools.Method(typeof(Storyteller), "StorytellerTick"), nameof(DiagnosticsPatches.StorytellerPrefix), nameof(DiagnosticsPatches.StorytellerPostfix));
                Log.Message("[RimMT Diagnostics] v" + Version + " safe top-level attribution initialized: patched=" + patched +
                    ", missing=" + missing + ". Pawn, WorkGiver, GenClosest, Reachability, PathFinder and MapComponent override probes remain absent.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT Diagnostics] bootstrap failed: " + ex);
            }
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            if (target == null) { missing++; return; }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(DiagnosticsPatches), prefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(DiagnosticsPatches), postfix) { priority = Priority.Last });
                patched++;
            }
            catch (Exception ex)
            {
                missing++;
                Log.Warning("[RimMT Diagnostics] patch failed for " + target.DeclaringType.FullName + "." + target.Name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchNamedMethods(Harmony harmony, Type type, string name, string prefix, string postfix)
        {
            List<MethodInfo> methods;
            try { methods = AccessTools.GetDeclaredMethods(type); }
            catch { missing++; return; }
            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo method = methods[i];
                if (method != null && method.Name == name) Patch(harmony, method, prefix, postfix);
            }
        }
    }

    internal static class DiagnosticsPatches
    {
        public static void TickPrefix()
        {
            DiagnosticsHub.BeginTick();
        }
        public static void TickPostfix()
        {
            DiagnosticsHub.EndTick();
        }

        public static void PawnPrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }
        public static void PawnPostfix(Pawn __instance, long __state) { DiagnosticsHub.EndPawn(__state, __instance); }

        public static void JobTrackerPrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }
        public static void JobTrackerPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.JobTracker); }

        public static void DeterminePrefix(Pawn_JobTracker __instance, ref long __state)
        {
            DiagnosticsV03.BeginDetermine(__instance);
            __state = DiagnosticsHub.BeginPhase();
        }
        public static void DeterminePostfix(Pawn_JobTracker __instance, ThinkResult __result, long __state)
        {
            DiagnosticsHub.EndPhase(__state, DiagPhase.DetermineNextJob);
            DiagnosticsHub.ObserveDetermine(__instance, __result);
            DiagnosticsV03.EndDetermine(__instance, __result);
        }

        public static void PatherPrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }
        public static void PatherPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Pather); }

        public static void GenClosestPrefix(ref long __state)
        {
            __state = RimMTDiagnosticsSettings.EnableSearchTiming ? DiagnosticsHub.BeginPhase() : 0L;
        }
        public static void GenClosestPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.GenClosest); }

        public static void ReachPrefix(ref long __state)
        {
            __state = RimMTDiagnosticsSettings.EnableSearchTiming ? DiagnosticsHub.BeginPhase() : 0L;
        }
        public static void ReachPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Reachability); }

        public static void MapPostPrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }
        public static void MapPostPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.MapPostTick); }

        public static void WorldPrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }
        public static void WorldPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.WorldTick); }

        public static void StorytellerPrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }
        public static void StorytellerPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Storyteller); }
    }
}











