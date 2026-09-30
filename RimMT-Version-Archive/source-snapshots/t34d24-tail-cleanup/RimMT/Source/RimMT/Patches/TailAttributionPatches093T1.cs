using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>Observational top-level Tick phase patches only; never changes __result or skips originals.</summary>
    internal static class TailAttributionPatches093T1
    {
        private static int patched;
        private static int missing;
        private static string missingNames = string.Empty;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            PatchAny(harmony, nameof(TickListPostfix), "Verse.TickList:Tick");
            PatchAny(harmony, nameof(MapPrePostfix), "Verse.Map:MapPreTick");
            PatchAny(harmony, nameof(WorldPostfix), "Verse.World:WorldTick");
            PatchAny(harmony, nameof(StoryWatcherPostfix), "RimWorld.StoryWatcher:StoryWatcherTick");
            PatchAny(harmony, nameof(GameEndPostfix), "RimWorld.GameEnder:GameEndTick");
            PatchAny(harmony, nameof(StorytellerPostfix), "RimWorld.Storyteller:StorytellerTick");
            PatchAny(harmony, nameof(TalesPostfix), "RimWorld.TaleManager:TaleManagerTick");
            PatchAny(harmony, nameof(WorldPostTickPostfix), "Verse.World:WorldPostTick");
            PatchAny(harmony, nameof(MapPostPostfix), "Verse.Map:MapPostTick");
            PatchAny(harmony, nameof(HistoryPostfix), "RimWorld.History:HistoryTick", "Verse.History:HistoryTick");
            PatchAny(harmony, nameof(GameComponentsPostfix), "Verse.GameComponentUtility:GameComponentTick");
            PatchAny(harmony, nameof(AutosaverPostfix), "RimWorld.Autosaver:AutosaverTick", "Verse.Autosaver:AutosaverTick");
            PatchAny(harmony, nameof(ScenarioPostfix), "RimWorld.Scenario:TickScenario", "Verse.Scenario:TickScenario");
            PatchAny(harmony, nameof(DateNotifierPostfix), "RimWorld.DateNotifier:DateNotifierTick", "Verse.DateNotifier:DateNotifierTick");
            PatchAny(harmony, nameof(LettersPostfix), "RimWorld.LetterStack:LetterStackTick", "Verse.LetterStack:LetterStackTick");
            PatchAny(harmony, nameof(FilthPostfix), "RimWorld.FilthMonitor:FilthMonitorTick", "Verse.FilthMonitor:FilthMonitorTick");
            Log.Message("[RimMT] T1 Tail Attribution installed: patched=" + patched + ", missing=" + missing +
                (missing == 0 ? "." : ", missingTargets=" + missingNames + ".") +
                " Measurement-only top-level timing; optimizer behavior unchanged.");
        }

        private static void PatchAny(Harmony harmony, string postfixName, params string[] specs)
        {
            MethodBase target = null;
            string resolved = null;
            for (int i = 0; i < specs.Length; i++)
            {
                try { target = AccessTools.Method(specs[i]); }
                catch { target = null; }
                if (target != null) { resolved = specs[i]; break; }
            }
            if (target == null)
            {
                missing++;
                if (missingNames.Length < 512)
                {
                    if (missingNames.Length != 0) missingNames += ",";
                    missingNames += specs.Length == 0 ? "<empty>" : specs[0];
                }
                return;
            }

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(TailAttributionPatches093T1), nameof(PhasePrefix)) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(TailAttributionPatches093T1), postfixName) { priority = Priority.Last };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                patched++;
            }
            catch (Exception ex)
            {
                missing++;
                Log.Warning("[RimMT] T1 attribution target failed closed: " + resolved + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PhasePrefix(ref long __state) { __state = TailAttribution093T1.BeginPhase(); }
        public static void TickListPostfix(long __state) { TailAttribution093T1.EndTickList(__state); }
        public static void MapPrePostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.MapPreTick); }
        public static void WorldPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.WorldTick); }
        public static void StoryWatcherPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.StoryWatcher); }
        public static void GameEndPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.GameEnd); }
        public static void StorytellerPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Storyteller); }
        public static void TalesPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Tales); }
        public static void WorldPostTickPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.WorldPostTick); }
        public static void MapPostPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.MapPostTick); }
        public static void HistoryPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.History); }
        public static void GameComponentsPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.GameComponents); }
        public static void AutosaverPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Autosaver); }
        public static void ScenarioPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Scenario); }
        public static void DateNotifierPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.DateNotifier); }
        public static void LettersPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Letters); }
        public static void FilthPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Filth); }
    }
}
