using System;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T26 engine boundary.  This does not move Verse.TickManager off Unity's main thread.
    /// Instead every outer DoSingleTick receives a monotonically increasing simulation epoch.
    /// Worker packets published by T26 must carry this epoch and are consumable only while the
    /// matching synchronous game tick/package is still active.
    ///
    /// This mirrors the useful part of SimplyMoreFPS' native worker contract: main-thread-owned
    /// state capture, POD-like packets, explicit epoch/sequence validation, and stale-result drop.
    /// </summary>
    internal static class SimulationEpochCoordinator093T26
    {
        internal const string FeatureId = "runtime.simulationEpoch";

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static long currentEpoch;
        [ThreadStatic] private static int currentGameTick;

        private static long epochSerial;
        private static long ticksEntered;
        private static long ticksCompleted;
        private static long nestedEntries;
        private static long exceptionsSeen;
        private static int installed;
        private static int installFailures;

        internal static long CurrentEpoch { get { return currentEpoch; } }
        internal static int CurrentGameTick { get { return currentGameTick; } }
        internal static bool InSimulationTick { get { return currentEpoch != 0L && depth > 0; } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase target = AccessTools.Method(typeof(TickManager), "DoSingleTick", Type.EmptyTypes);
                if (target == null)
                {
                    FeatureGate.Suppress(FeatureId, "TickManager.DoSingleTick() not found");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(SimulationEpochCoordinator093T26), nameof(Prefix));
                prefix.priority = Priority.First + 500;
                HarmonyMethod finalizer = new HarmonyMethod(typeof(SimulationEpochCoordinator093T26), nameof(Finalizer));
                finalizer.priority = Priority.Last - 500;
                harmony.Patch(target, prefix: prefix, finalizer: finalizer);
                Volatile.Write(ref installed, 1);
                Log.Message("[RimMT] T26 simulation epoch coordinator installed at TickManager.DoSingleTick. Unity/Verse state remains main-thread-owned; workers may consume only captured primitive packets.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref installFailures);
                FeatureGate.Suppress(FeatureId, "DoSingleTick epoch patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] T26 simulation epoch coordinator failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(ref EpochState __state)
        {
            __state = default(EpochState);
            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Outermost = depth == 0;
            depth++;
            if (!__state.Outermost)
            {
                Interlocked.Increment(ref nestedEntries);
                __state.Epoch = currentEpoch;
                return;
            }

            long epoch = Interlocked.Increment(ref epochSerial);
            if (epoch == 0L) epoch = Interlocked.Increment(ref epochSerial);
            currentEpoch = epoch;
            try { currentGameTick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { currentGameTick = -1; }
            __state.Epoch = epoch;
            __state.GameTick = currentGameTick;
            Interlocked.Increment(ref ticksEntered);
            ParallelWorkSearch093T26.OnEpochBegin(epoch, currentGameTick);
        }

        public static Exception Finalizer(Exception __exception, EpochState __state)
        {
            if (!__state.Entered) return __exception;
            if (__exception != null) Interlocked.Increment(ref exceptionsSeen);
            if (depth > 0) depth--;

            if (__state.Outermost)
            {
                try { ParallelWorkSearch093T26.OnEpochEnd(__state.Epoch, __state.GameTick); }
                catch { }
                currentEpoch = 0L;
                currentGameTick = -1;
                Interlocked.Increment(ref ticksCompleted);
            }
            return __exception;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(384);
            sb.Append("T26 simulation epoch: installed=").Append(Volatile.Read(ref installed) != 0)
              .Append(", entered/completed=").Append(Interlocked.Read(ref ticksEntered)).Append("/").Append(Interlocked.Read(ref ticksCompleted))
              .Append(", nested=").Append(Interlocked.Read(ref nestedEntries))
              .Append(", exceptions=").Append(Interlocked.Read(ref exceptionsSeen))
              .Append(", latestEpoch=").Append(Interlocked.Read(ref epochSerial))
              .Append(", installFailures=").Append(Volatile.Read(ref installFailures))
              .Append(". DoSingleTick remains on Unity main thread; epoch is a validity boundary for captured worker packets only.");
            return sb.ToString();
        }

        public struct EpochState
        {
            public bool Entered;
            public bool Outermost;
            public long Epoch;
            public int GameTick;
        }
    }
}
