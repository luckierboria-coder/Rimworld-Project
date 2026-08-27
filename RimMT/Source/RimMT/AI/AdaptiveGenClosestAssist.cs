using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.11 production goal: reduce JobGiver search spikes without moving gameplay
    // authority off the main thread.
    //
    // Supported custom-global GenClosest calls keep the exact same candidate identities.
    // RimMT only snapshots integer positions and reorders those candidates into stable
    // nearest-first spatial rings so Vanilla can establish a useful best-distance early.
    // Vanilla still owns Reachability.CanReach, WorkGiver validators, reservations and
    // final Job selection.
    //
    // The threshold and ring granularity adapt to measured frame pressure. Worker assist
    // is opportunistic only: if its tiny time budget expires, the main thread immediately
    // performs the cheap integer key build itself. It NEVER waits for a late worker.
    internal static class AdaptiveGenClosestAssist
    {
        private const string FeatureId = "parallel.jobPartition";

        private const int ThresholdLow = 128;
        private const int ThresholdNormal = 96;
        private const int ThresholdHigh = 80;
        private const int ThresholdCritical = 64;

        private const int RingLow = 16;
        private const int RingNormal = 12;
        private const int RingHigh = 8;
        private const int RingCritical = 8;

        private const int WorkerThresholdLow = 768;
        private const int WorkerThresholdNormal = 512;
        private const int WorkerThresholdHigh = 384;
        private const int WorkerThresholdCritical = 256;

        // Small enough that worker assist cannot become a new visible main-thread stall.
        private const double WorkerBudgetLowMs = 0.06;
        private const double WorkerBudgetNormalMs = 0.08;
        private const double WorkerBudgetHighMs = 0.10;
        private const double WorkerBudgetCriticalMs = 0.12;

        private static volatile bool compatibilityReady;
        private static int workerAssistInFlight;

        private static long observedCalls;
        private static long supportedCalls;
        private static long reorderedCalls;
        private static long materializedEnumerables;
        private static long listInputs;
        private static long collectionInputs;
        private static long enumerableInputs;
        private static long smallSetFallbacks;
        private static long haulableBypasses;
        private static long unsupportedShapeFallbacks;
        private static long nullOrInvalidFallbacks;

        private static long pressureLowCalls;
        private static long pressureNormalCalls;
        private static long pressureHighCalls;
        private static long pressureCriticalCalls;

        private static long workerAssistAttempts;
        private static long workerAssistCompletedInBudget;
        private static long workerAssistDeadlineMisses;
        private static long workerAssistRejected;
        private static long workerAssistBusyBypasses;
        private static long workerAssistLateCompletions;
        private static long mainThreadKeyBuilds;

        private static long candidatesSeen;
        private static long candidatesReordered;
        private static long maxCandidateCount;
        private static long reorderTicksTotal;
        private static long reorderTicksMax;
        private static long failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(GenClosest),
                    nameof(GenClosest.ClosestThingReachable),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(IEnumerable<Thing>), typeof(int), typeof(int),
                        typeof(bool), typeof(RegionType), typeof(bool)
                    });

                if (target == null)
                {
                    FeatureGate.Suppress(FeatureId, "GenClosest.ClosestThingReachable target not found");
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.11 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AdaptiveGenClosestAssist), nameof(Prefix));
                prefix.priority = Priority.First + 50;
                harmony.Patch(target, prefix: prefix);
                Log.Message("[RimMT] parallel.jobPartition V0.4.11 installed. Adaptive nearest-first candidate ordering is pressure-aware; worker assist is non-blocking and Vanilla Reachability/validator/final selection remain authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "adaptive GenClosest assist patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.11 patch failed; Vanilla candidate order remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
        }

        public static void Prefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            ref IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMin,
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions)
        {
            Interlocked.Increment(ref observedCalls);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref nullOrInvalidFallbacks);
                return;
            }

            // Keep the whitelist narrow. Region-limited searches have different traversal
            // and termination semantics and are not touched by this feature.
            if (!thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref unsupportedShapeFallbacks);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            try
            {
                // Exact hauling paths have their own stronger V0.4.6/V0.4.7 accelerators.
                List<Thing> haulables = map.listerHaulables == null ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling();
                if (haulables != null && ReferenceEquals(customGlobalSearchSet, haulables))
                {
                    Interlocked.Increment(ref haulableBypasses);
                    return;
                }

                Thing[] things;
                if (!TryMaterialize(customGlobalSearchSet, out things))
                    return;

                int count = things.Length;
                Interlocked.Add(ref candidatesSeen, count);
                UpdateMax(ref maxCandidateCount, count);

                LoadPressure pressure = AdaptiveLoadBalancer.Pressure;
                RecordPressure(pressure);
                int threshold = ThresholdFor(pressure);
                if (count < threshold)
                {
                    Interlocked.Increment(ref smallSetFallbacks);
                    return;
                }

                int[] xs = new int[count];
                int[] zs = new int[count];
                for (int i = 0; i < count; i++)
                {
                    Thing thing = things[i];
                    if (thing == null)
                    {
                        Interlocked.Increment(ref nullOrInvalidFallbacks);
                        return;
                    }

                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid || !pos.InBounds(map))
                    {
                        Interlocked.Increment(ref nullOrInvalidFallbacks);
                        return;
                    }

                    xs[i] = pos.x;
                    zs[i] = pos.z;
                }

                int ringSize = RingSizeFor(pressure);
                int[] ringKeys = new int[count];
                bool workerDone = false;
                if (count >= WorkerThresholdFor(pressure))
                    workerDone = TryWorkerRingKeysNonBlocking(root.x, root.z, xs, zs, ringSize, ringKeys, pressure);

                if (!workerDone)
                {
                    ComputeRingKeys(root.x, root.z, xs, zs, ringSize, ringKeys);
                    Interlocked.Increment(ref mainThreadKeyBuilds);
                }

                customGlobalSearchSet = StableRingPartition(things, ringKeys);
                Interlocked.Increment(ref supportedCalls);
                Interlocked.Increment(ref reorderedCalls);
                Interlocked.Add(ref candidatesReordered, count);

                long elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref reorderTicksTotal, elapsed);
                UpdateMax(ref reorderTicksMax, elapsed);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.11 runtime failure; this call keeps Vanilla candidate order. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryMaterialize(IEnumerable<Thing> source, out Thing[] result)
        {
            IList<Thing> list = source as IList<Thing>;
            if (list != null)
            {
                Interlocked.Increment(ref listInputs);
                int count = list.Count;
                result = new Thing[count];
                for (int i = 0; i < count; i++)
                    result[i] = list[i];
                return true;
            }

            ICollection<Thing> collection = source as ICollection<Thing>;
            if (collection != null)
            {
                Interlocked.Increment(ref collectionInputs);
                result = new Thing[collection.Count];
                collection.CopyTo(result, 0);
                Interlocked.Increment(ref materializedEnumerables);
                return true;
            }

            Interlocked.Increment(ref enumerableInputs);
            List<Thing> temp = new List<Thing>();
            foreach (Thing thing in source)
                temp.Add(thing);
            result = temp.ToArray();
            Interlocked.Increment(ref materializedEnumerables);
            return true;
        }

        private static bool TryWorkerRingKeysNonBlocking(
            int rootX,
            int rootZ,
            int[] xs,
            int[] zs,
            int ringSize,
            int[] destination,
            LoadPressure pressure)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || scheduler.Pending > 0 || scheduler.ActiveWorkers >= scheduler.WorkerCount)
            {
                Interlocked.Increment(ref workerAssistBusyBypasses);
                return false;
            }

            // At most one opportunistic assist can outlive its budget. This prevents a run
            // of deadline misses from filling the worker pool with tiny abandoned tasks.
            if (Interlocked.CompareExchange(ref workerAssistInFlight, 1, 0) != 0)
            {
                Interlocked.Increment(ref workerAssistBusyBypasses);
                return false;
            }

            Interlocked.Increment(ref workerAssistAttempts);
            WorkerAssistState state = new WorkerAssistState(destination.Length);
            JobPriority priority = pressure == LoadPressure.High || pressure == LoadPressure.Critical
                ? JobPriority.High
                : JobPriority.Normal;

            bool accepted = scheduler.TryEnqueue(FeatureId, priority, delegate
            {
                try
                {
                    ComputeRingKeys(rootX, rootZ, xs, zs, ringSize, state.Output);
                }
                finally
                {
                    Volatile.Write(ref state.Done, 1);
                    if (Volatile.Read(ref state.Abandoned) != 0)
                        Interlocked.Increment(ref workerAssistLateCompletions);
                    Volatile.Write(ref workerAssistInFlight, 0);
                }
            });

            if (!accepted)
            {
                Volatile.Write(ref workerAssistInFlight, 0);
                Interlocked.Increment(ref workerAssistRejected);
                return false;
            }

            double budgetMs = WorkerBudgetFor(pressure);
            long budgetTicks = Math.Max(1L, (long)(Stopwatch.Frequency * budgetMs / 1000.0));
            long started = Stopwatch.GetTimestamp();
            SpinWait spinner = new SpinWait();
            while (Volatile.Read(ref state.Done) == 0 && Stopwatch.GetTimestamp() - started < budgetTicks)
                spinner.SpinOnce();

            if (Volatile.Read(ref state.Done) != 0)
            {
                Array.Copy(state.Output, destination, destination.Length);
                Interlocked.Increment(ref workerAssistCompletedInBudget);
                return true;
            }

            // Critical V0.4.11 rule: do not wait. The worker owns a private output array,
            // so the main thread can immediately compute its own fallback safely.
            Volatile.Write(ref state.Abandoned, 1);
            Interlocked.Increment(ref workerAssistDeadlineMisses);
            return false;
        }

        private static void ComputeRingKeys(int rootX, int rootZ, int[] xs, int[] zs, int ringSize, int[] ringKeys)
        {
            for (int i = 0; i < ringKeys.Length; i++)
            {
                int dx = Math.Abs(xs[i] - rootX);
                int dz = Math.Abs(zs[i] - rootZ);
                ringKeys[i] = Math.Max(dx, dz) / ringSize;
            }
        }

        private static Thing[] StableRingPartition(Thing[] source, int[] ringKeys)
        {
            int count = source.Length;
            int maxRing = 0;
            for (int i = 0; i < count; i++)
                if (ringKeys[i] > maxRing) maxRing = ringKeys[i];

            int[] counts = new int[maxRing + 1];
            for (int i = 0; i < count; i++)
                counts[ringKeys[i]]++;

            int[] offsets = new int[counts.Length];
            int sum = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                offsets[i] = sum;
                sum += counts[i];
            }

            Thing[] result = new Thing[count];
            int[] write = new int[offsets.Length];
            Array.Copy(offsets, write, offsets.Length);
            for (int i = 0; i < count; i++)
                result[write[ringKeys[i]]++] = source[i];
            return result;
        }

        private static int ThresholdFor(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Low: return ThresholdLow;
                case LoadPressure.High: return ThresholdHigh;
                case LoadPressure.Critical: return ThresholdCritical;
                default: return ThresholdNormal;
            }
        }

        private static int RingSizeFor(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Low: return RingLow;
                case LoadPressure.High: return RingHigh;
                case LoadPressure.Critical: return RingCritical;
                default: return RingNormal;
            }
        }

        private static int WorkerThresholdFor(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Low: return WorkerThresholdLow;
                case LoadPressure.High: return WorkerThresholdHigh;
                case LoadPressure.Critical: return WorkerThresholdCritical;
                default: return WorkerThresholdNormal;
            }
        }

        private static double WorkerBudgetFor(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Low: return WorkerBudgetLowMs;
                case LoadPressure.High: return WorkerBudgetHighMs;
                case LoadPressure.Critical: return WorkerBudgetCriticalMs;
                default: return WorkerBudgetNormalMs;
            }
        }

        private static void RecordPressure(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Low:
                    Interlocked.Increment(ref pressureLowCalls);
                    break;
                case LoadPressure.High:
                    Interlocked.Increment(ref pressureHighCalls);
                    break;
                case LoadPressure.Critical:
                    Interlocked.Increment(ref pressureCriticalCalls);
                    break;
                default:
                    Interlocked.Increment(ref pressureNormalCalls);
                    break;
            }
        }

        private static void UpdateMax(ref long field, long value)
        {
            long observed;
            while (value > (observed = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

        internal static string Summary()
        {
            long reordered = Interlocked.Read(ref reorderedCalls);
            long candidates = Interlocked.Read(ref candidatesReordered);
            double avgCandidates = reordered <= 0 ? 0.0 : candidates / (double)reordered;
            long totalTicks = Interlocked.Read(ref reorderTicksTotal);
            double avgUs = reordered <= 0 ? 0.0 : (totalTicks * 1000000.0 / Stopwatch.Frequency) / reordered;
            double maxUs = Interlocked.Read(ref reorderTicksMax) * 1000000.0 / Stopwatch.Frequency;
            LoadPressure current = AdaptiveLoadBalancer.Pressure;

            return "Adaptive GenClosest assist V0.4.11: compatibilityReady=" + compatibilityReady +
                ", currentPressure=" + current +
                ", currentThreshold=" + ThresholdFor(current) +
                ", currentRing=" + RingSizeFor(current) +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", supported=" + Interlocked.Read(ref supportedCalls) +
                ", reordered=" + reordered +
                ", listInputs=" + Interlocked.Read(ref listInputs) +
                ", collectionInputs=" + Interlocked.Read(ref collectionInputs) +
                ", enumerableInputs=" + Interlocked.Read(ref enumerableInputs) +
                ", materialized=" + Interlocked.Read(ref materializedEnumerables) +
                ", smallSet=" + Interlocked.Read(ref smallSetFallbacks) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", unsupportedShape=" + Interlocked.Read(ref unsupportedShapeFallbacks) +
                ", invalid=" + Interlocked.Read(ref nullOrInvalidFallbacks) +
                ", pressureCalls(L/N/H/C)=" + Interlocked.Read(ref pressureLowCalls) + "/" +
                    Interlocked.Read(ref pressureNormalCalls) + "/" + Interlocked.Read(ref pressureHighCalls) + "/" +
                    Interlocked.Read(ref pressureCriticalCalls) +
                ", workerAttempts=" + Interlocked.Read(ref workerAssistAttempts) +
                ", workerInBudget=" + Interlocked.Read(ref workerAssistCompletedInBudget) +
                ", workerDeadlineMiss=" + Interlocked.Read(ref workerAssistDeadlineMisses) +
                ", workerLate=" + Interlocked.Read(ref workerAssistLateCompletions) +
                ", workerRejected=" + Interlocked.Read(ref workerAssistRejected) +
                ", workerBusyBypass=" + Interlocked.Read(ref workerAssistBusyBypasses) +
                ", mainKeyBuilds=" + Interlocked.Read(ref mainThreadKeyBuilds) +
                ", candidatesSeen=" + Interlocked.Read(ref candidatesSeen) +
                ", candidatesReordered=" + candidates +
                ", avgCandidates=" + avgCandidates.ToString("F1") +
                ", maxCandidates=" + Interlocked.Read(ref maxCandidateCount) +
                ", avgAssistUs=" + avgUs.ToString("F2") +
                ", maxAssistUs=" + maxUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Candidate membership is unchanged; Vanilla GenClosest/Reachability/validator/final selection remains authoritative; worker deadline misses never block the main thread.";
        }

        private sealed class WorkerAssistState
        {
            internal readonly int[] Output;
            internal int Done;
            internal int Abandoned;

            internal WorkerAssistState(int count)
            {
                Output = new int[count];
            }
        }
    }
}
