using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.9 compatibility-first JobGiver candidate reduction.
    //
    // The V0.4.8 capture showed that slow JobGiver packages are dominated by the
    // GenClosest -> Reachability -> RegionTraverser chain. The safe first production
    // response is NOT to move CanReach/validators off-thread and NOT to replace
    // GenClosest selection semantics. Instead, RimMT only changes the enumeration
    // order of large, stable custom global Thing lists before Vanilla evaluates them.
    //
    // A main-thread snapshot records exact Thing reference order and integer positions.
    // A worker computes a stable distance order for the current root cell using only
    // those immutable primitives. The next matching call receives all original
    // candidates, sorted nearest-first with original index as the tie-breaker. Vanilla
    // still performs every max-distance, reachability, validator and final-result
    // decision. If the list membership/order/position changes, or any call shape is
    // ambiguous, RimMT falls back without rewriting the enumerable.
    internal static class WorkCandidateOrderAccelerator
    {
        private const string FeatureId = "parallel.jobCandidates";
        private const int MinCandidateCount = 96;
        private const int MaxRootOrdersPerList = 16;

        private static readonly ConditionalWeakTable<object, SearchSetState> States = new ConditionalWeakTable<object, SearchSetState>();
        private static volatile bool compatibilityReady;

        private static long observedCalls;
        private static long eligibleCalls;
        private static long reorderedCalls;
        private static long cacheHits;
        private static long cacheMisses;
        private static long nonListFallbacks;
        private static long smallSetFallbacks;
        private static long haulableBypasses;
        private static long unsupportedEntryFallbacks;
        private static long snapshotRefreshes;
        private static long snapshotMismatchFallbacks;
        private static long buildsScheduled;
        private static long buildsPublished;
        private static long buildsDiscarded;
        private static long buildsRejected;
        private static long rootOrderEvictions;
        private static long candidatesReordered;
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
                    Log.Warning("[RimMT] parallel.jobCandidates V0.4.9 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(WorkCandidateOrderAccelerator), nameof(Prefix));
                // The V0.4.6 exact hauling fast-path remains first. V0.4.9 only sees
                // calls that continue toward Vanilla and explicitly bypasses haulables.
                prefix.priority = Priority.VeryHigh;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[RimMT] parallel.jobCandidates V0.4.9 installed. Large stable custom global Thing lists may be worker-ordered nearest-first, while Vanilla GenClosest/Reachability/validator/final selection remains authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "candidate ordering patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobCandidates V0.4.9 patch failed; Vanilla candidate enumeration remains unchanged. " + ex.GetType().Name + ": " + ex.Message);
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
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions)
        {
            Interlocked.Increment(ref observedCalls);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing)
                return;

            // Keep the first production pass intentionally narrow. We only reorder the
            // custom global list for the same broad non-prioritized/global call family
            // proven hot by V0.4.8. We do not touch region-only searches or custom
            // traversable-region semantics.
            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || !thingReq.IsUndefined ||
                customGlobalSearchSet == null || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
                return;

            try
            {
                IList<Thing> list = customGlobalSearchSet as IList<Thing>;
                if (list == null)
                {
                    Interlocked.Increment(ref nonListFallbacks);
                    return;
                }

                // V0.4.6 already owns this exact list and can skip the Vanilla global
                // pass entirely. Reordering it again would only duplicate work.
                List<Thing> haulables = map.listerHaulables == null ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling();
                if (haulables != null && ReferenceEquals(customGlobalSearchSet, haulables))
                {
                    Interlocked.Increment(ref haulableBypasses);
                    return;
                }

                int count = list.Count;
                if (count < MinCandidateCount)
                {
                    Interlocked.Increment(ref smallSetFallbacks);
                    return;
                }

                Interlocked.Increment(ref eligibleCalls);
                SearchSetState state = States.GetValue((object)list, delegate(object _) { return new SearchSetState(); });

                bool snapshotMatches = SnapshotMatches(state, list, map);
                if (!snapshotMatches)
                {
                    if (state.SnapshotThings != null)
                        Interlocked.Increment(ref snapshotMismatchFallbacks);
                    if (!RefreshSnapshot(state, list, map))
                    {
                        Interlocked.Increment(ref unsupportedEntryFallbacks);
                        return;
                    }
                    Interlocked.Increment(ref snapshotRefreshes);
                }

                int rootKey = root.x + root.z * map.Size.x;
                Thing[] ordered;
                if (state.Orders.TryGetValue(rootKey, out ordered) && ordered != null && ordered.Length == count)
                {
                    Interlocked.Increment(ref cacheHits);
                    Interlocked.Increment(ref reorderedCalls);
                    Interlocked.Add(ref candidatesReordered, count);
                    customGlobalSearchSet = ordered;
                    return;
                }

                Interlocked.Increment(ref cacheMisses);
                EnsureOrderBuildScheduled(state, rootKey, root, map);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobCandidates V0.4.9 runtime failure; this call keeps the original Vanilla candidate order. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool SnapshotMatches(SearchSetState state, IList<Thing> list, Map map)
        {
            Thing[] things = state.SnapshotThings;
            int[] xs = state.Xs;
            int[] zs = state.Zs;
            if (things == null || xs == null || zs == null || things.Length != list.Count ||
                xs.Length != things.Length || zs.Length != things.Length || state.MapId != map.uniqueID)
                return false;

            // Exact O(n) identity + position verification is deliberate. It is much
            // cheaper than thousands of CanReach calls and prevents same-count list
            // churn or unseen moved Things from producing a stale ordering.
            for (int i = 0; i < things.Length; i++)
            {
                Thing current = list[i];
                if (current == null || !ReferenceEquals(current, things[i]) || !current.Spawned || current.Map != map)
                    return false;
                IntVec3 pos = current.Position;
                if (!pos.IsValid || !pos.InBounds(map) || pos.x != xs[i] || pos.z != zs[i])
                    return false;
            }
            return true;
        }

        private static bool RefreshSnapshot(SearchSetState state, IList<Thing> list, Map map)
        {
            int count = list.Count;
            Thing[] things = new Thing[count];
            int[] xs = new int[count];
            int[] zs = new int[count];

            for (int i = 0; i < count; i++)
            {
                Thing thing = list[i];
                if (thing == null || !thing.Spawned || thing.Map != map)
                {
                    state.ClearSnapshot();
                    return false;
                }

                IntVec3 pos = thing.Position;
                if (!pos.IsValid || !pos.InBounds(map))
                {
                    state.ClearSnapshot();
                    return false;
                }

                things[i] = thing;
                xs[i] = pos.x;
                zs[i] = pos.z;
            }

            state.Generation++;
            state.MapId = map.uniqueID;
            state.Width = map.Size.x;
            state.Height = map.Size.z;
            state.SnapshotThings = things;
            state.Xs = xs;
            state.Zs = zs;
            state.Orders.Clear();
            state.InFlightRoots.Clear();
            return true;
        }

        private static void EnsureOrderBuildScheduled(SearchSetState state, int rootKey, IntVec3 root, Map map)
        {
            if (state.InFlightRoots.Contains(rootKey) || state.SnapshotThings == null)
                return;

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
                return;

            Thing[] snapshotThings = state.SnapshotThings;
            int[] xs = state.Xs;
            int[] zs = state.Zs;
            int generation = state.Generation;
            int mapId = state.MapId;
            int count = snapshotThings.Length;
            int rootX = root.x;
            int rootZ = root.z;

            // The worker receives immutable arrays captured on the main thread. It may
            // carry Thing references into the result array, but never dereferences them.
            state.InFlightRoots.Add(rootKey);
            Interlocked.Increment(ref buildsScheduled);

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                SortEntry[] entries = new SortEntry[count];
                for (int i = 0; i < count; i++)
                {
                    long dx = xs[i] - rootX;
                    long dz = zs[i] - rootZ;
                    entries[i] = new SortEntry(snapshotThings[i], i, dx * dx + dz * dz);
                }

                Array.Sort(entries, SortEntryComparer.Instance);
                Thing[] ordered = new Thing[count];
                for (int i = 0; i < count; i++)
                    ordered[i] = entries[i].Thing;

                MainThreadDispatcher.TryEnqueue(delegate
                {
                    state.InFlightRoots.Remove(rootKey);
                    if (map.Disposed || map.uniqueID != mapId || state.Generation != generation || state.SnapshotThings != snapshotThings)
                    {
                        Interlocked.Increment(ref buildsDiscarded);
                        return;
                    }

                    if (state.Orders.Count >= MaxRootOrdersPerList && !state.Orders.ContainsKey(rootKey))
                    {
                        int removeKey = int.MinValue;
                        foreach (KeyValuePair<int, Thing[]> pair in state.Orders)
                        {
                            removeKey = pair.Key;
                            break;
                        }
                        if (removeKey != int.MinValue)
                        {
                            state.Orders.Remove(removeKey);
                            Interlocked.Increment(ref rootOrderEvictions);
                        }
                    }

                    state.Orders[rootKey] = ordered;
                    Interlocked.Increment(ref buildsPublished);
                });
            });

            if (!accepted)
            {
                state.InFlightRoots.Remove(rootKey);
                Interlocked.Increment(ref buildsRejected);
            }
        }

        internal static string Summary()
        {
            long reordered = Interlocked.Read(ref reorderedCalls);
            long reorderedCandidates = Interlocked.Read(ref candidatesReordered);
            double avgCandidates = reordered <= 0 ? 0.0 : reorderedCandidates / (double)reordered;

            return "Work candidate ordering V0.4.9: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", eligible=" + Interlocked.Read(ref eligibleCalls) +
                ", reordered=" + reordered +
                ", cacheHits=" + Interlocked.Read(ref cacheHits) +
                ", cacheMisses=" + Interlocked.Read(ref cacheMisses) +
                ", nonList=" + Interlocked.Read(ref nonListFallbacks) +
                ", smallSet=" + Interlocked.Read(ref smallSetFallbacks) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", unsupportedEntry=" + Interlocked.Read(ref unsupportedEntryFallbacks) +
                ", snapshotRefreshes=" + Interlocked.Read(ref snapshotRefreshes) +
                ", snapshotMismatch=" + Interlocked.Read(ref snapshotMismatchFallbacks) +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
                ", buildsPublished=" + Interlocked.Read(ref buildsPublished) +
                ", buildsDiscarded=" + Interlocked.Read(ref buildsDiscarded) +
                ", buildsRejected=" + Interlocked.Read(ref buildsRejected) +
                ", rootEvictions=" + Interlocked.Read(ref rootOrderEvictions) +
                ", candidatesReordered=" + reorderedCandidates +
                ", avgCandidatesReordered=" + avgCandidates.ToString("F1") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". All candidates remain present; Vanilla GenClosest/Reachability/validator/final selection stays authoritative.";
        }

        private sealed class SearchSetState
        {
            internal int Generation;
            internal int MapId = -1;
            internal int Width;
            internal int Height;
            internal Thing[] SnapshotThings;
            internal int[] Xs;
            internal int[] Zs;
            internal readonly Dictionary<int, Thing[]> Orders = new Dictionary<int, Thing[]>();
            internal readonly HashSet<int> InFlightRoots = new HashSet<int>();

            internal void ClearSnapshot()
            {
                Generation++;
                MapId = -1;
                Width = 0;
                Height = 0;
                SnapshotThings = null;
                Xs = null;
                Zs = null;
                Orders.Clear();
                InFlightRoots.Clear();
            }
        }

        private struct SortEntry
        {
            internal readonly Thing Thing;
            internal readonly int OriginalIndex;
            internal readonly long DistanceSquared;

            internal SortEntry(Thing thing, int originalIndex, long distanceSquared)
            {
                Thing = thing;
                OriginalIndex = originalIndex;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class SortEntryComparer : IComparer<SortEntry>
        {
            internal static readonly SortEntryComparer Instance = new SortEntryComparer();

            public int Compare(SortEntry a, SortEntry b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                if (distance != 0)
                    return distance;
                return a.OriginalIndex.CompareTo(b.OriginalIndex);
            }
        }
    }
}
