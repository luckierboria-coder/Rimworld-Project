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
    // V0.4.10 targets the hot chain measured by the bounded JobGiver capture:
    // JobGiver_Work -> GenClosest.ClosestThingReachable -> ClosestThing_Global ->
    // Reachability.CanReach -> RegionTraverser.
    //
    // The optimization is deliberately non-authoritative. It never evaluates
    // Reachability, WorkGiver validators, reservations, priorities or Jobs off-thread.
    // For a supported custom global search set, the main thread materializes the exact
    // candidate identities and positions once, then reorders the same candidates into
    // coarse nearest-first spatial rings for THIS call. Vanilla GenClosest still scans
    // every candidate needed to prove the closest valid result and remains responsible
    // for all live gameplay decisions. Reordering only lets Vanilla establish a small
    // best-distance earlier so later far candidates can skip expensive validator/
    // reachability work.
    //
    // Very large snapshots may opportunistically ask one RimMT worker to compute the
    // pure integer ring keys. V0.4.10.1 makes that candidate-count threshold configurable;
    // the default remains 512. No live Verse object is dereferenced by the worker.
    internal static class SingleCallCandidatePartition
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int DefaultWorkerAssistMinCount = 512;
        private const int RingSize = 16;
        private const double WorkerAssistBudgetMs = 0.20;

        private static volatile bool compatibilityReady;
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
        private static long workerAssistAttempts;
        private static long workerAssistCompleted;
        private static long workerAssistTimeouts;
        private static long workerAssistRejected;
        private static long candidatesSeen;
        private static long candidatesReordered;
        private static long maxCandidateCount;
        private static long failures;

        private static int WorkerAssistMinCount
        {
            get
            {
                RimMTSettings settings = RimMTMod.Settings;
                if (settings == null)
                    return DefaultWorkerAssistMinCount;
                int value = settings.JobPartitionWorkerThreshold;
                if (value < MinCandidateCount) return MinCandidateCount;
                if (value > 2048) return 2048;
                return value;
            }
        }

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
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.10.1 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(SingleCallCandidatePartition), nameof(Prefix));
                prefix.priority = Priority.First + 50;
                harmony.Patch(target, prefix: prefix);
                Log.Message("[RimMT] parallel.jobPartition V0.4.10.1 installed. Supported custom global Work searches are reordered nearest-first per call; worker-assist threshold is configurable and defaults to 512; Vanilla Reachability/validator/final selection remain authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "single-call partition patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.10.1 patch failed; Vanilla candidate order remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
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

            // Narrow global-search shape only. Region-limited scans have different
            // ordering/termination semantics and remain untouched.
            if (!thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref unsupportedShapeFallbacks);
                return;
            }

            try
            {
                // Do not overlap with PUAH/vanilla hauling acceleration. Exact live
                // ListerHaulables identity stays on the established V0.4.6 path.
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
                if (count < MinCandidateCount)
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

                int[] ringKeys = new int[count];
                bool workerDone = false;
                int workerAssistMinCount = WorkerAssistMinCount;
                if (count >= workerAssistMinCount)
                    workerDone = TryWorkerRingKeys(root.x, root.z, xs, zs, ringKeys);
                if (!workerDone)
                    ComputeRingKeys(root.x, root.z, xs, zs, ringKeys);

                customGlobalSearchSet = StableRingPartition(things, ringKeys);
                Interlocked.Increment(ref supportedCalls);
                Interlocked.Increment(ref reorderedCalls);
                Interlocked.Add(ref candidatesReordered, count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.10.1 runtime failure; this call keeps Vanilla candidate order. " + ex.GetType().Name + ": " + ex.Message);
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

        private static bool TryWorkerRingKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] ringKeys)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || scheduler.Pending > 0 || scheduler.ActiveWorkers >= scheduler.WorkerCount)
                return false;

            Interlocked.Increment(ref workerAssistAttempts);
            ManualResetEventSlim done = new ManualResetEventSlim(false);
            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.High, delegate
            {
                try
                {
                    ComputeRingKeys(rootX, rootZ, xs, zs, ringKeys);
                }
                finally
                {
                    done.Set();
                }
            });

            if (!accepted)
            {
                done.Dispose();
                Interlocked.Increment(ref workerAssistRejected);
                return false;
            }

            long budgetTicks = Math.Max(1L, (long)(Stopwatch.Frequency * WorkerAssistBudgetMs / 1000.0));
            long started = Stopwatch.GetTimestamp();
            SpinWait spinner = new SpinWait();
            while (!done.IsSet && Stopwatch.GetTimestamp() - started < budgetTicks)
                spinner.SpinOnce();

            if (done.IsSet)
            {
                done.Dispose();
                Interlocked.Increment(ref workerAssistCompleted);
                return true;
            }

            Interlocked.Increment(ref workerAssistTimeouts);
            done.Wait();
            done.Dispose();
            return true;
        }

        private static void ComputeRingKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] ringKeys)
        {
            for (int i = 0; i < ringKeys.Length; i++)
            {
                int dx = Math.Abs(xs[i] - rootX);
                int dz = Math.Abs(zs[i] - rootZ);
                ringKeys[i] = Math.Max(dx, dz) / RingSize;
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

        private static void UpdateMax(ref long field, int value)
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
            double avg = reordered <= 0 ? 0.0 : candidates / (double)reordered;
            return "Single-call work partition V0.4.10.1: compatibilityReady=" + compatibilityReady +
                ", workerThreshold=" + WorkerAssistMinCount +
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
                ", workerAttempts=" + Interlocked.Read(ref workerAssistAttempts) +
                ", workerImmediate=" + Interlocked.Read(ref workerAssistCompleted) +
                ", workerWaits=" + Interlocked.Read(ref workerAssistTimeouts) +
                ", workerRejected=" + Interlocked.Read(ref workerAssistRejected) +
                ", candidatesSeen=" + Interlocked.Read(ref candidatesSeen) +
                ", candidatesReordered=" + candidates +
                ", avgCandidates=" + avg.ToString("F1") +
                ", maxCandidates=" + Interlocked.Read(ref maxCandidateCount) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Candidate membership is unchanged; Vanilla GenClosest/Reachability/validator/final selection remains authoritative.";
        }
    }
}
