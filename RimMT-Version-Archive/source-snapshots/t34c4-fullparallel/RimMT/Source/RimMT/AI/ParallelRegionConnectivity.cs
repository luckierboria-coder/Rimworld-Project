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
    // V0.4.15: permissive connectivity is an off-thread NEGATIVE hint only.
    //
    // Main thread: clone the primitive normal PathGrid array at a stable topology generation.
    // Workers: label independent horizontal stripes, then merge stripe boundaries and publish
    // an immutable component array. No worker dereferences Map, Thing, Pawn, Region or Def state.
    //
    // The graph deliberately contains at least as many routes as ordinary pawn reachability:
    // normal PathGrid, 8-neighbour connectivity, no pawn/danger/door/fence Region.Allows tests.
    // Consequently, different components can prove a candidate is unreachable in supported
    // non-destroy-through modes. Same-component candidates are NEVER accepted by the snapshot;
    // they still go through live Vanilla Reachability.CanReach and the original validator.
    internal static class ParallelRegionConnectivity
    {
        internal const string FeatureId = "parallel.regionHint";

        private const int ImpassableCost = 10000;
        private const int MinSourceCount = 96;
        private const int MaxSourceCount = 4096;
        private const int MaxLiveCandidates = 64;
        private const int MaxSnapshotCells = 160000;
        private const long RebuildCooldownFrames = 30;

        private static readonly ConditionalWeakTable<Map, MapState> States =
            new ConditionalWeakTable<Map, MapState>();
        private static readonly ThreadLocal<List<Candidate>> CandidateScratch =
            new ThreadLocal<List<Candidate>>(() => new List<Candidate>(128));
        private static readonly ThreadLocal<int[]> RootComponentScratch =
            new ThreadLocal<int[]>(() => new int[9]);
        private static readonly ThreadLocal<int[]> TargetComponentScratch =
            new ThreadLocal<int[]>(() => new int[9]);

        private static volatile bool compatibilityReady;

        private static long observed;
        private static long eligible;
        private static long accelerated;
        private static long acceleratedNoResult;
        private static long fallback;
        private static long unsupportedMode;
        private static long sourceShapeBypass;
        private static long sourceTooSmall;
        private static long sourceTooLarge;
        private static long snapshotTooLarge;
        private static long snapshotMiss;
        private static long rootUnclassified;
        private static long buildCooldownBypass;
        private static long buildScheduled;
        private static long buildRejected;
        private static long stripeTasks;
        private static long mergeTasks;
        private static long buildPublished;
        private static long buildStale;
        private static long workerFailures;
        private static long topologyRejected;
        private static long potentialCandidates;
        private static long broadSameComponentBypass;
        private static long reachChecks;
        private static long validatorChecks;
        private static long captureTicks;
        private static long captureTicksMax;
        private static long queryTicks;
        private static long queryTicksMax;
        private static long mergeTicks;
        private static long mergeTicksMax;

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
                    Log.Warning("[RimMT] V0.4.15 parallel region hint unavailable: GenClosest target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(ParallelRegionConnectivity), nameof(Prefix));
                // The V0.4.14 persistent-fabric prefix runs first. If it handles a bounded query,
                // Harmony skips this stateful prefix. If it falls back, this stage may still prune
                // definitely disconnected candidates.
                prefix.priority = Priority.Last - 100;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[RimMT] parallel.regionHint V0.4.15 installed. Immutable permissive connectivity snapshots are built in parallel worker stripes; only proven-disconnected candidates are pruned before live Vanilla Reachability.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "parallel region hint patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.regionHint V0.4.15 patch failed; Vanilla remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
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
            Interlocked.Increment(ref observed);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            if (map == null || map.Disposed || customGlobalSearchSet == null ||
                !root.IsValid || !root.InBounds(map) || !thingReq.IsUndefined ||
                traversableRegionTypes != RegionType.Set_Passable || ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref fallback);
                return true;
            }

            if (!SupportedTraverseMode(traverseParams.mode) ||
                (peMode != PathEndMode.OnCell && peMode != PathEndMode.Touch && peMode != PathEndMode.ClosestTouch))
            {
                Interlocked.Increment(ref unsupportedMode);
                return true;
            }

            SourceKind kind;
            int count;
            object source = customGlobalSearchSet;
            if (!TryGetSourceShape(source, out kind, out count))
            {
                Interlocked.Increment(ref sourceShapeBypass);
                return true;
            }
            if (count < MinSourceCount)
            {
                Interlocked.Increment(ref sourceTooSmall);
                return true;
            }
            if (count > MaxSourceCount)
            {
                Interlocked.Increment(ref sourceTooLarge);
                return true;
            }

            Interlocked.Increment(ref eligible);

            ConnectivitySnapshot snapshot;
            if (!TryGetSnapshot(map, out snapshot))
            {
                EnsureBuildScheduled(map);
                Interlocked.Increment(ref snapshotMiss);
                Interlocked.Increment(ref fallback);
                return true;
            }

            int[] rootComponents = RootComponentScratch.Value;
            int rootCount = snapshot.GatherComponents(root, rootComponents);
            if (rootCount == 0)
            {
                Interlocked.Increment(ref rootUnclassified);
                Interlocked.Increment(ref fallback);
                return true;
            }

            long started = Stopwatch.GetTimestamp();
            List<Candidate> candidates = CandidateScratch.Value;
            int[] targetComponents = TargetComponentScratch.Value;
            candidates.Clear();
            float maxDistanceSquared = maxDistance * maxDistance;
            long localRejected = 0;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing thing = GetThingAt(source, kind, i);
                    if (thing == null || !thing.Spawned || thing.MapHeld != map)
                    {
                        Interlocked.Increment(ref fallback);
                        return true;
                    }

                    IntVec3 pos = thing.PositionHeld;
                    long dx = root.x - pos.x;
                    long dz = root.z - pos.z;
                    float distSquared = (float)(dx * dx + dz * dz);
                    if (distSquared > maxDistanceSquared)
                        continue;

                    if (snapshot.ProvesDisconnected(rootComponents, rootCount, pos, targetComponents))
                    {
                        localRejected++;
                        continue;
                    }

                    candidates.Add(new Candidate(thing, pos, distSquared, i));
                    if (candidates.Count > MaxLiveCandidates)
                    {
                        Interlocked.Add(ref topologyRejected, localRejected);
                        Interlocked.Add(ref potentialCandidates, candidates.Count);
                        Interlocked.Increment(ref broadSameComponentBypass);
                        Interlocked.Increment(ref fallback);
                        return true;
                    }
                }

                Interlocked.Add(ref topologyRejected, localRejected);
                Interlocked.Add(ref potentialCandidates, candidates.Count);
                candidates.Sort(CandidateComparer.Instance);

                int localReach = 0;
                int localValidator = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Candidate candidate = candidates[i];
                    Thing thing = candidate.Thing;
                    if (thing == null || !thing.Spawned || thing.MapHeld != map || thing.PositionHeld != candidate.Position)
                    {
                        Interlocked.Increment(ref fallback);
                        return true;
                    }

                    localReach++;
                    if (!map.reachability.CanReach(root, (LocalTargetInfo)thing, peMode, traverseParams))
                        continue;

                    if (validator != null)
                    {
                        localValidator++;
                        if (!validator(thing))
                            continue;
                    }

                    __result = thing;
                    Interlocked.Increment(ref accelerated);
                    Interlocked.Add(ref reachChecks, localReach);
                    Interlocked.Add(ref validatorChecks, localValidator);
                    RecordQueryTicks(started);
                    return false;
                }

                __result = null;
                Interlocked.Increment(ref accelerated);
                Interlocked.Increment(ref acceleratedNoResult);
                Interlocked.Add(ref reachChecks, localReach);
                Interlocked.Add(ref validatorChecks, localValidator);
                RecordQueryTicks(started);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref fallback);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.regionHint V0.4.15 query failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                candidates.Clear();
            }
        }

        private static bool SupportedTraverseMode(TraverseMode mode)
        {
            return mode != TraverseMode.PassAllDestroyableThings &&
                mode != TraverseMode.PassAllDestroyablePlayerOwnedThings &&
                mode != TraverseMode.PassAllDestroyableThingsNotWater;
        }

        private static void EnsureBuildScheduled(Map map)
        {
            if (map == null || map.Disposed || !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled(FeatureId))
                return;

            int cells = map.Size.x * map.Size.z;
            if (cells <= 0 || cells > MaxSnapshotCells)
            {
                Interlocked.Increment(ref snapshotTooLarge);
                return;
            }

            MapState state = States.GetValue(map, delegate(Map m)
            {
                return new MapState(m.uniqueID, m.Size.x, m.Size.z);
            });

            int generationBefore = ReachabilityNoCache.TopologyGeneration;
            ConnectivitySnapshot existing = Volatile.Read(ref state.Published);
            if (existing != null && existing.Generation == generationBefore &&
                existing.MapId == map.uniqueID && existing.Width == map.Size.x && existing.Height == map.Size.z)
                return;

            long nowFrame = RimMTRuntime.MainThreadFrames;
            long last = Interlocked.Read(ref state.LastScheduleFrame);
            if (last != 0 && nowFrame - last < RebuildCooldownFrames)
            {
                Interlocked.Increment(ref buildCooldownBypass);
                return;
            }

            if (Interlocked.CompareExchange(ref state.BuildScheduled, 1, 0) != 0)
                return;

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Volatile.Write(ref state.BuildScheduled, 0);
                Interlocked.Increment(ref buildRejected);
                return;
            }

            int[] live;
            try
            {
                live = map.pathing.Normal.pathGrid.pathGrid;
            }
            catch
            {
                Volatile.Write(ref state.BuildScheduled, 0);
                Interlocked.Increment(ref buildRejected);
                return;
            }

            if (live == null || live.Length != cells)
            {
                Volatile.Write(ref state.BuildScheduled, 0);
                Interlocked.Increment(ref buildRejected);
                return;
            }

            long captureStart = Stopwatch.GetTimestamp();
            int[] raw = new int[live.Length];
            Array.Copy(live, raw, live.Length);
            long captureElapsed = Stopwatch.GetTimestamp() - captureStart;
            Interlocked.Add(ref captureTicks, captureElapsed);
            UpdateMax(ref captureTicksMax, captureElapsed);

            int generationAfter = ReachabilityNoCache.TopologyGeneration;
            if (generationAfter != generationBefore)
            {
                Volatile.Write(ref state.BuildScheduled, 0);
                Interlocked.Increment(ref buildStale);
                return;
            }

            Interlocked.Exchange(ref state.LastScheduleFrame, nowFrame);
            int stripes = Math.Max(1, Math.Min(scheduler.WorkerCount, map.Size.z));
            BuildContext context = new BuildContext(map.uniqueID, map.Size.x, map.Size.z, generationAfter, raw, stripes);
            bool accepted = scheduler.ParallelFor(
                FeatureId,
                0,
                stripes,
                1,
                delegate(int from, int to)
                {
                    for (int stripe = from; stripe < to; stripe++)
                    {
                        LabelStripe(context, stripe);
                        Interlocked.Increment(ref stripeTasks);
                    }
                },
                delegate
                {
                    ScheduleMerge(state, context);
                },
                JobPriority.Normal);

            if (!accepted)
            {
                Volatile.Write(ref state.BuildScheduled, 0);
                Interlocked.Increment(ref buildRejected);
                return;
            }

            Interlocked.Increment(ref buildScheduled);
        }

        private static void LabelStripe(BuildContext context, int stripe)
        {
            int width = context.Width;
            int height = context.Height;
            int zStart = stripe * height / context.Stripes;
            int zEnd = (stripe + 1) * height / context.Stripes;
            int capacity = Math.Max(1, (zEnd - zStart) * width);
            int[] queue = new int[capacity];

            for (int z = zStart; z < zEnd; z++)
            {
                int row = z * width;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x;
                    if (context.Raw[index] >= ImpassableCost || context.Labels[index] != 0)
                        continue;

                    int label = Interlocked.Increment(ref context.LabelCounter);
                    int head = 0;
                    int tail = 0;
                    queue[tail++] = index;
                    context.Labels[index] = label;

                    while (head < tail)
                    {
                        int current = queue[head++];
                        int cz = current / width;
                        int cx = current - cz * width;
                        int minZ = Math.Max(zStart, cz - 1);
                        int maxZ = Math.Min(zEnd - 1, cz + 1);
                        int minX = Math.Max(0, cx - 1);
                        int maxX = Math.Min(width - 1, cx + 1);

                        for (int nz = minZ; nz <= maxZ; nz++)
                        {
                            int nrow = nz * width;
                            for (int nx = minX; nx <= maxX; nx++)
                            {
                                if (nx == cx && nz == cz)
                                    continue;
                                int ni = nrow + nx;
                                if (context.Raw[ni] >= ImpassableCost || context.Labels[ni] != 0)
                                    continue;
                                context.Labels[ni] = label;
                                queue[tail++] = ni;
                            }
                        }
                    }
                }
            }
        }

        private static void ScheduleMerge(MapState state, BuildContext context)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || !scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                MergeAndPublish(state, context);
            }))
            {
                Volatile.Write(ref state.BuildScheduled, 0);
                Interlocked.Increment(ref buildRejected);
            }
        }

        private static void MergeAndPublish(MapState state, BuildContext context)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                Interlocked.Increment(ref mergeTasks);
                if (ReachabilityNoCache.TopologyGeneration != context.Generation)
                {
                    Interlocked.Increment(ref buildStale);
                    return;
                }

                int labelCount = Volatile.Read(ref context.LabelCounter);
                int[] parent = new int[labelCount + 1];
                for (int i = 0; i <= labelCount; i++)
                    parent[i] = i;

                int width = context.Width;
                int height = context.Height;
                for (int stripe = 0; stripe < context.Stripes - 1; stripe++)
                {
                    int upperZ = ((stripe + 1) * height / context.Stripes) - 1;
                    int lowerZ = upperZ + 1;
                    if (upperZ < 0 || lowerZ >= height)
                        continue;

                    int upperRow = upperZ * width;
                    int lowerRow = lowerZ * width;
                    for (int x = 0; x < width; x++)
                    {
                        int a = upperRow + x;
                        if (context.Raw[a] >= ImpassableCost)
                            continue;
                        UnionCells(context, parent, a, lowerRow + x);
                        if (x > 0)
                            UnionCells(context, parent, a, lowerRow + x - 1);
                        if (x + 1 < width)
                            UnionCells(context, parent, a, lowerRow + x + 1);
                    }
                }

                for (int i = 0; i < context.Labels.Length; i++)
                {
                    int label = context.Labels[i];
                    if (label != 0)
                        context.Labels[i] = Find(parent, label);
                }

                if (ReachabilityNoCache.TopologyGeneration != context.Generation)
                {
                    Interlocked.Increment(ref buildStale);
                    return;
                }

                Volatile.Write(ref state.Published, new ConnectivitySnapshot(
                    context.MapId, context.Width, context.Height, context.Generation, context.Labels));
                Interlocked.Increment(ref buildPublished);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.regionHint V0.4.15 worker merge failure; snapshot discarded. " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref mergeTicks, elapsed);
                UpdateMax(ref mergeTicksMax, elapsed);
                Volatile.Write(ref state.BuildScheduled, 0);
            }
        }

        private static void UnionCells(BuildContext context, int[] parent, int a, int b)
        {
            if (b < 0 || b >= context.Raw.Length || context.Raw[b] >= ImpassableCost)
                return;
            int la = context.Labels[a];
            int lb = context.Labels[b];
            if (la == 0 || lb == 0 || la == lb)
                return;
            Union(parent, la, lb);
        }

        private static int Find(int[] parent, int value)
        {
            int root = value;
            while (parent[root] != root)
                root = parent[root];
            while (parent[value] != value)
            {
                int next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra == rb)
                return;
            if (ra < rb)
                parent[rb] = ra;
            else
                parent[ra] = rb;
        }

        private static bool TryGetSnapshot(Map map, out ConnectivitySnapshot snapshot)
        {
            snapshot = null;
            MapState state;
            if (map == null || !States.TryGetValue(map, out state))
                return false;

            snapshot = Volatile.Read(ref state.Published);
            if (snapshot == null || snapshot.Generation != ReachabilityNoCache.TopologyGeneration ||
                snapshot.MapId != map.uniqueID || snapshot.Width != map.Size.x || snapshot.Height != map.Size.z)
            {
                snapshot = null;
                return false;
            }
            return true;
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
                case SourceKind.Thing: return ((IList<Thing>)source)[index];
                case SourceKind.Pawn: return ((IList<Pawn>)source)[index];
                case SourceKind.Building: return ((IList<Building>)source)[index];
                default: return null;
            }
        }

        private static void RecordQueryTicks(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref queryTicks, elapsed);
            UpdateMax(ref queryTicksMax, elapsed);
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen)
                    break;
            }
        }

        internal static string Summary()
        {
            long acceleratedCount = Interlocked.Read(ref accelerated);
            long scheduled = Interlocked.Read(ref buildScheduled);
            long merged = Interlocked.Read(ref mergeTasks);
            double avgCaptureUs = scheduled == 0 ? 0.0 :
                (Interlocked.Read(ref captureTicks) * 1000000.0 / Stopwatch.Frequency) / scheduled;
            double maxCaptureUs = Interlocked.Read(ref captureTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgQueryUs = acceleratedCount == 0 ? 0.0 :
                (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / acceleratedCount;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgMergeUs = merged == 0 ? 0.0 :
                (Interlocked.Read(ref mergeTicks) * 1000000.0 / Stopwatch.Frequency) / merged;
            double maxMergeUs = Interlocked.Read(ref mergeTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Parallel region connectivity V0.4.15: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observed) +
                ", eligible=" + Interlocked.Read(ref eligible) +
                ", accelerated=" + acceleratedCount +
                ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallback) +
                ", unsupportedMode=" + Interlocked.Read(ref unsupportedMode) +
                ", sourceShapeBypass=" + Interlocked.Read(ref sourceShapeBypass) +
                ", sourceTooSmall=" + Interlocked.Read(ref sourceTooSmall) +
                ", sourceTooLarge=" + Interlocked.Read(ref sourceTooLarge) +
                ", snapshotTooLarge=" + Interlocked.Read(ref snapshotTooLarge) +
                ", snapshotMiss=" + Interlocked.Read(ref snapshotMiss) +
                ", rootUnclassified=" + Interlocked.Read(ref rootUnclassified) +
                ", buildCooldownBypass=" + Interlocked.Read(ref buildCooldownBypass) +
                ", buildScheduled=" + scheduled +
                ", buildRejected=" + Interlocked.Read(ref buildRejected) +
                ", stripeTasks=" + Interlocked.Read(ref stripeTasks) +
                ", mergeTasks=" + merged +
                ", published=" + Interlocked.Read(ref buildPublished) +
                ", buildStale=" + Interlocked.Read(ref buildStale) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ", topologyRejected=" + Interlocked.Read(ref topologyRejected) +
                ", potentialCandidates=" + Interlocked.Read(ref potentialCandidates) +
                ", broadSameComponentBypass=" + Interlocked.Read(ref broadSameComponentBypass) +
                ", reachChecks=" + Interlocked.Read(ref reachChecks) +
                ", validatorChecks=" + Interlocked.Read(ref validatorChecks) +
                ", maxLiveCandidates=" + MaxLiveCandidates +
                ", avgCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", maxCaptureUs=" + maxCaptureUs.ToString("F2") +
                ", avgQueryUs=" + avgQueryUs.ToString("F2") +
                ", maxQueryUs=" + maxQueryUs.ToString("F2") +
                ", avgMergeUs=" + avgMergeUs.ToString("F2") +
                ", maxMergeUs=" + maxMergeUs.ToString("F2") +
                ". Worker graph is permissive: different components are a safe negative hint; same components always defer to live Vanilla CanReach.";
        }

        private sealed class MapState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal int BuildScheduled;
            internal long LastScheduleFrame;
            internal ConnectivitySnapshot Published;

            internal MapState(int mapId, int width, int height)
            {
                MapId = mapId;
                Width = width;
                Height = height;
            }
        }

        private sealed class BuildContext
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int Generation;
            internal readonly int[] Raw;
            internal readonly int Stripes;
            internal readonly int[] Labels;
            internal int LabelCounter;

            internal BuildContext(int mapId, int width, int height, int generation, int[] raw, int stripes)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                Generation = generation;
                Raw = raw;
                Stripes = stripes;
                Labels = new int[width * height];
            }
        }

        private sealed class ConnectivitySnapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int Generation;
            private readonly int[] components;

            internal ConnectivitySnapshot(int mapId, int width, int height, int generation, int[] components)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                Generation = generation;
                this.components = components;
            }

            internal int GatherComponents(IntVec3 cell, int[] output)
            {
                int count = 0;
                AddComponent(cell.x, cell.z, output, ref count);
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        AddComponent(cell.x + dx, cell.z + dz, output, ref count);
                    }
                }
                return count;
            }

            internal bool ProvesDisconnected(int[] rootComponents, int rootCount, IntVec3 target, int[] targetScratch)
            {
                int targetCount = GatherComponents(target, targetScratch);
                if (targetCount == 0)
                    return false;

                for (int i = 0; i < rootCount; i++)
                {
                    int rootComponent = rootComponents[i];
                    for (int j = 0; j < targetCount; j++)
                    {
                        if (rootComponent == targetScratch[j])
                            return false;
                    }
                }
                return true;
            }

            private void AddComponent(int x, int z, int[] output, ref int count)
            {
                if (x < 0 || z < 0 || x >= Width || z >= Height)
                    return;
                int component = components[x + z * Width];
                if (component == 0)
                    return;
                for (int i = 0; i < count; i++)
                {
                    if (output[i] == component)
                        return;
                }
                output[count++] = component;
            }
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly IntVec3 Position;
            internal readonly float DistanceSquared;
            internal readonly int SourceIndex;

            internal Candidate(Thing thing, IntVec3 position, float distanceSquared, int sourceIndex)
            {
                Thing = thing;
                Position = position;
                DistanceSquared = distanceSquared;
                SourceIndex = sourceIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();

            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                if (distance != 0)
                    return distance;
                return a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }

        private enum SourceKind
        {
            None,
            Thing,
            Pawn,
            Building
        }
    }
}
