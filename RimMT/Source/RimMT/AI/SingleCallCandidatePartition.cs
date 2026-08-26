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
    // V0.4.11 extends the production path validated in V0.4.10.
    //
    // RimWorld's ClosestThingReachable first performs its normal regionwise search when
    // the ThingRequest can be found in regions. customGlobalSearchSet is consumed only
    // by the later global fallback. Therefore a defined ThingRequest does not make
    // reordering the custom global set unsafe: region traversal, its stopping rules and
    // any result it returns are untouched. V0.4.11 consequently admits those calls too.
    //
    // The optimization remains deliberately non-authoritative. Candidate membership is
    // unchanged. The main thread snapshots exact candidate identity and PositionHeld,
    // then orders the same candidates by exact horizontal squared distance. Equal-distance
    // candidates keep their original relative order, preserving Vanilla's tie order as
    // closely as possible. Vanilla still owns max-distance checks, Reachability.CanReach,
    // validators, reservations, priorities, final target selection and Job creation.
    //
    // Calls for which global search is disabled are not touched because Vanilla never
    // consumes customGlobalSearchSet there. Non-passable traversableRegionTypes remain
    // fail-closed because Vanilla itself considers that global-search combination
    // unsupported. Exact ListerHaulables identity also remains owned by V0.4.6/V0.4.7.
    internal static class SingleCallCandidatePartition
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int WorkerAssistMinCount = 512;
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
        private static long sortTicks;
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
                HarmonyMethod prefix = new HarmonyMethod(typeof(SingleCallCandidatePartition), nameof(Prefix));
                prefix.priority = Priority.First + 50;
                harmony.Patch(target, prefix: prefix);
                Log.Message("[RimMT] parallel.jobPartition V0.4.11 installed. Defined-ThingRequest custom global fallbacks are now supported; exact-distance stable ordering is applied per call while Vanilla Reachability/validator/final selection remain authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "single-call partition patch failed: " + ex.GetType().Name);
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

            // Vanilla itself logs this global-search shape as unsupported because its
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

                int[] distanceKeys = new int[count];
                bool workerDone = false;
                if (count >= WorkerAssistMinCount)
                    workerDone = TryWorkerDistanceKeys(root.x, root.z, xs, zs, distanceKeys);
                if (!workerDone)
                    ComputeDistanceKeys(root.x, root.z, xs, zs, distanceKeys);

                long sortStarted = Stopwatch.GetTimestamp();
                customGlobalSearchSet = StableExactDistanceOrder(things, distanceKeys);
                Interlocked.Add(ref sortTicks, Stopwatch.GetTimestamp() - sortStarted);

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

        private static bool TryWorkerDistanceKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] distanceKeys)
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
                    ComputeDistanceKeys(rootX, rootZ, xs, zs, distanceKeys);
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

        private static void ComputeDistanceKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] distanceKeys)
        {
            for (int i = 0; i < distanceKeys.Length; i++)
            {
                int dx = xs[i] - rootX;
                int dz = zs[i] - rootZ;
                distanceKeys[i] = dx * dx + dz * dz;
            }
        }

        private static Thing[] StableExactDistanceOrder(Thing[] source, int[] distanceKeys)
        {
            CandidateOrder[] order = new CandidateOrder[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                order[i].Thing = source[i];
                order[i].DistanceSquared = distanceKeys[i];
                order[i].OriginalIndex = i;
            }

            Array.Sort(order, delegate(CandidateOrder a, CandidateOrder b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            Thing[] result = new Thing[order.Length];
            for (int i = 0; i < order.Length; i++)
                result[i] = order[i].Thing;
            return result;
        }

        private struct CandidateOrder
        {
            internal Thing Thing;
            internal int DistanceSquared;
            internal int OriginalIndex;
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
            double sortMs = Interlocked.Read(ref sortTicks) * 1000.0 / Stopwatch.Frequency;
            double avgSortUs = reordered <= 0 ? 0.0 : sortMs * 1000.0 / reordered;

            return "Single-call work partition V0.4.11: compatibilityReady=" + compatibilityReady +
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
                ", sortTotalMs=" + sortMs.ToString("F3") +
                ", avgSortUs=" + avgSortUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Candidate membership is unchanged; exact-distance ties retain original order; Vanilla region search/Reachability/validator/final selection remains authoritative.";
        }
    }
}
