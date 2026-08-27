using System;
using System.Threading;
using HarmonyLib;
using Verse;

namespace ShangRuo.VSIEWorkTimeNullKeyHotfix
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        private const string HarmonyId = "shangruo.vsie.worktime.nullkey.hotfix";
        private const string TargetError =
            "Null key while loading dictionary of Verse.Pawn and VanillaSocialInteractionsExpanded.WorkTime. label=workersWithWorkingTicks";
        private static int suppressedCount;

        static Bootstrap()
        {
            Harmony harmony = new Harmony(HarmonyId);
            harmony.Patch(
                AccessTools.Method(typeof(Log), "Error", new Type[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(Bootstrap), "LogErrorPrefix"));
            harmony.Patch(
                AccessTools.Method(typeof(Game), "FinalizeInit"),
                postfix: new HarmonyMethod(typeof(Bootstrap), "GameFinalizeInitPostfix"));
            Log.Message("[VSIE 1.5 WorkTime Null-Key Hotfix] Installed exact null-key log guard.");
        }

        public static bool LogErrorPrefix(string text)
        {
            if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs &&
                text != null &&
                text.StartsWith(TargetError, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref suppressedCount);
                return false;
            }
            return true;
        }

        public static void GameFinalizeInitPostfix()
        {
            int count = Interlocked.Exchange(ref suppressedCount, 0);
            if (count > 0)
            {
                Log.Message(
                    "[VSIE 1.5 WorkTime Null-Key Hotfix] RimWorld skipped " +
                    count +
                    " invalid VSIE WorkTime Pawn reference(s) while loading. " +
                    "The repeated red error lines were suppressed; valid entries were untouched.");
            }
        }
    }
}
