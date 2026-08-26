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
    // V0.4.12 keeps the wider safe custom-global coverage introduced by V0.4.11,
    // but restores the coarse stable spatial partition validated in V0.4.10.
    //
    // RimWorld's ClosestThingReachable still performs its normal regionwise search first
    // when the ThingRequest can be found in regions. customGlobalSearchSet is consumed
    // only by the later global fallback, so admitting defined ThingRequests does not alter
    // region traversal, its stopping rules, or any result returned by the region search.
    //
    // Candidate membership is never changed. The main thread snapshots exact candidate
    // identity and PositionHeld, then stable-partitions candidates into 16-cell spatial
    // rings from near to far. Crucially, candidates inside the same ring retain their
    // original relative order. This preserves much more of the WorkGiver/Vanilla scan
    // sequence than the exact-distance ordering tested in V0.4.11 while still helping
    // Vanilla establish a smaller best-distance early enough to skip many far candidates.
    //
    // Vanilla remains authoritative for max-distance checks, Reachability.CanReach,
    // validators, reservations, priorities, final target selection and Job creation.
    // Calls where global search is disabled or non-passable region types are requested
    // remain fail-closed. Exact ListerHaulables identity stays on V0.4.6/V0.4.7/PUAH paths.
    internal static class SingleCallCandidatePartition
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int WorkerAssistMinCount = 512;
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
        private static long noCustomSetFallbacks;
        private static long invalidMapRootFallbacks;
        private static long invalidCandidateFallbacks;
        private static long globalDisabledFallbacks;
        private static long nonPassableRegionTypeFallbacks;
        private static long definedRequestExpanded;
        private static long forbiddenRegionExpanded;
        private static long workerAssistAttempts;
        private static long workerAssistCompleted;
        private static long workerAssistTimeouts;
        private static long workerAssistRejected;
        private static long candidatesSeen;
        private static long candidatesReordered;
        private static long maxCandidateCount;
        private static long partitionTicks;
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
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.12 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(SingleCallCandidatePartition), nameof(Prefix));
                prefix.priority = Priority.First + 50;
                harmony.Patch(target, prefix: prefix);
                Log.Message("[RimMT] parallel.jobPartition V0.4.12 installed. Expanded custom-global coverage now uses V0.4.10-style stable 16-cell rings; Vanilla region search/Reachability/validator/final selection remain authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "single-call partition patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.12 patch failed; Vanilla candidate order remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
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

            if (customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref noCustomSetFallbacks);
                return;
            }

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map))
            {
                Interlocked.Increment(ref invalidMapRootFallbacks);
                return;
            }

            // Vanilla never consumes customGlobalSearchSet when global search is disabled.
            if (!(searchRegionsMax < 0 || forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref globalDisabledFallbacks);
                return;
            }

            // Vanilla itself considers this global-search shape unsupported because its
            // Reachability check is based on passable regions only. Keep it fail-closed.
            if (traversableRegionTypes != RegionType.Set_Passable)
            {
                Interlocked.Increment(ref nonPassableRegionTypeFallbacks);
                return;
            }

            try
            {
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
                        Interlocked.Increment(ref invalidCandidateFallbacks);
                        return;
                    }

                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid || !pos.InBounds(map))
                    {
                        Interlocked.Increment(ref invalidCandidateFallbacks);
                        return;
                    }

                    xs[i] = pos.x;
                    zs[i] = pos.z;
                }

                int[] ringKeys = new int[count];
                bool workerDone = false;
                if (count >= WorkerAssistMinCount)
                    workerDone = TryWorkerRingKeys(root.x, root.z, xs, zs, ringKeys);
                if (!workerDone)
                    ComputeRingKeys(root.x, root.z, xs, zs, ringKeys);

                long partitionStarted = Stopwatch.GetTimestamp();
                customGlobalSearchSet = StableRingPartition(things, ringKeys);
                Interlocked.Add(ref partitionTicks, Stopwatch.GetTimestamp() - partitionStarted);

                if (!thingReq.IsUndefined)
                    Interlocked.Increment(ref definedRequestExpanded);
                if (ignoreEntirelyForbiddenRegions)
                    Interlocked.Increment(ref forbiddenRegionExpanded);

                Interlocked.Increment(ref supportedCalls);
                Interlocked.Increment(ref reorderedCalls);
                Interlocked.Add(ref candidatesReordered, count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.12 runtime failure; this call keeps Vanilla candidate order. " + ex.GetType().Name + ": " + ex.Message);
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
            double partitionMs = Interlocked.Read(ref partitionTicks) * 1000.0 / Stopwatch.Frequency;
            double avgPartitionUs = reordered <= 0 ? 0.0 : partitionMs * 1000.0 / reordered;

            return "Single-call work partition V0.4.12: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", supported=" + Interlocked.Read(ref supportedCalls) +
                ", reordered=" + reordered +
                ", definedRequestExpanded=" + Interlocked.Read(ref definedRequestExpanded) +
                ", forbiddenRegionExpanded=" + Interlocked.Read(ref forbiddenRegionExpanded) +
                ", listInputs=" + Interlocked.Read(ref listInputs) +
                ", collectionInputs=" + Interlocked.Read(ref collectionInputs) +
                ", enumerableInputs=" + Interlocked.Read(ref enumerableInputs) +
                ", materialized=" + Interlocked.Read(ref materializedEnumerables) +
                ", smallSet=" + Interlocked.Read(ref smallSetFallbacks) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", noCustomSet=" + Interlocked.Read(ref noCustomSetFallbacks) +
                ", invalidMapRoot=" + Interlocked.Read(ref invalidMapRootFallbacks) +
                ", invalidCandidate=" + Interlocked.Read(ref invalidCandidateFallbacks) +
                ", globalDisabled=" + Interlocked.Read(ref globalDisabledFallbacks) +
                ", nonPassableRegionType=" + Interlocked.Read(ref nonPassableRegionTypeFallbacks) +
                ", workerAttempts=" + Interlocked.Read(ref workerAssistAttempts) +
                ", workerImmediate=" + Interlocked.Read(ref workerAssistCompleted) +
                ", workerWaits=" + Interlocked.Read(ref workerAssistTimeouts) +
                ", workerRejected=" + Interlocked.Read(ref workerAssistRejected) +
                ", candidatesSeen=" + Interlocked.Read(ref candidatesSeen) +
                ", candidatesReordered=" + candidates +
                ", avgCandidates=" + avg.ToString("F1") +
                ", maxCandidates=" + Interlocked.Read(ref maxCandidateCount) +
                ", partitionTotalMs=" + partitionMs.ToString("F3") +
                ", avgPartitionUs=" + avgPartitionUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Candidate membership is unchanged; 16-cell rings are near-to-far and preserve original order inside each ring; Vanilla region search/Reachability/validator/final selection remains authoritative.";
        }
    }
}
