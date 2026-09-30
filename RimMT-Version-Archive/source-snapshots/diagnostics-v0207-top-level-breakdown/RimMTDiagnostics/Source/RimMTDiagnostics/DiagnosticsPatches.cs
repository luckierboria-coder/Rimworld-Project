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
        internal const string Version = "0.20.7";
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
                Patch(harmony, AccessTools.Method(typeof(TickList), "Tick"), nameof(DiagnosticsPatches.PhasePrefix), nameof(DiagnosticsPatches.TickListPostfix));
                Patch(harmony, AccessTools.Method(typeof(Map), "MapPreTick"), nameof(DiagnosticsPatches.PhasePrefix), nameof(DiagnosticsPatches.MapPrePostfix));
                Patch(harmony, AccessTools.Method(typeof(Map), "MapPostTick"), nameof(DiagnosticsPatches.MapPostPrefix), nameof(DiagnosticsPatches.MapPostPostfix));
                Patch(harmony, AccessTools.Method(typeof(World), "WorldTick"), nameof(DiagnosticsPatches.WorldPrefix), nameof(DiagnosticsPatches.WorldPostfix));
                Patch(harmony, AccessTools.Method(typeof(World), "WorldPostTick"), nameof(DiagnosticsPatches.PhasePrefix), nameof(DiagnosticsPatches.WorldPostTickPostfix));
                Patch(harmony, AccessTools.Method(typeof(Storyteller), "StorytellerTick"), nameof(DiagnosticsPatches.StorytellerPrefix), nameof(DiagnosticsPatches.StorytellerPostfix));
                PatchExact(harmony, "RimWorld.DateNotifier", "DateNotifierTick", nameof(DiagnosticsPatches.DatePostfix));
                PatchExact(harmony, "RimWorld.Scenario", "TickScenario", nameof(DiagnosticsPatches.ScenarioPostfix));
                PatchExact(harmony, "RimWorld.StoryWatcher", "StoryWatcherTick", nameof(DiagnosticsPatches.StoryWatcherPostfix));
                PatchExact(harmony, "RimWorld.GameEnder", "GameEndTick", nameof(DiagnosticsPatches.GameEndPostfix));
                PatchExact(harmony, "RimWorld.TaleManager", "TaleManagerTick", nameof(DiagnosticsPatches.TalesPostfix));
                PatchExact(harmony, "RimWorld.QuestManager", "QuestManagerTick", nameof(DiagnosticsPatches.QuestsPostfix));
                PatchExact(harmony, "RimWorld.History", "HistoryTick", nameof(DiagnosticsPatches.HistoryPostfix));
                PatchExact(harmony, "Verse.GameComponentUtility", "GameComponentTick", nameof(DiagnosticsPatches.GameComponentsPostfix));
                PatchExact(harmony, "Verse.LetterStack", "LetterStackTick", nameof(DiagnosticsPatches.LettersPostfix));
                PatchExact(harmony, "RimWorld.Autosaver", "AutosaverTick", nameof(DiagnosticsPatches.AutosaverPostfix));
                PatchExact(harmony, "RimWorld.TransportShipManager", "ShipObjectsTick", nameof(DiagnosticsPatches.TransportShipsPostfix));
                Log.Message("[RimMT Diagnostics] v" + Version + " safe top-level attribution initialized: patched=" + patched +
                    ", missing=" + missing + ". Exact vanilla tick stages only; Pawn, WorkGiver, GenClosest, Reachability, PathFinder and MapComponent override probes remain absent.");
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

        private static void PatchExact(Harmony harmony, string typeName, string methodName, string postfix)
        {
            Type type = AccessTools.TypeByName(typeName);
            Patch(harmony, type == null ? null : AccessTools.Method(type, methodName),
                nameof(DiagnosticsPatches.PhasePrefix), postfix);
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

        public static void PhasePrefix(ref long __state) { __state = DiagnosticsHub.BeginPhase(); }

        public static void TickListPostfix(TickerType ___tickType, long __state)
        {
            DiagPhase phase = ___tickType == TickerType.Rare ? DiagPhase.TickListRare :
                (___tickType == TickerType.Long ? DiagPhase.TickListLong : DiagPhase.TickListNormal);
            DiagnosticsHub.EndPhase(__state, phase);
        }
        public static void MapPrePostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.MapPreTick); }
        public static void WorldPostTickPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.WorldPostTick); }
        public static void DatePostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.DateNotifier); }
        public static void ScenarioPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Scenario); }
        public static void StoryWatcherPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.StoryWatcher); }
        public static void GameEndPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.GameEnd); }
        public static void TalesPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Tales); }
        public static void QuestsPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Quests); }
        public static void HistoryPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.History); }
        public static void GameComponentsPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.GameComponents); }
        public static void LettersPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Letters); }
        public static void AutosaverPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.Autosaver); }
        public static void TransportShipsPostfix(long __state) { DiagnosticsHub.EndPhase(__state, DiagPhase.TransportShips); }

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











