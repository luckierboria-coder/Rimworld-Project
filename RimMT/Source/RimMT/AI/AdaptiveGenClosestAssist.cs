using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.13: true offload for repeated custom global Work scans.
    //
    // V0.4.11/0.4.12 still did useful ordering work on the main thread. This version
    // changes the model: the first observation snapshots only Thing refs + integer
    // positions on the main thread, then a normal-priority RimMT worker builds an
    // immutable spatial index. Later calls consume that already-built index without
    // waiting for a worker. Vanilla live Reachability, the caller validator and final
    // Job/Reservation authority remain on the main thread.
    //
    // The optimization is deliberately narrow:
    //   * ThingRequest must be Undefined and customGlobalSearchSet must be an IList
    //     (Thing/Pawn/Building).
    //   * Global search must actually be allowed.
    //   * Exact ListerHaulables searches are left to the dedicated V0.4.6/V0.4.7 path.
    //   * Before every accelerated call, the source list is compared exactly against
    //     the published snapshot (same refs and same positions). Any change falls back
    //     to Vanilla for that call and schedules a background rebuild.
    //   * The worker never dereferences Verse objects. It groups immutable integer
    //     coordinates/source indices only.
    internal static class AdaptiveGenClosestAssist
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int BucketSize = 16;
        private const int MinCandidateCount = 96;
        private const int MaxConcurrentBuilds = 4;

        private static readonly ConditionalWeakTable<object, SourceState> States =
            new ConditionalWeakTable<object, SourceState>();

        private static volatile bool compatibilityReady;
        private static int activeBuilds;

        [ThreadStatic]
        private static int assistDepth;

        private static long observedCalls;
        private static long eligibleCalls;
        private static long acceleratedCalls;
        private static long acceleratedNoResult;
        private static long fallbackCalls;
        private static long nonListBypasses;
        private static long shapeBypasses;
        private static long smallSetBypasses;
        private static long haulableBypasses;
        private static long cacheMisses;
        private static long cacheHits;
        private static long cacheInvalidations;
        private static long buildsScheduled;
        private static long buildsPublished;
        private static long buildsDiscarded;
        private static long buildsRejected;
        private static long buildBusyBypasses;
        private static long snapshotCaptures;
        private static long snapshotCandidates;
        private static long snapshotCaptureTicks;
        private static long validationPasses;
        private static long validationFailures;
        private static long validationTicks;
        private static long queryTicks;
        private static long queryTicksMax;
        private static long bucketVisits;
        private static long candidatesVisited;
        private static long candidatesAvoided;
        private static long reachabilityChecks;
        private static long validatorChecks;
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
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.13 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AdaptiveGenClosestAssist), nameof(Prefix));
                prefix.priority = Priority.First + 100;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[RimMT] parallel.jobPartition V0.4.13 true-offload installed. Repeated IList-backed custom global searches may use a worker-built immutable spatial index. The main thread never waits for index construction; any missing/stale snapshot falls back to Vanilla. Live Reachability, validators, reservations and final Jobs remain main-thread authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "true-offload GenClosest patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.13 patch failed; Vanilla GenClosest remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
        }

        public static bool Prefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator,
            IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMin,
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions,
            ref Thing __result)
        {
            Interlocked.Increment(ref observedCalls);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            // With an undefined ThingRequest there is no region BFS branch; when global
            // search is allowed Vanilla goes directly to ClosestThing_Global over the
            // custom set. That exact shape is what this accelerator reproduces.
            if (!thingReq.IsUndefined ||
                traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref shapeBypasses);
                return true;
            }

            object source = customGlobalSearchSet;
            SourceKind kind;
            int count;
            if (!TryGetSourceShape(source, out kind, out count))
            {
                Interlocked.Increment(ref nonListBypasses);
                return true;
            }

            if (count < MinCandidateCount)
            {
                Interlocked.Increment(ref smallSetBypasses);
                return true;
            }

            // Exact hauling lists already have a stronger dedicated accelerator.
            IList<Thing> thingList = source as IList<Thing>;
            if (thingList != null)
            {
                try
                {
                    List<Thing> haulables = map.listerHaulables == null
                        ? null
                        : map.listerHaulables.ThingsPotentiallyNeedingHauling();
                    if (haulables != null && ReferenceEquals(source, haulables))
                    {
                        Interlocked.Increment(ref haulableBypasses);
                        return true;
                    }
                }
                catch
                {
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }
            }

            if (assistDepth != 0)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            Interlocked.Increment(ref eligibleCalls);
            SourceState state = States.GetValue(source, CreateState);
            EnsureMapIdentity(state, map);

            SpatialIndex index = state.Index;
            if (index == null || index.Count != count)
            {
                Interlocked.Increment(ref cacheMisses);
                EnsureBuildScheduled(source, kind, count, map, state);
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            assistDepth = 1;
            try
            {
                long validationStart = Stopwatch.GetTimestamp();
                bool valid = ValidateSnapshot(source, kind, count, map, index);
                Interlocked.Add(ref validationTicks, Stopwatch.GetTimestamp() - validationStart);
                if (!valid)
                {
                    Interlocked.Increment(ref validationFailures);
                    Invalidate(state);
                    EnsureBuildScheduled(source, kind, count, map, state);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                Interlocked.Increment(ref validationPasses);
                Interlocked.Increment(ref cacheHits);

                long queryStart = Stopwatch.GetTimestamp();
                Thing chosen;
                int visited;
                int bucketsSeen;
                int reaches;
                int validations;
                bool ok = index.TryFindClosest(
                    root, map, peMode, traverseParams, maxDistance, validator,
                    out chosen, out visited, out bucketsSeen, out reaches, out validations);
                long queryElapsed = Stopwatch.GetTimestamp() - queryStart;
                Interlocked.Add(ref queryTicks, queryElapsed);
                UpdateMax(ref queryTicksMax, queryElapsed);

                if (!ok)
                {
                    Invalidate(state);
                    EnsureBuildScheduled(source, kind, count, map, state);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                __result = chosen;
                Interlocked.Increment(ref acceleratedCalls);
                if (chosen == null)
                    Interlocked.Increment(ref acceleratedNoResult);
                Interlocked.Add(ref bucketVisits, bucketsSeen);
                Interlocked.Add(ref candidatesVisited, visited);
                Interlocked.Add(ref reachabilityChecks, reaches);
                Interlocked.Add(ref validatorChecks, validations);
                long avoided = count - visited;
                if (avoided > 0)
                    Interlocked.Add(ref candidatesAvoided, avoided);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.13 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                assistDepth = 0;
            }
        }

        private static SourceState CreateState(object source)
        {
            return new SourceState();
        }

        private static void EnsureMapIdentity(SourceState state, Map map)
        {
            if (state.MapId == map.uniqueID && state.Width == map.Size.x && state.Height == map.Size.z)
                return;

            state.MapId = map.uniqueID;
            state.Width = map.Size.x;
            state.Height = map.Size.z;
            state.Index = null;
            Interlocked.Increment(ref state.BuildToken);
        }

        private static bool TryGetSourceShape(object source, out SourceKind kind, out int count)
        {
            IList<Thing> things = source as IList<Thing>;
            if (things != null)
            {
                kind = SourceKind.Thing;
                count = things.Count;
                return true;
            }

            IList<Pawn> pawns = source as IList<Pawn>;
            if (pawns != null)
            {
                kind = SourceKind.Pawn;
                count = pawns.Count;
                return true;
            }

            IList<Building> buildings = source as IList<Building>;
            if (buildings != null)
            {
                kind = SourceKind.Building;
                count = buildings.Count;
                return true;
            }

            kind = SourceKind.None;
            count = 0;
            return false;
        }

        private static Thing GetThingAt(object source, SourceKind kind, int index)
        {
            switch (kind)
            {
                case SourceKind.Thing:
                    return ((IList<Thing>)source)[index];
                case SourceKind.Pawn:
                    return ((IList<Pawn>)source)[index];
                case SourceKind.Building:
                    return ((IList<Building>)source)[index];
                default:
                    return null;
            }
        }

        private static bool ValidateSnapshot(object source, SourceKind kind, int count, Map map, SpatialIndex index)
        {
            if (index == null || index.Count != count || index.MapId != map.uniqueID ||
                index.Width != map.Size.x || index.Height != map.Size.z)
                return false;

            for (int i = 0; i < count; i++)
            {
                Thing current = GetThingAt(source, kind, i);
                if (current == null || !ReferenceEquals(current, index.SourceThings[i]))
                    return false;

                IntVec3 pos = current.PositionHeld;
                if (!pos.IsValid || !pos.InBounds(map) ||
                    pos.x != index.SourceXs[i] || pos.z != index.SourceZs[i])
                    return false;
            }

            return true;
        }

        private static void EnsureBuildScheduled(
            object source,
            SourceKind kind,
            int count,
            Map map,
            SourceState state)
        {
            if (count < MinCandidateCount || Volatile.Read(ref state.BuildInFlight) != 0)
                return;

            if (Volatile.Read(ref activeBuilds) >= MaxConcurrentBuilds)
            {
                Interlocked.Increment(ref buildBusyBypasses);
                return;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
                return;

            Thing[] things = new Thing[count];
            int[] xs = new int[count];
            int[] zs = new int[count];

            long captureStart = Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing thing = GetThingAt(source, kind, i);
                    if (thing == null)
                        return;

                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid || !pos.InBounds(map))
                        return;

                    things[i] = thing;
                    xs[i] = pos.x;
                    zs[i] = pos.z;
                }
            }
            finally
            {
                Interlocked.Add(ref snapshotCaptureTicks, Stopwatch.GetTimestamp() - captureStart);
            }

            Interlocked.Increment(ref snapshotCaptures);
            Interlocked.Add(ref snapshotCandidates, count);

            int token = Interlocked.Increment(ref state.BuildToken);
            Volatile.Write(ref state.BuildInFlight, 1);
            Interlocked.Increment(ref activeBuilds);
            Interlocked.Increment(ref buildsScheduled);

            int mapId = map.uniqueID;
            int width = map.Size.x;
            int height = map.Size.z;

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                SpatialIndex built = SpatialIndex.Build(mapId, width, height, things, xs, zs);

                bool queued = MainThreadDispatcher.TryEnqueue(delegate
                {
                    try
                    {
                        if (Volatile.Read(ref state.BuildToken) != token ||
                            map.Disposed || map.uniqueID != mapId ||
                            map.Size.x != width || map.Size.z != height)
                        {
                            Interlocked.Increment(ref buildsDiscarded);
                            return;
                        }

                        state.Index = built;
                        Interlocked.Increment(ref buildsPublished);
                    }
                    finally
                    {
                        Volatile.Write(ref state.BuildInFlight, 0);
                        Interlocked.Decrement(ref activeBuilds);
                    }
                });

                if (!queued)
                {
                    Volatile.Write(ref state.BuildInFlight, 0);
                    Interlocked.Decrement(ref activeBuilds);
                    Interlocked.Increment(ref buildsDiscarded);
                }
            });

            if (!accepted)
            {
                Volatile.Write(ref state.BuildInFlight, 0);
                Interlocked.Decrement(ref activeBuilds);
                Interlocked.Increment(ref buildsRejected);
            }
        }

        private static void Invalidate(SourceState state)
        {
            if (state.Index != null)
            {
                state.Index = null;
                Interlocked.Increment(ref cacheInvalidations);
            }
            Interlocked.Increment(ref state.BuildToken);
        }

        internal static string Summary()
        {
            long accelerated = Interlocked.Read(ref acceleratedCalls);
            long visited = Interlocked.Read(ref candidatesVisited);
            long avoided = Interlocked.Read(ref candidatesAvoided);
            long validations = Interlocked.Read(ref validationPasses);
            long captures = Interlocked.Read(ref snapshotCaptures);

            double avgVisited = accelerated <= 0 ? 0.0 : visited / (double)accelerated;
            double avgAvoided = accelerated <= 0 ? 0.0 : avoided / (double)accelerated;
            double avgValidationUs = validations <= 0
                ? 0.0
                : (Interlocked.Read(ref validationTicks) * 1000000.0 / Stopwatch.Frequency) / validations;
            double avgQueryUs = accelerated <= 0
                ? 0.0
                : (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / accelerated;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgCaptureUs = captures <= 0
                ? 0.0
                : (Interlocked.Read(ref snapshotCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / captures;

            return "Background GenClosest true-offload V0.4.13: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", eligible=" + Interlocked.Read(ref eligibleCalls) +
                ", accelerated=" + accelerated +
                ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", nonListBypass=" + Interlocked.Read(ref nonListBypasses) +
                ", shapeBypass=" + Interlocked.Read(ref shapeBypasses) +
                ", smallSetBypass=" + Interlocked.Read(ref smallSetBypasses) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", cacheHits=" + Interlocked.Read(ref cacheHits) +
                ", cacheMisses=" + Interlocked.Read(ref cacheMisses) +
                ", invalidations=" + Interlocked.Read(ref cacheInvalidations) +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
                ", buildsPublished=" + Interlocked.Read(ref buildsPublished) +
                ", buildsDiscarded=" + Interlocked.Read(ref buildsDiscarded) +
                ", buildsRejected=" + Interlocked.Read(ref buildsRejected) +
                ", buildBusyBypass=" + Interlocked.Read(ref buildBusyBypasses) +
                ", activeBuilds=" + Volatile.Read(ref activeBuilds) +
                ", snapshotCaptures=" + captures +
                ", snapshotCandidates=" + Interlocked.Read(ref snapshotCandidates) +
                ", avgCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", validationPasses=" + validations +
                ", validationFailures=" + Interlocked.Read(ref validationFailures) +
                ", avgValidationUs=" + avgValidationUs.ToString("F2") +
                ", avgQueryUs=" + avgQueryUs.ToString("F2") +
                ", maxQueryUs=" + maxQueryUs.ToString("F2") +
                ", bucketVisits=" + Interlocked.Read(ref bucketVisits) +
                ", candidatesVisited=" + visited +
                ", avgCandidatesVisited=" + avgVisited.ToString("F1") +
                ", candidatesAvoided=" + avoided +
                ", avgCandidatesAvoided=" + avgAvoided.ToString("F1") +
                ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) +
                ", validatorChecks=" + Interlocked.Read(ref validatorChecks) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Index construction runs on RimMT workers at normal priority and is never awaited by the main thread. Snapshot validation and live Reachability/validator/final selection remain main-thread authoritative.";
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

        private enum SourceKind
        {
            None,
            Thing,
            Pawn,
            Building
        }

        private sealed class SourceState
        {
            internal int MapId = int.MinValue;
            internal int Width;
            internal int Height;
            internal int BuildToken;
            internal int BuildInFlight;
            internal SpatialIndex Index;
        }

        private sealed class SpatialIndex
        {
            private readonly int bucketCols;
            private readonly int bucketRows;
            private readonly int[] bucketOffsets;
            private readonly int[] sourceIndices;

            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly Thing[] SourceThings;
            internal readonly int[] SourceXs;
            internal readonly int[] SourceZs;
            internal int Count { get { return SourceThings.Length; } }

            private SpatialIndex(
                int mapId,
                int width,
                int height,
                Thing[] things,
                int[] xs,
                int[] zs,
                int bucketCols,
                int bucketRows,
                int[] bucketOffsets,
                int[] sourceIndices)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                SourceThings = things;
                SourceXs = xs;
                SourceZs = zs;
                this.bucketCols = bucketCols;
                this.bucketRows = bucketRows;
                this.bucketOffsets = bucketOffsets;
                this.sourceIndices = sourceIndices;
            }

            internal static SpatialIndex Build(
                int mapId,
                int width,
                int height,
                Thing[] things,
                int[] xs,
                int[] zs)
            {
                int cols = Math.Max(1, (width + BucketSize - 1) / BucketSize);
                int rows = Math.Max(1, (height + BucketSize - 1) / BucketSize);
                int bucketCount = cols * rows;
                int[] counts = new int[bucketCount];

                for (int i = 0; i < things.Length; i++)
                {
                    int key = (xs[i] / BucketSize) + (zs[i] / BucketSize) * cols;
                    counts[key]++;
                }

                int[] offsets = new int[bucketCount + 1];
                int sum = 0;
                for (int i = 0; i < bucketCount; i++)
                {
                    offsets[i] = sum;
                    sum += counts[i];
                }
                offsets[bucketCount] = sum;

                int[] write = new int[bucketCount];
                Array.Copy(offsets, write, bucketCount);
                int[] indices = new int[things.Length];
                for (int i = 0; i < things.Length; i++)
                {
                    int key = (xs[i] / BucketSize) + (zs[i] / BucketSize) * cols;
                    indices[write[key]++] = i;
                }

                return new SpatialIndex(mapId, width, height, things, xs, zs, cols, rows, offsets, indices);
            }

            internal bool TryFindClosest(
                IntVec3 root,
                Map map,
                PathEndMode peMode,
                TraverseParms traverseParams,
                float maxDistance,
                Predicate<Thing> validator,
                out Thing chosen,
                out int visited,
                out int bucketsSeen,
                out int reaches,
                out int validations)
            {
                chosen = null;
                visited = 0;
                bucketsSeen = 0;
                reaches = 0;
                validations = 0;

                if (map == null || map.uniqueID != MapId || Width != map.Size.x ||
                    Height != map.Size.z || !root.InBounds(map))
                    return false;

                float maxDistanceSquared = maxDistance * maxDistance;
                float bestDistanceSquared = float.MaxValue;
                int bestSourceIndex = int.MaxValue;

                int rootBucketX = root.x / BucketSize;
                int rootBucketZ = root.z / BucketSize;
                int maxRing = Math.Max(
                    Math.Max(rootBucketX, bucketCols - 1 - rootBucketX),
                    Math.Max(rootBucketZ, bucketRows - 1 - rootBucketZ));

                for (int ring = 0; ring <= maxRing; ring++)
                {
                    int minBx = Math.Max(0, rootBucketX - ring);
                    int maxBx = Math.Min(bucketCols - 1, rootBucketX + ring);
                    int minBz = Math.Max(0, rootBucketZ - ring);
                    int maxBz = Math.Min(bucketRows - 1, rootBucketZ + ring);

                    if (ring == 0)
                    {
                        ProcessBucket(
                            rootBucketX, rootBucketZ, root, map, peMode, traverseParams,
                            maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                            ref bestSourceIndex, ref visited, ref bucketsSeen, ref reaches, ref validations);
                    }
                    else
                    {
                        for (int bx = minBx; bx <= maxBx; bx++)
                        {
                            ProcessBucket(
                                bx, minBz, root, map, peMode, traverseParams,
                                maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                                ref bestSourceIndex, ref visited, ref bucketsSeen, ref reaches, ref validations);

                            if (maxBz != minBz)
                                ProcessBucket(
                                    bx, maxBz, root, map, peMode, traverseParams,
                                    maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                                    ref bestSourceIndex, ref visited, ref bucketsSeen, ref reaches, ref validations);
                        }

                        for (int bz = minBz + 1; bz < maxBz; bz++)
                        {
                            ProcessBucket(
                                minBx, bz, root, map, peMode, traverseParams,
                                maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                                ref bestSourceIndex, ref visited, ref bucketsSeen, ref reaches, ref validations);

                            if (maxBx != minBx)
                                ProcessBucket(
                                    maxBx, bz, root, map, peMode, traverseParams,
                                    maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                                    ref bestSourceIndex, ref visited, ref bucketsSeen, ref reaches, ref validations);
                        }
                    }

                    float outsideMin = MinimumOutsideDistanceSquared(root, minBx, maxBx, minBz, maxBz);
                    if (chosen != null && outsideMin > bestDistanceSquared)
                        break;
                    if (chosen == null && outsideMin > maxDistanceSquared)
                        break;
                }

                return true;
            }

            private void ProcessBucket(
                int bx,
                int bz,
                IntVec3 root,
                Map map,
                PathEndMode peMode,
                TraverseParms traverseParams,
                float maxDistanceSquared,
                Predicate<Thing> validator,
                ref Thing chosen,
                ref float bestDistanceSquared,
                ref int bestSourceIndex,
                ref int visited,
                ref int bucketsSeen,
                ref int reaches,
                ref int validations)
            {
                if (bx < 0 || bz < 0 || bx >= bucketCols || bz >= bucketRows)
                    return;

                int key = bx + bz * bucketCols;
                int start = bucketOffsets[key];
                int end = bucketOffsets[key + 1];
                if (start == end)
                    return;

                bucketsSeen++;
                for (int p = start; p < end; p++)
                {
                    int sourceIndex = sourceIndices[p];
                    Thing thing = SourceThings[sourceIndex];
                    visited++;

                    if (thing == null)
                        continue;
                    if (!thing.Spawned && !HaulAIUtility.IsInHaulableInventory(thing))
                        continue;

                    IntVec3 pos = thing.PositionHeld;
                    float distanceSquared = (float)(root - pos).LengthHorizontalSquared;

                    if (distanceSquared > maxDistanceSquared)
                        continue;

                    if (distanceSquared > bestDistanceSquared)
                        continue;

                    // Vanilla ClosestThing_Global keeps the first candidate at an equal
                    // distance. Preserve that tie-break even though buckets change scan order.
                    if (distanceSquared == bestDistanceSquared && sourceIndex >= bestSourceIndex)
                        continue;

                    reaches++;
                    if (!map.reachability.CanReach(root, (LocalTargetInfo)thing, peMode, traverseParams))
                        continue;

                    if (validator != null)
                    {
                        validations++;
                        if (!validator(thing))
                            continue;
                    }

                    chosen = thing;
                    bestDistanceSquared = distanceSquared;
                    bestSourceIndex = sourceIndex;
                }
            }

            private float MinimumOutsideDistanceSquared(
                IntVec3 root,
                int minBx,
                int maxBx,
                int minBz,
                int maxBz)
            {
                int minX = minBx * BucketSize;
                int maxX = Math.Min(Width - 1, (maxBx + 1) * BucketSize - 1);
                int minZ = minBz * BucketSize;
                int maxZ = Math.Min(Height - 1, (maxBz + 1) * BucketSize - 1);
                long best = long.MaxValue;

                if (minBx > 0)
                {
                    long dx = root.x - (minX - 1);
                    best = Math.Min(best, dx * dx);
                }
                if (maxBx < bucketCols - 1)
                {
                    long dx = (maxX + 1) - root.x;
                    best = Math.Min(best, dx * dx);
                }
                if (minBz > 0)
                {
                    long dz = root.z - (minZ - 1);
                    best = Math.Min(best, dz * dz);
                }
                if (maxBz < bucketRows - 1)
                {
                    long dz = (maxZ + 1) - root.z;
                    best = Math.Min(best, dz * dz);
                }

                return best == long.MaxValue ? float.MaxValue : (float)best;
            }
        }
    }
}
