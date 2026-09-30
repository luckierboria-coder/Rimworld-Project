using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.Profile;

namespace RimMT
{
    internal static class MemoryUnloadCoalescerD23
    {
        private const double DuplicateWindowMinutes = 15.0;
        private static long entryRequestTicks;
        private static int entryRequestSeen;
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
                    prefix: new HarmonyMethod(typeof(MemoryUnloadCoalescerD23), nameof(Prefix))
                    { priority = Priority.First });
                installed = 1;
                Log.Message("[RimMT] D.2.3 redundant post-load Unity asset cleanup coalescer installed (one suppression, 15-minute entry-to-play window).");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] D.2.3 asset cleanup coalescer failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool Prefix()
        {
            try
            {
                ProgramState state = Current.ProgramState;
                long now = Stopwatch.GetTimestamp();
                if (state == ProgramState.Entry)
                {
                    Volatile.Write(ref entryRequestTicks, now);
                    Volatile.Write(ref entryRequestSeen, 1);
                    return true;
                }

                if (Volatile.Read(ref entryRequestSeen) == 0 || Volatile.Read(ref duplicateSuppressed) != 0)
                    return true;

                long since = now - Volatile.Read(ref entryRequestTicks);
                double minutes = since / (double)Stopwatch.Frequency / 60.0;
                if (minutes < 0.0 || minutes > DuplicateWindowMinutes)
                    return true;

                if (state == ProgramState.MapInitializing || state == ProgramState.Playing)
                {
                    Interlocked.Exchange(ref duplicateSuppressed, 1);
                    Log.Message("[RimMT] D.2.3 skipped one redundant post-load Unity asset cleanup after a completed entry cleanup; later cleanup requests remain enabled.");
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
            return "D.2.3 asset cleanup coalescer: installed=" + (Volatile.Read(ref installed) != 0) +
                ", entryRequestSeen=" + (Volatile.Read(ref entryRequestSeen) != 0) +
                ", duplicateSuppressed=" + Interlocked.CompareExchange(ref duplicateSuppressed, 0, 0) +
                ", failures=" + Interlocked.CompareExchange(ref failures, 0, 0) +
                ", duplicateWindowMinutes=" + DuplicateWindowMinutes.ToString("F0") + ".";
        }
    }
}
