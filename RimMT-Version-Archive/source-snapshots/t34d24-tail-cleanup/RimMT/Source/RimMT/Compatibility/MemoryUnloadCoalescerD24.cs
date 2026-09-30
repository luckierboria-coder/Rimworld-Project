using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.Profile;

namespace RimMT
{
    internal static class MemoryUnloadCoalescerD24
    {
        private const double DuplicateWindowMinutes = 15.0;
        private static long loadBoundaryTicks;
        private static int loadBoundaryArmed;
        private static int firstLoadCleanupSeen;
        private static int duplicateSuppressed;
        private static int installed;
        private static int failures;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(MemoryUtility), nameof(MemoryUtility.UnloadUnusedUnityAssets));
                if (target == null)
                    throw new MissingMethodException(typeof(MemoryUtility).FullName, nameof(MemoryUtility.UnloadUnusedUnityAssets));

                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(MemoryUnloadCoalescerD24), nameof(Prefix))
                    { priority = Priority.First });

                HarmonyMethod loadPrefix = new HarmonyMethod(
                    typeof(MemoryUnloadCoalescerD24), nameof(LoadBoundaryPrefix))
                    { priority = Priority.First };
                MethodBase loadGame = AccessTools.Method(typeof(Game), nameof(Game.LoadGame));
                MethodBase newGame = AccessTools.Method(typeof(Game), nameof(Game.InitNewGame));
                if (loadGame == null || newGame == null)
                    throw new MissingMethodException(typeof(Game).FullName, "LoadGame/InitNewGame");
                harmony.Patch(loadGame, prefix: loadPrefix);
                harmony.Patch(newGame, prefix: loadPrefix);
                installed = 1;
                Log.Message("[RimMT] D.2.4 load-boundary Unity asset cleanup coalescer installed (first load cleanup allowed, one following cleanup suppressed within 15 minutes).");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] D.2.4 asset cleanup coalescer failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void LoadBoundaryPrefix()
        {
            Volatile.Write(ref loadBoundaryTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref firstLoadCleanupSeen, 0);
            Volatile.Write(ref duplicateSuppressed, 0);
            Volatile.Write(ref loadBoundaryArmed, 1);
        }

        public static bool Prefix()
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                // Game.LoadGame/InitNewGame calls this once near its entry. D.2.3 tried to infer
                // that call from ProgramState.Entry, which is not reliable in a heavily patched
                // long-event pipeline. The exact Game method prefix above supplies the boundary.
                if (Volatile.Read(ref loadBoundaryArmed) == 0)
                    return true;

                if (Volatile.Read(ref firstLoadCleanupSeen) == 0)
                {
                    Volatile.Write(ref firstLoadCleanupSeen, 1);
                    return true;
                }

                if (Volatile.Read(ref duplicateSuppressed) != 0)
                    return true;

                long since = now - Volatile.Read(ref loadBoundaryTicks);
                double minutes = since / (double)Stopwatch.Frequency / 60.0;
                if (minutes < 0.0 || minutes > DuplicateWindowMinutes)
                    return true;

                ProgramState state = Current.ProgramState;
                if (state == ProgramState.MapInitializing || state == ProgramState.Playing)
                {
                    Interlocked.Exchange(ref duplicateSuppressed, 1);
                    Log.Message("[RimMT] D.2.4 skipped one redundant Unity asset cleanup following the load-boundary cleanup; later cleanup requests remain enabled.");
                    return false;
                }
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
            return true;
        }

        internal static string Summary()
        {
            return "D.2.4 asset cleanup coalescer: installed=" + (Volatile.Read(ref installed) != 0) +
                ", loadBoundaryArmed=" + (Volatile.Read(ref loadBoundaryArmed) != 0) +
                ", firstLoadCleanupSeen=" + (Volatile.Read(ref firstLoadCleanupSeen) != 0) +
                ", duplicateSuppressed=" + Interlocked.CompareExchange(ref duplicateSuppressed, 0, 0) +
                ", failures=" + Interlocked.CompareExchange(ref failures, 0, 0) +
                ", duplicateWindowMinutes=" + DuplicateWindowMinutes.ToString("F0") + ".";
        }
    }
}
