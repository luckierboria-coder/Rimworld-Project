using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.6 first production Work optimization.
    //
    // Vanilla JobGiver_Work routes non-prioritized hauling through
    // GenClosest.ClosestThingReachable with ListerHaulables' custom global list.
    // That path performs a linear pass over every haulable and may run reachability
    // plus WorkGiver validation repeatedly. RimMT keeps the live WorkGiver/Job logic
    // on the main thread, but replaces the full-list candidate walk with a persistent
    // spatial index when the call shape is exactly the vanilla hauling shape.
    //
    // Initial index construction is split: the main thread snapshots only Thing refs
    // and integer positions, a worker groups those immutable values into buckets, and
    // the finished index is published at the normal main-thread dispatcher boundary.
    // Membership changes are then maintained incrementally from ListerHaulables' own
    // Check/CheckAdd/TryRemove methods. Any unsupported/ambiguous state falls back to
    // the original GenClosest method.
    internal static class HaulWorkAccelerator
    {
        private const int BucketSize = 16;
        private const int MinCandidateCount = 32;
        private const string FeatureId = "parallel.jobScan";

        private static readonly object Sync = new object();
        private static readonly Dictionary<int, MapState> States = new Dictionary<int, MapState>();
        private static readonly FieldInfo ListerMapField = AccessTools.Field(typeof(ListerHaulables), "map");

        private static volatile bool compatibilityReady;
        private static long eligibleCalls;
        private static long acceleratedCalls;
        private static long acceleratedNoResult;
        private static long fallbackCalls;
        private static long smallSetFallbacks;
        private static long indexBuildScheduled;
        private static long indexBuildPublished;
        private static long indexBuildDiscarded;
        private static long indexBuildRejected;
        private static long indexRebuilds;
        private static long incrementalAdds;
        private static long incrementalRemoves;
        private static long indexInvalidations;
        private static long bucketVisits;
        private static long candidatesVisited;
        private static long reachabilityChecks;
        private static long validatorChecks;
        private static long candidatesAvoided;
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
                    Log.Warning("[RimMT] parallel.jobScan V0.4.6 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(HaulWorkAccelerator), nameof(ClosestThingReachablePrefix));
                prefix.priority = Priority.First;
                harmony.Patch(target, prefix: prefix);

                PatchListerMutation(harmony, "Check");
                PatchListerMutation(harmony, "CheckAdd");
                PatchListerMutation(harmony, "TryRemove");

                Log.Message("[RimMT] parallel.jobScan V0.4.6 production haul accelerator installed. It remains fail-closed until the compatibility scan finishes; Clean Pathfinding is not bypassed because this feature does not replace PathFinder.FindPath.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "production work accelerator patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobScan V0.4.6 patch failed; Vanilla JobGiver/GenClosest remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchListerMutation(Harmony harmony, string methodName)
        {
            MethodBase target = AccessTools.Method(typeof(ListerHaulables), methodName, new Type[] { typeof(Thing) });
            if (target == null)
                throw new MissingMethodException(typeof(ListerHaulables).FullName, methodName);

            HarmonyMethod prefix = new HarmonyMethod(typeof(HaulWorkAccelerator), nameof(ListerMutationPrefix));
            HarmonyMethod postfix = new HarmonyMethod(typeof(HaulWorkAccelerator), nameof(ListerMutationPostfix));
            harmony.Patch(target, prefix: prefix, postfix: postfix);
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
        }

        public static bool ClosestThingReachablePrefix(
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
            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            // Exact whitelist: only the non-prioritized custom global hauling shape.
            // Everything else, including mod scanners with custom sets, remains Vanilla.
            if (map == null || !root.IsValid || !thingReq.IsUndefined || customGlobalSearchSet == null ||
                traversableRegionTypes != RegionType.Set_Passable || ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            List<Thing> haulables;
            try
            {
                haulables = map.listerHaulables == null ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling();
            }
            catch
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            if (haulables == null || !ReferenceEquals(customGlobalSearchSet, haulables))
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            Interlocked.Increment(ref eligibleCalls);
            if (haulables.Count < MinCandidateCount)
            {
                Interlocked.Increment(ref smallSetFallbacks);
                return true;
            }

            try
            {
                MapState state = GetState(map);
                SpatialIndex index = GetUsableIndex(state, haulables);
                if (index == null)
                {
                    EnsureIndexBuildScheduled(map, state, haulables);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                Thing chosen;
                int visited;
                int buckets;
                int reaches;
                int validations;
                if (!index.TryFindClosest(root, map, peMode, traverseParams, maxDistance, validator, out chosen, out visited, out buckets, out reaches, out validations))
                {
                    InvalidateIndex(state);
                    EnsureIndexBuildScheduled(map, state, haulables);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                __result = chosen;
                Interlocked.Increment(ref acceleratedCalls);
                if (chosen == null)
                    Interlocked.Increment(ref acceleratedNoResult);
                Interlocked.Add(ref bucketVisits, buckets);
                Interlocked.Add(ref candidatesVisited, visited);
                Interlocked.Add(ref reachabilityChecks, reaches);
                Interlocked.Add(ref validatorChecks, validations);
                long avoided = haulables.Count - visited;
                if (avoided > 0)
                    Interlocked.Add(ref candidatesAvoided, avoided);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobScan V0.4.6 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        public static void ListerMutationPrefix(ListerHaulables __instance, ref int __state)
        {
            __state = -1;
            if (__instance == null || !RimMTThreadGuard.IsMainThread)
                return;
            try
            {
                __state = __instance.ThingsPotentiallyNeedingHauling().Count;
            }
            catch
            {
                __state = -1;
            }
        }

        public static void ListerMutationPostfix(ListerHaulables __instance, Thing t, int __state)
        {
            if (__instance == null || __state < 0 || !RimMTThreadGuard.IsMainThread)
                return;

            try
            {
                List<Thing> list = __instance.ThingsPotentiallyNeedingHauling();
                int after = list.Count;
                if (after == __state)
                    return;

                Map map = ListerMapField == null ? null : ListerMapField.GetValue(__instance) as Map;
                if (map == null || map.Disposed)
                    return;

                MapState state = GetState(map);
                state.Generation++;
                SpatialIndex index = state.Index;
                if (index == null)
                    return;

                bool ok;
                if (after > __state)
                {
                    ok = index.Add(t, map);
                    if (ok) Interlocked.Increment(ref incrementalAdds);
                }
                else
                {
                    ok = index.Remove(t);
                    if (ok) Interlocked.Increment(ref incrementalRemoves);
                }

                if (!ok || index.Count != after)
                {
                    InvalidateIndex(state);
                    return;
                }
                index.Generation = state.Generation;
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        }

        private static MapState GetState(Map map)
        {
            lock (Sync)
            {
                MapState state;
                if (!States.TryGetValue(map.uniqueID, out state))
                {
                    state = new MapState(map.uniqueID, map.Size.x, map.Size.z);
                    States.Add(map.uniqueID, state);
                }
                return state;
            }
        }

        private static SpatialIndex GetUsableIndex(MapState state, List<Thing> haulables)
        {
            SpatialIndex index = state.Index;
            if (index == null)
                return null;
            if (index.Generation != state.Generation || index.Count != haulables.Count)
            {
                InvalidateIndex(state);
                return null;
            }
            return index;
        }

        private static void EnsureIndexBuildScheduled(Map map, MapState state, List<Thing> haulables)
        {
            if (state.BuildInFlight || haulables.Count < MinCandidateCount)
                return;

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
                return;

            int count = haulables.Count;
            Thing[] things = new Thing[count];
            int[] ids = new int[count];
            int[] xs = new int[count];
            int[] zs = new int[count];

            for (int i = 0; i < count; i++)
            {
                Thing thing = haulables[i];
                things[i] = thing;
                if (thing == null)
                {
                    ids[i] = -1;
                    xs[i] = int.MinValue;
                    zs[i] = int.MinValue;
                    continue;
                }

                ids[i] = thing.thingIDNumber;
                IntVec3 pos = thing.PositionHeld;
                xs[i] = pos.x;
                zs[i] = pos.z;
            }

            int generation = state.Generation;
            state.BuildInFlight = true;
            state.BuildGeneration = generation;
            Interlocked.Increment(ref indexBuildScheduled);
            if (state.EverBuilt)
                Interlocked.Increment(ref indexRebuilds);

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Background, delegate
            {
                SpatialIndex built = SpatialIndex.Build(map.uniqueID, map.Size.x, map.Size.z, generation, things, ids, xs, zs);
                MainThreadDispatcher.TryEnqueue(delegate
                {
                    state.BuildInFlight = false;
                    if (state.Generation != generation || map.Disposed)
                    {
                        Interlocked.Increment(ref indexBuildDiscarded);
                        return;
                    }
                    state.Index = built;
                    state.EverBuilt = true;
                    Interlocked.Increment(ref indexBuildPublished);
                });
            });

            if (!accepted)
            {
                state.BuildInFlight = false;
                Interlocked.Increment(ref indexBuildRejected);
            }
        }

        private static void InvalidateIndex(MapState state)
        {
            if (state.Index != null)
            {
                state.Index = null;
                Interlocked.Increment(ref indexInvalidations);
            }
        }

        internal static string Summary()
        {
            long accelerated = Interlocked.Read(ref acceleratedCalls);
            long visited = Interlocked.Read(ref candidatesVisited);
            double avgVisited = accelerated <= 0 ? 0.0 : visited / (double)accelerated;
            long avoided = Interlocked.Read(ref candidatesAvoided);
            double avgAvoided = accelerated <= 0 ? 0.0 : avoided / (double)accelerated;

            return "Work search production V0.4.6: compatibilityReady=" + compatibilityReady +
                ", eligible=" + Interlocked.Read(ref eligibleCalls) +
                ", accelerated=" + accelerated +
                ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", smallSetFallback=" + Interlocked.Read(ref smallSetFallbacks) +
                ", buildsScheduled=" + Interlocked.Read(ref indexBuildScheduled) +
                ", buildsPublished=" + Interlocked.Read(ref indexBuildPublished) +
                ", buildsDiscarded=" + Interlocked.Read(ref indexBuildDiscarded) +
                ", buildsRejected=" + Interlocked.Read(ref indexBuildRejected) +
                ", rebuilds=" + Interlocked.Read(ref indexRebuilds) +
                ", incrementalAdds=" + Interlocked.Read(ref incrementalAdds) +
                ", incrementalRemoves=" + Interlocked.Read(ref incrementalRemoves) +
                ", invalidations=" + Interlocked.Read(ref indexInvalidations) +
                ", bucketVisits=" + Interlocked.Read(ref bucketVisits) +
                ", candidatesVisited=" + visited +
                ", avgCandidatesVisited=" + avgVisited.ToString("F1") +
                ", candidatesAvoided=" + avoided +
                ", avgCandidatesAvoided=" + avgAvoided.ToString("F1") +
                ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) +
                ", validatorChecks=" + Interlocked.Read(ref validatorChecks) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Accelerated calls skip Vanilla's full haulable-list GenClosest pass but retain main-thread reachability/WorkGiver validation.";
        }

        private sealed class MapState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal int Generation;
            internal int BuildGeneration;
            internal bool BuildInFlight;
            internal bool EverBuilt;
            internal SpatialIndex Index;

            internal MapState(int mapId, int width, int height)
            {
                MapId = mapId;
                Width = width;
                Height = height;
            }
        }

        private sealed class SpatialIndex
        {
            private readonly int mapId;
            private readonly int width;
            private readonly int height;
            private readonly int bucketCols;
            private readonly int bucketRows;
            private readonly Dictionary<int, List<Thing>> buckets;
            private readonly Dictionary<int, int> thingBucketById;
            private readonly Dictionary<int, Thing> thingById;

            internal int Generation;
            internal int Count { get { return thingBucketById.Count; } }

            private SpatialIndex(int mapId, int width, int height, int generation)
            {
                this.mapId = mapId;
                this.width = width;
                this.height = height;
                Generation = generation;
                bucketCols = Math.Max(1, (width + BucketSize - 1) / BucketSize);
                bucketRows = Math.Max(1, (height + BucketSize - 1) / BucketSize);
                buckets = new Dictionary<int, List<Thing>>();
                thingBucketById = new Dictionary<int, int>();
                thingById = new Dictionary<int, Thing>();
            }

            internal static SpatialIndex Build(int mapId, int width, int height, int generation, Thing[] things, int[] ids, int[] xs, int[] zs)
            {
                SpatialIndex index = new SpatialIndex(mapId, width, height, generation);
                int length = things == null ? 0 : things.Length;
                for (int i = 0; i < length; i++)
                {
                    int id = ids[i];
                    int x = xs[i];
                    int z = zs[i];
                    if (id < 0 || x < 0 || z < 0 || x >= width || z >= height)
                        continue;
                    int key = index.BucketKey(x, z);
                    List<Thing> list;
                    if (!index.buckets.TryGetValue(key, out list))
                    {
                        list = new List<Thing>();
                        index.buckets.Add(key, list);
                    }
                    list.Add(things[i]);
                    index.thingBucketById[id] = key;
                    index.thingById[id] = things[i];
                }
                return index;
            }

            internal bool Add(Thing thing, Map map)
            {
                if (thing == null || map == null || map.uniqueID != mapId)
                    return false;

                IntVec3 pos = thing.PositionHeld;
                if (!pos.IsValid || !pos.InBounds(map))
                    return false;

                int id = thing.thingIDNumber;
                RemoveById(id);
                int key = BucketKey(pos.x, pos.z);
                List<Thing> list;
                if (!buckets.TryGetValue(key, out list))
                {
                    list = new List<Thing>();
                    buckets.Add(key, list);
                }
                list.Add(thing);
                thingBucketById[id] = key;
                thingById[id] = thing;
                return true;
            }

            internal bool Remove(Thing thing)
            {
                if (thing == null)
                    return false;
                return RemoveById(thing.thingIDNumber);
            }

            private bool RemoveById(int id)
            {
                int key;
                if (!thingBucketById.TryGetValue(id, out key))
                    return false;

                Thing thing;
                thingById.TryGetValue(id, out thing);
                List<Thing> list;
                if (buckets.TryGetValue(key, out list))
                {
                    if (thing != null)
                        list.Remove(thing);
                    if (list.Count == 0)
                        buckets.Remove(key);
                }
                thingBucketById.Remove(id);
                thingById.Remove(id);
                return true;
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

                if (map == null || map.uniqueID != mapId || width != map.Size.x || height != map.Size.z || !root.InBounds(map))
                    return false;

                float maxDistanceSquared = maxDistance * maxDistance;
                float bestDistanceSquared = float.MaxValue;
                int rootBucketX = root.x / BucketSize;
                int rootBucketZ = root.z / BucketSize;
                int maxRing = Math.Max(Math.Max(rootBucketX, bucketCols - 1 - rootBucketX), Math.Max(rootBucketZ, bucketRows - 1 - rootBucketZ));

                for (int ring = 0; ring <= maxRing; ring++)
                {
                    int minBx = Math.Max(0, rootBucketX - ring);
                    int maxBx = Math.Min(bucketCols - 1, rootBucketX + ring);
                    int minBz = Math.Max(0, rootBucketZ - ring);
                    int maxBz = Math.Min(bucketRows - 1, rootBucketZ + ring);

                    if (ring == 0)
                    {
                        if (!ProcessBucket(rootBucketX, rootBucketZ, root, map, peMode, traverseParams, maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                            return false;
                    }
                    else
                    {
                        for (int bx = minBx; bx <= maxBx; bx++)
                        {
                            if (!ProcessBucket(bx, minBz, root, map, peMode, traverseParams, maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
                            if (maxBz != minBz && !ProcessBucket(bx, maxBz, root, map, peMode, traverseParams, maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
                        }
                        for (int bz = minBz + 1; bz < maxBz; bz++)
                        {
                            if (!ProcessBucket(minBx, bz, root, map, peMode, traverseParams, maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
                            if (maxBx != minBx && !ProcessBucket(maxBx, bz, root, map, peMode, traverseParams, maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
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

            private bool ProcessBucket(
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
                ref int visited,
                ref int bucketsSeen,
                ref int reaches,
                ref int validations)
            {
                if (bx < 0 || bz < 0 || bx >= bucketCols || bz >= bucketRows)
                    return true;

                List<Thing> list;
                if (!buckets.TryGetValue(bx + bz * bucketCols, out list))
                    return true;

                bucketsSeen++;
                for (int i = 0; i < list.Count; i++)
                {
                    Thing thing = list[i];
                    visited++;
                    if (thing == null)
                        return false;
                    if (!thing.Spawned && !HaulAIUtility.IsInHaulableInventory(thing))
                        return false;

                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid || !pos.InBounds(map) || BucketKey(pos.x, pos.z) != bx + bz * bucketCols)
                        return false; // moved without a ListerHaulables membership transition: fail closed.

                    float distanceSquared = (float)(root - pos).LengthHorizontalSquared;
                    if (distanceSquared > maxDistanceSquared || distanceSquared >= bestDistanceSquared)
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
                }
                return true;
            }

            private int BucketKey(int x, int z)
            {
                return (x / BucketSize) + (z / BucketSize) * bucketCols;
            }

            private float MinimumOutsideDistanceSquared(IntVec3 root, int minBx, int maxBx, int minBz, int maxBz)
            {
                int minX = minBx * BucketSize;
                int maxX = Math.Min(width - 1, (maxBx + 1) * BucketSize - 1);
                int minZ = minBz * BucketSize;
                int maxZ = Math.Min(height - 1, (maxBz + 1) * BucketSize - 1);
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
