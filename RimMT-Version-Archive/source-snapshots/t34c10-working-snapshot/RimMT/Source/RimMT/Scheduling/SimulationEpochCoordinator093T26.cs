using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T26 establishes a simulation-epoch boundary at TickManager.DoSingleTick and a reusable
    /// primitive-only parallel stage. The Unity/Verse simulation remains on the main thread.
    /// Workers may consume only captured primitive arrays; they never dereference Pawn/Thing/Map,
    /// never create Jobs, never reserve, and never commit gameplay state.
    ///
    /// First production consumer: the large custom WorkGiver candidate partition. It may split
    /// pure distance/ring-key calculation across several workers. The main thread waits only for a
    /// very small bounded budget. On timeout it immediately computes the keys itself while workers
    /// finish into a private throw-away buffer. There is no unbounded worker wait.
    /// </summary>
    internal static class SimulationEpochCoordinator093T26
    {
        internal const string FeatureId = "parallel.engineStage";

        private const int MinParallelItems = 256;
        private const int BatchSize = 64;
        private const int MaxInFlightKernels = 2;
        private const double MainThreadBudgetMs = 0.40;

        private static bool installed;
        private static int installFailures;
        private static long epoch;
        private static long epochCalls;
        private static long epochTicksTotal;
        private static long epochTicksMax;
        private static long currentEpoch;
        private static long currentGameTick;

        private static int inFlightKernels;
        private static long kernelAttempts;
        private static long kernelAccepted;
        private static long kernelCompletedInBudget;
        private static long kernelTimedOut;
        private static long kernelRejected;
        private static long kernelBusyBypass;
        private static long kernelItems;
        private static long kernelBatches;
        private static long kernelMainWaitTicks;
        private static long kernelMainWaitTicksMax;

        internal static long CurrentEpoch { get { return Interlocked.Read(ref currentEpoch); } }
        internal static long CurrentGameTick { get { return Interlocked.Read(ref currentGameTick); } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase tick = AccessTools.Method(typeof(TickManager), "DoSingleTick");
                if (tick == null)
                {
                    installFailures++;
                    Log.Warning("[RimMT] T26 simulation epoch unavailable: TickManager.DoSingleTick not found.");
                    return;
                }

                harmony.Patch(tick,
                    prefix: new HarmonyMethod(typeof(SimulationEpochCoordinator093T26), nameof(TickPrefix)) { priority = Priority.First + 350 },
                    postfix: new HarmonyMethod(typeof(SimulationEpochCoordinator093T26), nameof(TickPostfix)) { priority = Priority.Last - 350 });
                installed = true;
                Log.Message("[RimMT] T26 Engine Parallel Simulation coordinator installed at DoSingleTick. Unity/Verse state stays main-thread; primitive compute stages may fan out to workers with bounded no-wait fallback.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T26 simulation epoch install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void TickPrefix(ref EpochState __state)
        {
            __state = default(EpochState);
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Started = Stopwatch.GetTimestamp();
            long next = Interlocked.Increment(ref epoch);
            Interlocked.Exchange(ref currentEpoch, next);
            try { Interlocked.Exchange(ref currentGameTick, Find.TickManager.TicksGame); }
            catch { Interlocked.Exchange(ref currentGameTick, -1L); }
            Interlocked.Increment(ref epochCalls);
        }

        public static void TickPostfix(EpochState __state)
        {
            if (!__state.Entered || __state.Started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            if (elapsed < 0L) return;
            Interlocked.Add(ref epochTicksTotal, elapsed);
            UpdateMax(ref epochTicksMax, elapsed);
        }

        /// <summary>
        /// Computes Chebyshev ring keys from primitive position arrays. Returns true only when the
        /// worker batches completed within the bounded main-thread budget and their private result
        /// was copied into ringKeys. Returning false means the caller must use its serial fallback.
        /// Workers may still finish after false, but they write only to a private array.
        /// </summary>
        internal static bool TryComputeRingKeys(int rootX, int rootZ, int ringSize, int[] xs, int[] zs, int[] ringKeys)
        {
            Interlocked.Increment(ref kernelAttempts);
            if (!installed || !FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || xs == null || zs == null || ringKeys == null ||
                xs.Length != zs.Length || xs.Length != ringKeys.Length || xs.Length < MinParallelItems || ringSize <= 0)
            {
                Interlocked.Increment(ref kernelRejected);
                return false;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || scheduler.WorkerCount < 2 || scheduler.ProductionPending > 16)
            {
                Interlocked.Increment(ref kernelRejected);
                return false;
            }

            if (Interlocked.Increment(ref inFlightKernels) > MaxInFlightKernels)
            {
                Interlocked.Decrement(ref inFlightKernels);
                Interlocked.Increment(ref kernelBusyBypass);
                return false;
            }

            int count = xs.Length;
            int batches = (count + BatchSize - 1) / BatchSize;
            int remaining = batches;
            int[] workerKeys = new int[count];
            long kernelEpoch = CurrentEpoch;
            bool accepted = scheduler.ParallelFor(
                FeatureId,
                0,
                count,
                BatchSize,
                delegate(int start, int end)
                {
                    try
                    {
                        // Primitive-only worker body. No Verse/Unity object access is permitted.
                        for (int i = start; i < end; i++)
                        {
                            int dx = Math.Abs(xs[i] - rootX);
                            int dz = Math.Abs(zs[i] - rootZ);
                            workerKeys[i] = Math.Max(dx, dz) / ringSize;
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                            Interlocked.Decrement(ref inFlightKernels);
                    }
                },
                null,
                JobPriority.High);

            if (!accepted)
            {
                Interlocked.Decrement(ref inFlightKernels);
                Interlocked.Increment(ref kernelRejected);
                return false;
            }

            Interlocked.Increment(ref kernelAccepted);
            Interlocked.Add(ref kernelItems, count);
            Interlocked.Add(ref kernelBatches, batches);

            long started = Stopwatch.GetTimestamp();
            long budgetTicks = Math.Max(1L, (long)(Stopwatch.Frequency * MainThreadBudgetMs / 1000.0));
            SpinWait spinner = new SpinWait();
            while (Volatile.Read(ref remaining) > 0 && Stopwatch.GetTimestamp() - started < budgetTicks)
                spinner.SpinOnce();

            long waited = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref kernelMainWaitTicks, waited);
            UpdateMax(ref kernelMainWaitTicksMax, waited);

            if (Volatile.Read(ref remaining) == 0 && CurrentEpoch == kernelEpoch)
            {
                Array.Copy(workerKeys, ringKeys, count);
                Interlocked.Increment(ref kernelCompletedInBudget);
                return true;
            }

            // Critical rule: do not wait. Caller immediately executes its existing serial path.
            // Late workers continue only against workerKeys, which is never published.
            Interlocked.Increment(ref kernelTimedOut);
            return false;
        }

        internal static string Summary()
        {
            long calls = Interlocked.Read(ref epochCalls);
            long accepted = Interlocked.Read(ref kernelAccepted);
            double avgEpochUs = calls == 0 ? 0.0 :
                Interlocked.Read(ref epochTicksTotal) * 1000000.0 / Stopwatch.Frequency / calls;
            double avgItems = accepted == 0 ? 0.0 : Interlocked.Read(ref kernelItems) / (double)accepted;
            double avgWaitUs = accepted == 0 ? 0.0 :
                Interlocked.Read(ref kernelMainWaitTicks) * 1000000.0 / Stopwatch.Frequency / accepted;
            return "T26 engine parallel simulation: installed=" + installed +
                ", epoch=" + CurrentEpoch +
                ", tick=" + CurrentGameTick +
                ", epochCalls=" + calls +
                ", avgEpochUs=" + avgEpochUs.ToString("F1") +
                ", maxEpochMs=" + (Interlocked.Read(ref epochTicksMax) * 1000.0 / Stopwatch.Frequency).ToString("F2") +
                ", kernels[attempt/accepted/inBudget/timeout/rejected/busy]=" +
                Interlocked.Read(ref kernelAttempts) + "/" + accepted + "/" +
                Interlocked.Read(ref kernelCompletedInBudget) + "/" + Interlocked.Read(ref kernelTimedOut) + "/" +
                Interlocked.Read(ref kernelRejected) + "/" + Interlocked.Read(ref kernelBusyBypass) +
                ", batches=" + Interlocked.Read(ref kernelBatches) +
                ", avgItems=" + avgItems.ToString("F1") +
                ", avgMainWaitUs=" + avgWaitUs.ToString("F2") +
                ", maxMainWaitUs=" + (Interlocked.Read(ref kernelMainWaitTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", inFlight=" + Volatile.Read(ref inFlightKernels) +
                ", installFailures=" + installFailures +
                ". DoSingleTick remains on the Unity main thread; workers receive primitive arrays only; timeout never blocks the simulation thread.";
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
            }
        }

        internal struct EpochState
        {
            internal bool Entered;
            internal long Started;
        }
    }
}
