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
    // V0.4.7 production fast path for vanilla JobGiver_Haul.
    //
    // JobGiver_Haul uses GenClosest.ClosestThing_Global_Reachable with the exact
    // ListerHaulables live list. Vanilla therefore performs a global linear candidate
    // walk every time a pawn asks for ordinary hauling work. RimMT keeps every live
    // gameplay decision (reachability, validator and final result) on the main thread,
    // while a bounded worker builds a spatial index from an immutable id/position
    // snapshot. Unsupported calls, priority-based calls, stale indices and any anomaly
    // fall straight back to the original GenClosest implementation.
    internal static class GlobalHaulAccelerator
    {
        private const string FeatureId = "parallel.haulGlobal";
        private const int BucketSize = 16;
        private const int MinCandidateCount = 24;

        private static readonly object Sync = new object();
        private static readonly Dictionary<int, MapState> States = new Dictionary<int, MapState>();
        private static readonly FieldInfo ListerMapField = AccessTools.Field(typeof(ListerHaulables), "map");

        private static volatile bool compatibilityReady;
        private static long observedCalls;
        private static long eligibleCalls;
        private static long acceleratedCalls;
        private static long acceleratedNoResult;
        private static long fallbackCalls;
        private static long wrongSearchSetFallbacks;
        private static long priorityFallbacks;
        private static long smallSetFallbacks;
        private static long noIndexFallbacks;
        private static long staleFallbacks;
        private static long buildsScheduled;
        private static long buildsPublished;
        private static long buildsDiscarded;
        private static long buildsRejected;
        private static long rebuilds;
        private static long incrementalAdds;
        private static long incrementalRemoves;
        private static long invalidations;
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
                int patched = 0;
                List<MethodInfo> methods = AccessTools.GetDeclaredMethods(typeof(GenClosest));
                for (int i = 0; i < methods.Count; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedTarget(method))
                        continue;

                    CompatibilityGuard.RegisterTarget(FeatureId, method);
                    HarmonyMethod prefix = new HarmonyMethod(typeof(GlobalHaulAccelerator), nameof(ClosestThingGlobalReachablePrefix));
                    prefix.priority = Priority.First;
                    harmony.Patch(method, prefix: prefix);
                    patched++;
                }

                if (patched == 0)
                {
                    FeatureGate.Suppress(FeatureId, "ClosestThing_Global_Reachable target not found");
                    Log.Warning("[RimMT] parallel.haulGlobal V0.4.7 unavailable: compatible GenClosest.ClosestThing_Global_Reachable overload not found.");
                    return;
                }

                PatchListerMutation(harmony, "Check");
                PatchListerMutation(harmony, "CheckAdd");
                PatchListerMutation(harmony, "TryRemove");

                Log.Message("[RimMT] parallel.haulGlobal V0.4.7 installed on " + patched + " GenClosest.ClosestThing_Global_Reachable overload(s). Exact ListerHaulables calls may use the worker-built spatial index; live reachability and validators remain main-thread authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "global haul accelerator patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.haulGlobal V0.4.7 patch failed; Vanilla global hauling remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsSupportedTarget(MethodInfo method)
        {
            if (method == null || method.Name != "ClosestThing_Global_Reachable" || method.ReturnType != typeof(Thing))
                return false;

            ParameterInfo[] p = method.GetParameters();
            if (p.Length != 8)
                return false;

            return p[0].ParameterType == typeof(IntVec3) &&
                   p[1].ParameterType == typeof(Map) &&
                   typeof(IEnumerable<Thing>).IsAssignableFrom(p[2].ParameterType) &&
                   p[3].ParameterType == typeof(PathEndMode) &&
                   p[4].ParameterType == typeof(TraverseParms) &&
                   p[5].ParameterType == typeof(float) &&
                   p[6].ParameterType == typeof(Predicate<Thing>);
        }

        private static void PatchListerMutation(Harmony harmony, string methodName)
        {
            MethodBase target = AccessTools.Method(typeof(ListerHaulables), methodName, new Type[] { typeof(Thing) });
            if (target == null)
                throw new MissingMethodException(typeof(ListerHaulables).FullName, methodName);

            HarmonyMethod prefix = new HarmonyMethod(typeof(GlobalHaulAccelerator), nameof(ListerMutationPrefix));
            HarmonyMethod postfix = new HarmonyMethod(typeof(GlobalHaulAccelerator), nameof(ListerMutationPostfix));
            harmony.Patch(target, prefix: prefix, postfix: postfix);
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
        }

        public static bool ClosestThingGlobalReachablePrefix(ref Thing __result, object[] __args)
        {
            Interlocked.Increment(ref observedCalls);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || __args == null || __args.Length != 8)
                return true;

            try
            {
                IntVec3 root = (IntVec3)__args[0];
                Map map = __args[1] as Map;
                IEnumerable<Thing> searchSet = __args[2] as IEnumerable<Thing>;
                PathEndMode peMode = (PathEndMode)__args[3];
                TraverseParms traverseParams = (TraverseParms)__args[4];
                float maxDistance = (float)__args[5];
                Predicate<Thing> validator = __args[6] as Predicate<Thing>;
                object priorityGetter = __args[7];

                if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || searchSet == null)
                {
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                // JobGiver_Haul and non-prioritized work use null priority getter.
                // Prioritized WorkGivers intentionally remain Vanilla because selection semantics differ.
                if (priorityGetter != null)
                {
                    Interlocked.Increment(ref priorityFallbacks);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                List<Thing> haulables = map.listerHaulables == null ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling();
                if (haulables == null || !ReferenceEquals(searchSet, haulables))
                {
                    Interlocked.Increment(ref wrongSearchSetFallbacks);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                Interlocked.Increment(ref eligibleCalls);
                if (haulables.Count < MinCandidateCount)
                {
                    Interlocked.Increment(ref smallSetFallbacks);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                MapState state = GetState(map);
                SpatialIndex index = GetUsableIndex(state, haulables);
                if (index == null)
                {
                    EnsureIndexBuildScheduled(map, state, haulables);
                    Interlocked.Increment(ref noIndexFallbacks);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                Thing chosen;
                int visited;
                int buckets;
                int reaches;
                int validations;
                if (!index.TryFindClosest(root, map, peMode, traverseParams, maxDistance, validator,
                    out chosen, out visited, out buckets, out reaches, out validations))
                {
                    InvalidateIndex(state);
                    EnsureIndexBuildScheduled(map, state, haulables);
                    Interlocked.Increment(ref staleFallbacks);
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
                Interlocked.Increment(ref fallbackCalls);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.haulGlobal V0.4.7 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
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
                    if (ok)
                        Interlocked.Increment(ref incrementalAdds);
                }
                else
                {
                    ok = index.Remove(t);
                    if (ok)
                        Interlocked.Increment(ref incrementalRemoves);
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
                    state = new MapState();
                    state.MapId = map.uniqueID;
                    state.Width = map.Size.x;
                    state.Height = map.Size.z;
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
            if (state.BuildInFlight || haulables == null || haulables.Count < MinCandidateCount)
                return;

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
                return;

            int count = haulables.Count;
            Thing[] things = new Thing[count];
            int[] ids = new int[count];
            int[] xs = new int[count];
            int[] zs = new int[count];

            // Main-thread snapshot: only identity and integer position cross the boundary.
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

            int mapId = map.uniqueID;
            int width = map.Size.x;
            int height = map.Size.z;
            int generation = state.Generation;
            state.BuildInFlight = true;
            Interlocked.Increment(ref buildsScheduled);
            if (state.EverBuilt)
                Interlocked.Increment(ref rebuilds);

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                SpatialIndex built = SpatialIndex.Build(mapId, width, height, generation, things, ids, xs, zs);
                MainThreadDispatcher.TryEnqueue(delegate
                {
                    state.BuildInFlight = false;
                    if (map.Disposed || map.uniqueID != mapId || state.Generation != generation)
                    {
                        Interlocked.Increment(ref buildsDiscarded);
                        return;
                    }

                    state.Index = built;
                    state.EverBuilt = true;
                    Interlocked.Increment(ref buildsPublished);
                });
            });

            if (!accepted)
            {
                state.BuildInFlight = false;
                Interlocked.Increment(ref buildsRejected);
            }
        }

        private static void InvalidateIndex(MapState state)
        {
            if (state != null && state.Index != null)
            {
                state.Index = null;
                Interlocked.Increment(ref invalidations);
            }
        }

        internal static string Summary()
        {
            long accelerated = Interlocked.Read(ref acceleratedCalls);
            long visited = Interlocked.Read(ref candidatesVisited);
            long avoided = Interlocked.Read(ref candidatesAvoided);
            double avgVisited = accelerated <= 0 ? 0.0 : visited / (double)accelerated;
            double avgAvoided = accelerated <= 0 ? 0.0 : avoided / (double)accelerated;

            return "Global haul production V0.4.7: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", eligible=" + Interlocked.Read(ref eligibleCalls) +
                ", accelerated=" + accelerated +
                ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", wrongSet=" + Interlocked.Read(ref wrongSearchSetFallbacks) +
                ", priority=" + Interlocked.Read(ref priorityFallbacks) +
                ", smallSet=" + Interlocked.Read(ref smallSetFallbacks) +
                ", noIndex=" + Interlocked.Read(ref noIndexFallbacks) +
                ", stale=" + Interlocked.Read(ref staleFallbacks) +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
                ", buildsPublished=" + Interlocked.Read(ref buildsPublished) +
                ", buildsDiscarded=" + Interlocked.Read(ref buildsDiscarded) +
                ", buildsRejected=" + Interlocked.Read(ref buildsRejected) +
                ", rebuilds=" + Interlocked.Read(ref rebuilds) +
                ", incrementalAdds=" + Interlocked.Read(ref incrementalAdds) +
                ", incrementalRemoves=" + Interlocked.Read(ref incrementalRemoves) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", bucketVisits=" + Interlocked.Read(ref bucketVisits) +
                ", candidatesVisited=" + visited +
                ", avgCandidatesVisited=" + avgVisited.ToString("F1") +
                ", candidatesAvoided=" + avoided +
                ", avgCandidatesAvoided=" + avgAvoided.ToString("F1") +
                ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) +
                ", validatorChecks=" + Interlocked.Read(ref validatorChecks) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Exact ListerHaulables global searches use a worker-built spatial index; reachability/validator checks stay on the main thread.";
        }

        private sealed class MapState
        {
            internal int MapId;
            internal int Width;
            internal int Height;
            internal int Generation;
            internal bool BuildInFlight;
            internal bool EverBuilt;
            internal SpatialIndex Index;
        }

        private sealed class SpatialIndex
        {
            private readonly int mapId;
            private readonly int width;
            private readonly int height;
            private readonly int bucketCols;
            private readonly int bucketRows;
            private readonly Dictionary<int, List<Thing>> buckets = new Dictionary<int, List<Thing>>();
            private readonly Dictionary<int, int> thingBucketById = new Dictionary<int, int>();
            private readonly Dictionary<int, Thing> thingById = new Dictionary<int, Thing>();

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
            }

            internal static SpatialIndex Build(int mapId, int width, int height, int generation,
                Thing[] things, int[] ids, int[] xs, int[] zs)
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
                    List<Thing> bucket;
                    if (!index.buckets.TryGetValue(key, out bucket))
                    {
                        bucket = new List<Thing>();
                        index.buckets.Add(key, bucket);
                    }
                    bucket.Add(things[i]);
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
                List<Thing> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new List<Thing>();
                    buckets.Add(key, bucket);
                }
                bucket.Add(thing);
                thingBucketById[id] = key;
                thingById[id] = thing;
                return true;
            }

            internal bool Remove(Thing thing)
            {
                return thing != null && RemoveById(thing.thingIDNumber);
            }

            private bool RemoveById(int id)
            {
                int key;
                if (!thingBucketById.TryGetValue(id, out key))
                    return false;

                Thing thing;
                thingById.TryGetValue(id, out thing);
                List<Thing> bucket;
                if (buckets.TryGetValue(key, out bucket))
                {
                    if (thing != null)
                        bucket.Remove(thing);
                    if (bucket.Count == 0)
                        buckets.Remove(key);
                }

                thingBucketById.Remove(id);
                thingById.Remove(id);
                return true;
            }

            internal bool TryFindClosest(IntVec3 root, Map map, PathEndMode peMode, TraverseParms traverseParams,
                float maxDistance, Predicate<Thing> validator, out Thing chosen, out int visited,
                out int bucketsSeen, out int reaches, out int validations)
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
                        if (!ProcessBucket(rootBucketX, rootBucketZ, root, map, peMode, traverseParams,
                            maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                            ref visited, ref bucketsSeen, ref reaches, ref validations))
                            return false;
                    }
                    else
                    {
                        for (int bx = minBx; bx <= maxBx; bx++)
                        {
                            if (!ProcessBucket(bx, minBz, root, map, peMode, traverseParams, maxDistanceSquared,
                                validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
                            if (maxBz != minBz && !ProcessBucket(bx, maxBz, root, map, peMode, traverseParams,
                                maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                                ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
                        }

                        for (int bz = minBz + 1; bz < maxBz; bz++)
                        {
                            if (!ProcessBucket(minBx, bz, root, map, peMode, traverseParams, maxDistanceSquared,
                                validator, ref chosen, ref bestDistanceSquared, ref visited, ref bucketsSeen, ref reaches, ref validations))
                                return false;
                            if (maxBx != minBx && !ProcessBucket(maxBx, bz, root, map, peMode, traverseParams,
                                maxDistanceSquared, validator, ref chosen, ref bestDistanceSquared,
                                ref visited, ref bucketsSeen, ref reaches, ref validations))
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

            private bool ProcessBucket(int bx, int bz, IntVec3 root, Map map, PathEndMode peMode,
                TraverseParms traverseParams, float maxDistanceSquared, Predicate<Thing> validator,
                ref Thing chosen, ref float bestDistanceSquared, ref int visited, ref int bucketsSeen,
                ref int reaches, ref int validations)
            {
                if (bx < 0 || bz < 0 || bx >= bucketCols || bz >= bucketRows)
                    return true;

                int key = bx + bz * bucketCols;
                List<Thing> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                    return true;

                bucketsSeen++;
                for (int i = 0; i < bucket.Count; i++)
                {
                    Thing thing = bucket[i];
                    visited++;
                    if (thing == null)
                        return false;
                    if (!thing.Spawned && !HaulAIUtility.IsInHaulableInventory(thing))
                        return false;

                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid || !pos.InBounds(map) || BucketKey(pos.x, pos.z) != key)
                        return false;

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
