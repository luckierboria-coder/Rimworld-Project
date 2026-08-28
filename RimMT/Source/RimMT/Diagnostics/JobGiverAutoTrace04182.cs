using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    [StaticConstructorOnStartup]
    internal static class JobGiverAutoTrace04182
    {
        private const string HarmonyId = "allen.rimmt.jobgiver.autotrace04182";
        private static readonly long TriggerTicks = Math.Max(1L, Stopwatch.Frequency * 64L / 1000L);
        private static int pending;
        private static int triggered;
        private static long triggerElapsedTicks;

        static JobGiverAutoTrace04182()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodBase jobPackage = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                MethodBase rootUpdate = AccessTools.Method(typeof(Root_Play), "Update");
                if (jobPackage == null || rootUpdate == null)
                {
                    Log.Warning("[RimMT] JobGiver auto-trace JT1 unavailable: required RimWorld 1.5 target missing.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverAutoTrace04182), nameof(JobPrefix)) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(JobGiverAutoTrace04182), nameof(JobPostfix)) { priority = Priority.Last };
                HarmonyMethod rootPostfix = new HarmonyMethod(typeof(JobGiverAutoTrace04182), nameof(RootUpdatePostfix)) { priority = Priority.Last };
                harmony.Patch(jobPackage, prefix: prefix, postfix: postfix);
                harmony.Patch(rootUpdate, postfix: rootPostfix);

                Log.Message("[RimMT] JT1 JobGiver auto-trace armed. First TryIssueJobPackage >=64ms requests one bounded V0.4.8 detail-capture session on the following Root_Play.Update boundary; temporary detail patches then auto-unpatch.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] JT1 JobGiver auto-trace patch failed; base V0.4.18.2 remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void JobPrefix(ref long __state)
        {
            if (Volatile.Read(ref triggered) != 0 || WorkGiverDetailPatches.CaptureActive || !RimMTThreadGuard.IsMainThread)
            {
                __state = 0L;
                return;
            }
            __state = Stopwatch.GetTimestamp();
        }

        public static void JobPostfix(long __state)
        {
            if (__state == 0L || Volatile.Read(ref triggered) != 0)
                return;

            long elapsed = Stopwatch.GetTimestamp() - __state;
            if (elapsed < TriggerTicks)
                return;

            if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
            {
                Interlocked.Exchange(ref triggerElapsedTicks, elapsed);
                Log.Message("[RimMT] JT1 observed slow JobGiver package " + (elapsed * 1000.0 / Stopwatch.Frequency).ToString("F3") + "ms; detail capture will arm at the next frame boundary.");
            }
        }

        public static void RootUpdatePostfix()
        {
            if (!RimMTThreadGuard.IsMainThread || Volatile.Read(ref triggered) != 0)
                return;
            if (Interlocked.Exchange(ref pending, 0) == 0)
                return;

            if (WorkGiverDetailPatches.StartCapture())
            {
                Interlocked.Exchange(ref triggered, 1);
                long elapsed = Interlocked.Read(ref triggerElapsedTicks);
                Log.Message("[RimMT] JT1 detail capture started after a " + (elapsed * 1000.0 / Stopwatch.Frequency).ToString("F3") + "ms trigger. This is a one-shot diagnostic session; no further auto-captures will be armed this run.");
            }
            else
            {
                // StartCapture can fail transiently if another capture is already active. Retry only
                // when no capture is active; otherwise that existing session already provides data.
                if (WorkGiverDetailPatches.CaptureActive)
                    Interlocked.Exchange(ref triggered, 1);
                else
                    Interlocked.Exchange(ref pending, 1);
            }
        }
    }
}
