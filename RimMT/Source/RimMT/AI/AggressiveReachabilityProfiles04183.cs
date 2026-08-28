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
    // V0.4.18.3: generation-owned reachability profiles with a bounded main-thread capture pump.
    //
    // V0.4.16 proved that sampled positive/negative profile authority can remove a very large
    // amount of live RegionTraverser work. Its remaining weakness was lifecycle: every profile
    // expired after 180 main-thread frames and every refresh synchronously evaluated Region.Allows
    // for the whole map in the requesting CanReach call.
    //
    // V0.4.18.3 changes both properties:
    // - RegionGeneration is the primary validity key. A profile survives while the topology
    //   generation and TraverseKey remain valid.
    // - SoftRefreshFrames requests a background refresh without withdrawing the old profile.
    // - HardMaxAgeFrames is only a defensive age fuse for dynamic state that may not emit a
    //   RegionDirtyer signal.
    // - Region.Allows capture is split into bounded chunks pumped from TickManagerUpdate.
    //   The main thread never waits for a worker and a CanReach request never scans the full
    //   Region table synchronously.
    // - Worker graph construction still consumes only primitive arrays.
    internal static class AggressiveReachabilityProfiles04183
    {
        internal const string FeatureId = AggressiveReachabilityProfiles.FeatureId;

        private const int MaxSnapshotCells = 160000;
        private const long SoftRefreshFrames = 900;
        private const long HardMaxAgeFrames = 3600;
        private const long BuildCooldownFrames = 12;
        private const long MismatchCooldownFrames = 600;
        private const int WarmupSamples = 8;
        private const int SampleMask = 15;
        private const int GlobalMismatchFuse = 16;
        private const int MaxRegionsPerChunk = 64;
        private const double CaptureBudgetMilliseconds = 0.80;

        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();
        private static readonly Queue<PendingCapture> PendingCaptures = new Queue<PendingCapture>();

        private static volatile bool compatibilityReady;

        [ThreadStatic] private static int[] rootRegionScratch;
        [ThreadStatic] private static int[] rootComponentScratch;

        private static long observed;
        private static long eligible;
        private static long priorPrefixOwned;
        private static long immediateHits;
        private static long unsupported;
        private static long profileHits;
        private static long profileMisses;
        private static long profileHardExpired;
        private static long profileSoftRefresh;
        private static long retainedProfileHitsDuringRefresh;
        private static long profileCooldownBypass;
        private static long topologyBuilds;
        private static long topologyStale;
        private static long topologyFailures;
        private static long topologyCaptureTicks;
        private static long topologyCaptureTicksMax;
        private static long captureStarts;
        private static long captureChunks;
        private static long captureRegions;
        private static long captureCompleted;
        private static long captureDiscardedTopology;
        private static long captureDiscardedPawnState;
        private static long captureRejected;
        private static long captureChunkTicks;
        private static long captureChunkTicksMax;
        private static long buildsScheduled;
        private static long buildsPublished;
        private static long buildsRejected;
        private static long buildsStale;
        private static long workerFailures;
        private static long workerBuildTicks;
        private static long workerBuildTicksMax;
        private static long predictedReachable;
        private static long predictedUnreachable;
        private static long authoritativeTrue;
        private static long authoritativeFalse;
        private static long shadowSamples;
        private static long shadowMatches;
        private static long parityMismatches;
        private static long mismatchReachableToFalse;
        private static long mismatchUnreachableToTrue;
        private static long regionDirtyEvents;
        private static long queriesUnknown;
        private static long queryTicks;
        private static long queryTicksMax;
        private static long pumpFrames;
        private static long pumpBudgetYields;
        private static long pendingHighWater;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            PatchRegionDirtySignals(harmony);

            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(Reachability),
                    nameof(Reachability.CanReach),
                    new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });

                if (target == null)
                {
                    FeatureGate.Suppress(FeatureId, "Reachability.CanReach target not found");
                    Log.Warning("[RimMT] parallel.reachProfile V0.4.18.3 unavailable: Reachability.CanReach target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AggressiveReachabilityProfiles04183), nameof(Prefix));
                prefix.priority = Priority.Low;
                HarmonyMethod postfix = new HarmonyMethod(typeof(AggressiveReachabilityProfiles04183), nameof(Postfix));
                postfix.priority = Priority.First;
                harmony.Patch(target, prefix: prefix, postfix: postfix);

                Log.Message("[RimMT] parallel.reachProfile V0.4.18.3 installed. Region generation now owns profile validity; 900-frame refreshes retain the old profile while a bounded main-thread capture pump rebuilds Region.Allows snapshots. 3600 frames is a defensive hard-age fuse. Sampled parity and the global mismatch fuse remain active.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "V0.4.18.3 reachability profile patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.reachProfile V0.4.18.3 patch failed; Vanilla Reachability remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
        }

        internal static void PumpMainThreadBudget()
        {
            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            Interlocked.Increment(ref pumpFrames);
            long frameStart = Stopwatch.GetTimestamp();
            long budgetTicks = (long)(CaptureBudgetMilliseconds * Stopwatch.Frequency / 1000.0);
            if (budgetTicks < 1)
                budgetTicks = 1;

            while (PendingCaptures.Count > 0)
            {
                PendingCapture capture = PendingCaptures.Dequeue();
                if (capture == null)
                    continue;

                if (!ValidateCaptureOwner(capture))
                {
                    Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                    Interlocked.Increment(ref captureDiscardedPawnState);
                    continue;
                }

                if (Interlocked.Read(ref capture.MapState.RegionGeneration) != capture.RegionGeneration ||
                    capture.Topology.RegionGeneration != capture.RegionGeneration)
                {
                    Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                    Interlocked.Increment(ref captureDiscardedTopology);
                    Interlocked.Increment(ref buildsStale);
                    continue;
                }

                long chunkStart = Stopwatch.GetTimestamp();
                int processed = 0;
                bool failed = false;
                try
                {
                    Region[] regions = capture.Topology.RegionRefs;
                    while (capture.NextRegion < regions.Length && processed < MaxRegionsPerChunk)
                    {
                        Region region = regions[capture.NextRegion];
                        if (region == null || !region.valid)
                        {
                            failed = true;
                            break;
                        }

                        int index = capture.NextRegion++;
                        capture.TraverseAllowed[index] = region.Allows(capture.TraverseParams, false);
                        capture.DestinationAllowed[index] = region.Allows(capture.TraverseParams, true);
                        processed++;

                        if (Stopwatch.GetTimestamp() - frameStart >= budgetTicks)
                            break;
                    }
                }
                catch
                {
                    failed = true;
                }

                long chunkElapsed = Stopwatch.GetTimestamp() - chunkStart;
                Interlocked.Increment(ref captureChunks);
                Interlocked.Add(ref captureRegions, processed);
                Interlocked.Add(ref captureChunkTicks, chunkElapsed);
                UpdateMax(ref captureChunkTicksMax, chunkElapsed);

                if (failed)
                {
                    Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                    Interlocked.Increment(ref captureRejected);
                    continue;
                }

                if (Interlocked.Read(ref capture.MapState.RegionGeneration) != capture.RegionGeneration)
                {
                    Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                    Interlocked.Increment(ref captureDiscardedTopology);
                    Interlocked.Increment(ref buildsStale);
                    continue;
                }

                if (capture.NextRegion >= capture.Topology.RegionRefs.Length)
                {
                    Interlocked.Increment(ref captureCompleted);
                    ScheduleWorkerBuild(capture);
                }
                else
                {
                    PendingCaptures.Enqueue(capture);
                }

                if (Stopwatch.GetTimestamp() - frameStart >= budgetTicks)
                {
                    Interlocked.Increment(ref pumpBudgetYields);
                    break;
                }
            }
        }

        public static bool Prefix(
            IntVec3 start,
            LocalTargetInfo dest,
            PathEndMode peMode,
            TraverseParms traverseParams,
            Map ___map,
            bool __runOriginal,
            ref bool __result,
            out ReachSampleState __state)
        {
            __state = default(ReachSampleState);
            Interlocked.Increment(ref observed);

            if (!__runOriginal)
            {
                Interlocked.Increment(ref priorPrefixOwned);
                return true;
            }

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            Pawn pawn = traverseParams.pawn;
            Map map = ___map;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !start.IsValid || !start.InBounds(map) || !dest.IsValid || !dest.Cell.InBounds(map))
            {
                Interlocked.Increment(ref unsupported);
                return true;
            }

            if (!SupportedTraverseMode(traverseParams.mode) || !SupportedPathEndMode(peMode))
            {
                Interlocked.Increment(ref unsupported);
                return true;
            }

            try
            {
                if (ReachabilityImmediate.CanReachImmediate(start, dest, map, peMode, pawn))
                {
                    __result = true;
                    Interlocked.Increment(ref immediateHits);
                    return false;
                }
            }
            catch
            {
                return true;
            }

            Interlocked.Increment(ref eligible);
            long started = Stopwatch.GetTimestamp();
            try
            {
                MapState mapState = MapStates.GetValue(map, delegate(Map m)
                {
                    return new MapState(m.uniqueID, m.Size.x, m.Size.z);
                });
                TraverseKey key = new TraverseKey(traverseParams);
                PawnState pawnState = mapState.Pawns.GetValue(pawn, delegate(Pawn p) { return new PawnState(); });
                ProfileSlot slot = pawnState.GetOrCreate(key);

                long now = RimMTRuntime.MainThreadFrames;
                if (Interlocked.Read(ref slot.DisabledUntilFrame) > now)
                {
                    Interlocked.Increment(ref profileCooldownBypass);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }

                ProfileSnapshot profile = Volatile.Read(ref slot.Published);
                long generation = Interlocked.Read(ref mapState.RegionGeneration);
                if (profile == null || profile.RegionGeneration != generation || profile.MapId != map.uniqueID ||
                    profile.Width != map.Size.x || profile.Height != map.Size.z || !profile.Key.Equals(key))
                {
                    Interlocked.Increment(ref profileMisses);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }

                long age = now - profile.CaptureFrame;
                if (age > HardMaxAgeFrames)
                {
                    Interlocked.Increment(ref profileHardExpired);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }

                if (age > SoftRefreshFrames)
                {
                    if (Volatile.Read(ref slot.BuildScheduled) == 0)
                        Interlocked.Increment(ref profileSoftRefresh);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                }

                if (Volatile.Read(ref slot.BuildScheduled) != 0)
                    Interlocked.Increment(ref retainedProfileHitsDuringRefresh);

                Interlocked.Increment(ref profileHits);
                Prediction prediction = profile.Classify(start, dest, peMode, map, traverseParams);
                if (prediction == Prediction.Unknown)
                {
                    Interlocked.Increment(ref queriesUnknown);
                    return true;
                }

                bool predicted = prediction == Prediction.Reachable;
                if (predicted)
                    Interlocked.Increment(ref predictedReachable);
                else
                    Interlocked.Increment(ref predictedUnreachable);

                int validated = Volatile.Read(ref slot.ValidatedMatches);
                int serial = Interlocked.Increment(ref slot.PredictionSerial);
                bool sample = validated < WarmupSamples || (serial & SampleMask) == 0;
                if (sample)
                {
                    __state = new ReachSampleState(true, predicted, slot);
                    Interlocked.Increment(ref shadowSamples);
                    return true;
                }

                __result = predicted;
                if (predicted)
                    Interlocked.Increment(ref authoritativeTrue);
                else
                    Interlocked.Increment(ref authoritativeFalse);
                return false;
            }
            catch (Exception ex)
            {
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.reachProfile V0.4.18.3 query failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                RecordElapsed(ref queryTicks, ref queryTicksMax, started);
            }
        }

        public static void Postfix(bool __result, ReachSampleState __state)
        {
            if (!__state.Active || __state.Slot == null)
                return;

            if (__result == __state.Predicted)
            {
                Interlocked.Increment(ref shadowMatches);
                Interlocked.Increment(ref __state.Slot.ValidatedMatches);
                return;
            }

            long mismatches = Interlocked.Increment(ref parityMismatches);
            if (__state.Predicted)
                Interlocked.Increment(ref mismatchReachableToFalse);
            else
                Interlocked.Increment(ref mismatchUnreachableToTrue);

            Interlocked.Exchange(ref __state.Slot.ValidatedMatches, 0);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
            Volatile.Write(ref __state.Slot.Published, null);

            if (mismatches >= GlobalMismatchFuse)
            {
                FeatureGate.Suppress(FeatureId, "V0.4.18.3 reachability parity fuse: " + mismatches + " sampled mismatches");
                Log.Warning("[RimMT] parallel.reachProfile V0.4.18.3 disabled by parity fuse after " + mismatches + " sampled mismatches. Vanilla Reachability is authoritative again.");
            }
        }

        private static void EnsureProfileScheduled(
            Map map,
            MapState mapState,
            Pawn pawn,
            TraverseParms traverseParams,
            TraverseKey key,
            ProfileSlot slot)
        {
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled(FeatureId))
                return;

            long now = RimMTRuntime.MainThreadFrames;
            long last = Interlocked.Read(ref slot.LastScheduleFrame);
            if (last != 0 && now - last < BuildCooldownFrames)
                return;
            if (Interlocked.CompareExchange(ref slot.BuildScheduled, 1, 0) != 0)
                return;

            TopologySnapshot topology = EnsureTopology(map, mapState);
            if (topology == null)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                return;
            }

            long generation = Interlocked.Read(ref mapState.RegionGeneration);
            if (topology.RegionGeneration != generation)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                return;
            }

            PendingCapture capture = new PendingCapture(
                map,
                mapState,
                pawn,
                traverseParams,
                key,
                slot,
                topology,
                generation,
                now);
            PendingCaptures.Enqueue(capture);
            Interlocked.Exchange(ref slot.LastScheduleFrame, now);
            Interlocked.Increment(ref captureStarts);
            UpdateMax(ref pendingHighWater, PendingCaptures.Count);
        }

        private static bool ValidateCaptureOwner(PendingCapture capture)
        {
            return capture.Map != null && !capture.Map.Disposed && capture.Pawn != null &&
                capture.Pawn.Spawned && capture.Pawn.Map == capture.Map &&
                capture.Map.uniqueID == capture.MapState.MapId &&
                capture.Map.Size.x == capture.MapState.Width && capture.Map.Size.z == capture.MapState.Height;
        }

        private static void ScheduleWorkerBuild(PendingCapture capture)
        {
            if (!ValidateCaptureOwner(capture) ||
                Interlocked.Read(ref capture.MapState.RegionGeneration) != capture.RegionGeneration)
            {
                Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                Interlocked.Increment(ref captureDiscardedTopology);
                Interlocked.Increment(ref buildsStale);
                return;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
                return;
            }

            ProfileBuildContext context = new ProfileBuildContext(
                capture.Topology.MapId,
                capture.Topology.Width,
                capture.Topology.Height,
                capture.RegionGeneration,
                RimMTRuntime.MainThreadFrames,
                capture.Key,
                capture.Topology.CellRegion,
                capture.Topology.DistrictByRegion,
                capture.Topology.EdgeOffsets,
                capture.Topology.Edges,
                capture.TraverseAllowed,
                capture.DestinationAllowed);

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                BuildAndPublishProfile(capture.MapState, capture.Slot, context);
            });
            if (!accepted)
            {
                Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
                return;
            }

            Interlocked.Increment(ref buildsScheduled);
        }

        private static TopologySnapshot EnsureTopology(Map map, MapState state)
        {
            long generation = Interlocked.Read(ref state.RegionGeneration);
            TopologySnapshot existing = Volatile.Read(ref state.Topology);
            if (existing != null && existing.RegionGeneration == generation && existing.MapId == map.uniqueID &&
                existing.Width == map.Size.x && existing.Height == map.Size.z)
                return existing;

            int cells = map.Size.x * map.Size.z;
            if (cells <= 0 || cells > MaxSnapshotCells)
                return null;

            long started = Stopwatch.GetTimestamp();
            try
            {
                long before = Interlocked.Read(ref state.RegionGeneration);
                Region[] direct = map.regionGrid.DirectGrid;
                if (direct == null || direct.Length != cells)
                    return null;

                Dictionary<Region, int> regionIndex = new Dictionary<Region, int>(ReferenceEqualityComparer<Region>.Instance);
                List<Region> regions = new List<Region>();
                int[] cellRegion = new int[cells];
                for (int i = 0; i < cellRegion.Length; i++)
                    cellRegion[i] = -1;

                for (int i = 0; i < direct.Length; i++)
                {
                    Region region = direct[i];
                    if (region == null || !region.valid)
                        continue;
                    int ordinal;
                    if (!regionIndex.TryGetValue(region, out ordinal))
                    {
                        ordinal = regions.Count;
                        regionIndex.Add(region, ordinal);
                        regions.Add(region);
                    }
                    cellRegion[i] = ordinal;
                }

                Dictionary<District, int> districtIndex = new Dictionary<District, int>(ReferenceEqualityComparer<District>.Instance);
                int[] districtByRegion = new int[regions.Count];
                int nextDistrict = 1;
                for (int i = 0; i < regions.Count; i++)
                {
                    District district = regions[i].District;
                    if (district == null)
                        continue;
                    int ordinal;
                    if (!districtIndex.TryGetValue(district, out ordinal))
                    {
                        ordinal = nextDistrict++;
                        districtIndex.Add(district, ordinal);
                    }
                    districtByRegion[i] = ordinal;
                }

                List<int>[] adjacency = new List<int>[regions.Count];
                for (int i = 0; i < adjacency.Length; i++)
                    adjacency[i] = new List<int>(4);

                for (int i = 0; i < regions.Count; i++)
                {
                    Region region = regions[i];
                    List<RegionLink> links = region.links;
                    for (int li = 0; li < links.Count; li++)
                    {
                        RegionLink link = links[li];
                        if (link == null)
                            continue;
                        for (int side = 0; side < 2; side++)
                        {
                            Region other = link.regions[side];
                            if (other == null || ReferenceEquals(other, region) || !other.valid)
                                continue;
                            int otherIndex;
                            if (!regionIndex.TryGetValue(other, out otherIndex))
                                continue;
                            if (!adjacency[i].Contains(otherIndex))
                                adjacency[i].Add(otherIndex);
                        }
                    }
                }

                int[] edgeOffsets = new int[regions.Count + 1];
                int edgeCount = 0;
                for (int i = 0; i < adjacency.Length; i++)
                {
                    edgeOffsets[i] = edgeCount;
                    edgeCount += adjacency[i].Count;
                }
                edgeOffsets[regions.Count] = edgeCount;
                int[] edges = new int[edgeCount];
                int write = 0;
                for (int i = 0; i < adjacency.Length; i++)
                    for (int j = 0; j < adjacency[i].Count; j++)
                        edges[write++] = adjacency[i][j];

                long after = Interlocked.Read(ref state.RegionGeneration);
                if (after != before)
                {
                    Interlocked.Increment(ref topologyStale);
                    return null;
                }

                TopologySnapshot built = new TopologySnapshot(
                    map.uniqueID, map.Size.x, map.Size.z, after,
                    regions.ToArray(), cellRegion, districtByRegion, edgeOffsets, edges);
                Volatile.Write(ref state.Topology, built);
                Interlocked.Increment(ref topologyBuilds);
                return built;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref topologyFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                return null;
            }
            finally
            {
                RecordElapsed(ref topologyCaptureTicks, ref topologyCaptureTicksMax, started);
            }
        }

        private static void BuildAndPublishProfile(MapState mapState, ProfileSlot slot, ProfileBuildContext context)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (Interlocked.Read(ref mapState.RegionGeneration) != context.RegionGeneration)
                {
                    Interlocked.Increment(ref buildsStale);
                    return;
                }

                int regionCount = context.TraverseAllowed.Length;
                int[] components = new int[regionCount];
                int[] queue = new int[Math.Max(1, regionCount)];
                int component = 0;

                for (int i = 0; i < regionCount; i++)
                {
                    if (!context.TraverseAllowed[i] || components[i] != 0)
                        continue;
                    component++;
                    int head = 0;
                    int tail = 0;
                    queue[tail++] = i;
                    components[i] = component;

                    while (head < tail)
                    {
                        int current = queue[head++];
                        int from = context.EdgeOffsets[current];
                        int to = context.EdgeOffsets[current + 1];
                        for (int e = from; e < to; e++)
                        {
                            int next = context.Edges[e];
                            if (next < 0 || next >= regionCount || components[next] != 0 || !context.TraverseAllowed[next])
                                continue;
                            components[next] = component;
                            queue[tail++] = next;
                        }
                    }
                }

                if (Interlocked.Read(ref mapState.RegionGeneration) != context.RegionGeneration)
                {
                    Interlocked.Increment(ref buildsStale);
                    return;
                }

                ProfileSnapshot profile = new ProfileSnapshot(
                    context.MapId,
                    context.Width,
                    context.Height,
                    context.RegionGeneration,
                    context.CaptureFrame,
                    context.Key,
                    context.CellRegion,
                    context.DistrictByRegion,
                    context.EdgeOffsets,
                    context.Edges,
                    components,
                    context.DestinationAllowed);

                Interlocked.Exchange(ref slot.ValidatedMatches, 0);
                Interlocked.Exchange(ref slot.PredictionSerial, 0);
                Volatile.Write(ref slot.Published, profile);
                Interlocked.Increment(ref buildsPublished);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
            finally
            {
                RecordElapsed(ref workerBuildTicks, ref workerBuildTicksMax, started);
                Volatile.Write(ref slot.BuildScheduled, 0);
            }
        }

        private static bool SupportedTraverseMode(TraverseMode mode)
        {
            return mode == TraverseMode.ByPawn || mode == TraverseMode.PassDoors || mode == TraverseMode.NoPassClosedDoors;
        }

        private static bool SupportedPathEndMode(PathEndMode mode)
        {
            return mode == PathEndMode.OnCell || mode == PathEndMode.Touch || mode == PathEndMode.ClosestTouch;
        }

        private static void PatchRegionDirtySignals(Harmony harmony)
        {
            TryPatchDirtySignal(harmony, "Notify_WalkabilityChanged", new Type[] { typeof(IntVec3), typeof(bool) });
            TryPatchDirtySignal(harmony, "Notify_ThingAffectingRegionsSpawned", new Type[] { typeof(Thing) });
            TryPatchDirtySignal(harmony, "Notify_ThingAffectingRegionsDespawned", new Type[] { typeof(Thing) });
            TryPatchDirtySignal(harmony, "SetAllDirty", Type.EmptyTypes);
        }

        private static void TryPatchDirtySignal(Harmony harmony, string name, Type[] args)
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(RegionDirtyer), name, args);
                if (target == null)
                    return;
                HarmonyMethod postfix = new HarmonyMethod(typeof(AggressiveReachabilityProfiles04183), nameof(RegionDirtyPostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(target, postfix: postfix);
            }
            catch
            {
                // Generation + sampled parity + hard age remain the safety net.
            }
        }

        public static void RegionDirtyPostfix(Map ___map)
        {
            Map map = ___map;
            if (map == null)
                return;
            MapState state = MapStates.GetValue(map, delegate(Map m)
            {
                return new MapState(m.uniqueID, m.Size.x, m.Size.z);
            });
            Interlocked.Increment(ref state.RegionGeneration);
            Volatile.Write(ref state.Topology, null);
            Interlocked.Increment(ref regionDirtyEvents);
        }

        internal static string Summary()
        {
            long topo = Interlocked.Read(ref topologyBuilds);
            long completed = Interlocked.Read(ref captureCompleted);
            long chunks = Interlocked.Read(ref captureChunks);
            long published = Interlocked.Read(ref buildsPublished);
            long hits = Interlocked.Read(ref profileHits);
            double avgTopoUs = topo == 0 ? 0.0 : (Interlocked.Read(ref topologyCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / topo;
            double maxTopoUs = Interlocked.Read(ref topologyCaptureTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgChunkUs = chunks == 0 ? 0.0 : (Interlocked.Read(ref captureChunkTicks) * 1000000.0 / Stopwatch.Frequency) / chunks;
            double maxChunkUs = Interlocked.Read(ref captureChunkTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgRegions = chunks == 0 ? 0.0 : Interlocked.Read(ref captureRegions) / (double)chunks;
            double avgBuildUs = published == 0 ? 0.0 : (Interlocked.Read(ref workerBuildTicks) * 1000000.0 / Stopwatch.Frequency) / published;
            double maxBuildUs = Interlocked.Read(ref workerBuildTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgQueryUs = hits == 0 ? 0.0 : (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / hits;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Aggressive reachability profile V0.4.18.3: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observed) +
                ", eligible=" + Interlocked.Read(ref eligible) +
                ", priorPrefixOwned=" + Interlocked.Read(ref priorPrefixOwned) +
                ", immediateHits=" + Interlocked.Read(ref immediateHits) +
                ", unsupported=" + Interlocked.Read(ref unsupported) +
                ", profileHits=" + hits +
                ", profileMisses=" + Interlocked.Read(ref profileMisses) +
                ", softRefresh=" + Interlocked.Read(ref profileSoftRefresh) +
                ", hardExpired=" + Interlocked.Read(ref profileHardExpired) +
                ", retainedHitsDuringRefresh=" + Interlocked.Read(ref retainedProfileHitsDuringRefresh) +
                ", cooldownBypass=" + Interlocked.Read(ref profileCooldownBypass) +
                ", topologyBuilds=" + topo +
                ", topologyStale=" + Interlocked.Read(ref topologyStale) +
                ", topologyFailures=" + Interlocked.Read(ref topologyFailures) +
                ", regionDirtyEvents=" + Interlocked.Read(ref regionDirtyEvents) +
                ", captureStarts=" + Interlocked.Read(ref captureStarts) +
                ", captureChunks=" + chunks +
                ", captureCompleted=" + completed +
                ", captureDiscardedTopology=" + Interlocked.Read(ref captureDiscardedTopology) +
                ", captureDiscardedPawnState=" + Interlocked.Read(ref captureDiscardedPawnState) +
                ", captureRejected=" + Interlocked.Read(ref captureRejected) +
                ", pending=" + PendingCaptures.Count +
                ", pendingHighWater=" + Interlocked.Read(ref pendingHighWater) +
                ", pumpFrames=" + Interlocked.Read(ref pumpFrames) +
                ", pumpBudgetYields=" + Interlocked.Read(ref pumpBudgetYields) +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
                ", buildsPublished=" + published +
                ", buildsRejected=" + Interlocked.Read(ref buildsRejected) +
                ", buildsStale=" + Interlocked.Read(ref buildsStale) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ", predictedReachable=" + Interlocked.Read(ref predictedReachable) +
                ", predictedUnreachable=" + Interlocked.Read(ref predictedUnreachable) +
                ", authoritativeTrue=" + Interlocked.Read(ref authoritativeTrue) +
                ", authoritativeFalse=" + Interlocked.Read(ref authoritativeFalse) +
                ", shadowSamples=" + Interlocked.Read(ref shadowSamples) +
                ", shadowMatches=" + Interlocked.Read(ref shadowMatches) +
                ", parityMismatches=" + Interlocked.Read(ref parityMismatches) +
                " (predTrue/liveFalse=" + Interlocked.Read(ref mismatchReachableToFalse) +
                ", predFalse/liveTrue=" + Interlocked.Read(ref mismatchUnreachableToTrue) + ")" +
                ", unknown=" + Interlocked.Read(ref queriesUnknown) +
                ", softRefreshFrames=" + SoftRefreshFrames +
                ", hardMaxAgeFrames=" + HardMaxAgeFrames +
                ", captureBudgetMs=" + CaptureBudgetMilliseconds.ToString("F2") +
                ", maxRegionsPerChunk=" + MaxRegionsPerChunk +
                ", avgRegionsPerChunk=" + avgRegions.ToString("F1") +
                ", avgTopologyCaptureUs=" + avgTopoUs.ToString("F2") +
                ", maxTopologyCaptureUs=" + maxTopoUs.ToString("F2") +
                ", avgChunkUs=" + avgChunkUs.ToString("F2") +
                ", maxChunkUs=" + maxChunkUs.ToString("F2") +
                ", avgWorkerBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxWorkerBuildUs=" + maxBuildUs.ToString("F2") +
                ", avgQueryUs=" + avgQueryUs.ToString("F2") +
                ", maxQueryUs=" + maxQueryUs.ToString("F2") +
                ". Generation owns validity; refresh capture is budgeted and old profiles remain usable during soft refresh.";
        }

        private static void RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
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

        internal struct ReachSampleState
        {
            internal readonly bool Active;
            internal readonly bool Predicted;
            internal readonly ProfileSlot Slot;

            internal ReachSampleState(bool active, bool predicted, ProfileSlot slot)
            {
                Active = active;
                Predicted = predicted;
                Slot = slot;
            }
        }

        internal enum Prediction
        {
            Unknown,
            Reachable,
            Unreachable
        }

        private sealed class MapState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly ConditionalWeakTable<Pawn, PawnState> Pawns = new ConditionalWeakTable<Pawn, PawnState>();
            internal long RegionGeneration = 1;
            internal TopologySnapshot Topology;

            internal MapState(int mapId, int width, int height)
            {
                MapId = mapId;
                Width = width;
                Height = height;
            }
        }

        private sealed class PawnState
        {
            private readonly Dictionary<TraverseKey, ProfileSlot> slots = new Dictionary<TraverseKey, ProfileSlot>();

            internal ProfileSlot GetOrCreate(TraverseKey key)
            {
                ProfileSlot slot;
                if (!slots.TryGetValue(key, out slot))
                {
                    slot = new ProfileSlot();
                    slots.Add(key, slot);
                }
                return slot;
            }
        }

        internal sealed class ProfileSlot
        {
            internal int BuildScheduled;
            internal long LastScheduleFrame;
            internal long DisabledUntilFrame;
            internal int ValidatedMatches;
            internal int PredictionSerial;
            internal ProfileSnapshot Published;
        }

        internal struct TraverseKey : IEquatable<TraverseKey>
        {
            internal readonly TraverseMode Mode;
            internal readonly Danger MaxDanger;
            internal readonly bool CanBashDoors;
            internal readonly bool CanBashFences;
            internal readonly bool AlwaysUseAvoidGrid;
            internal readonly bool FenceBlocked;

            internal TraverseKey(TraverseParms parms)
            {
                Mode = parms.mode;
                MaxDanger = parms.maxDanger;
                CanBashDoors = parms.canBashDoors;
                CanBashFences = parms.canBashFences;
                AlwaysUseAvoidGrid = parms.alwaysUseAvoidGrid;
                FenceBlocked = parms.fenceBlocked;
            }

            public bool Equals(TraverseKey other)
            {
                return Mode == other.Mode && MaxDanger == other.MaxDanger &&
                    CanBashDoors == other.CanBashDoors && CanBashFences == other.CanBashFences &&
                    AlwaysUseAvoidGrid == other.AlwaysUseAvoidGrid && FenceBlocked == other.FenceBlocked;
            }

            public override bool Equals(object obj)
            {
                return obj is TraverseKey && Equals((TraverseKey)obj);
            }

            public override int GetHashCode()
            {
                int hash = (int)Mode;
                hash = (hash * 397) ^ (int)MaxDanger;
                hash = (hash * 397) ^ (CanBashDoors ? 1 : 0);
                hash = (hash * 397) ^ (CanBashFences ? 1 : 0);
                hash = (hash * 397) ^ (AlwaysUseAvoidGrid ? 1 : 0);
                hash = (hash * 397) ^ (FenceBlocked ? 1 : 0);
                return hash;
            }
        }

        private sealed class PendingCapture
        {
            internal readonly Map Map;
            internal readonly MapState MapState;
            internal readonly Pawn Pawn;
            internal readonly TraverseParms TraverseParams;
            internal readonly TraverseKey Key;
            internal readonly ProfileSlot Slot;
            internal readonly TopologySnapshot Topology;
            internal readonly long RegionGeneration;
            internal readonly long StartFrame;
            internal readonly bool[] TraverseAllowed;
            internal readonly bool[] DestinationAllowed;
            internal int NextRegion;

            internal PendingCapture(Map map, MapState mapState, Pawn pawn, TraverseParms traverseParams,
                TraverseKey key, ProfileSlot slot, TopologySnapshot topology, long generation, long startFrame)
            {
                Map = map;
                MapState = mapState;
                Pawn = pawn;
                TraverseParams = traverseParams;
                Key = key;
                Slot = slot;
                Topology = topology;
                RegionGeneration = generation;
                StartFrame = startFrame;
                TraverseAllowed = new bool[topology.RegionRefs.Length];
                DestinationAllowed = new bool[topology.RegionRefs.Length];
                NextRegion = 0;
            }
        }

        private sealed class TopologySnapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long RegionGeneration;
            internal readonly Region[] RegionRefs;
            internal readonly int[] CellRegion;
            internal readonly int[] DistrictByRegion;
            internal readonly int[] EdgeOffsets;
            internal readonly int[] Edges;

            internal TopologySnapshot(int mapId, int width, int height, long generation, Region[] regions,
                int[] cellRegion, int[] districtByRegion, int[] edgeOffsets, int[] edges)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                RegionGeneration = generation;
                RegionRefs = regions;
                CellRegion = cellRegion;
                DistrictByRegion = districtByRegion;
                EdgeOffsets = edgeOffsets;
                Edges = edges;
            }
        }

        private sealed class ProfileBuildContext
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long RegionGeneration;
            internal readonly long CaptureFrame;
            internal readonly TraverseKey Key;
            internal readonly int[] CellRegion;
            internal readonly int[] DistrictByRegion;
            internal readonly int[] EdgeOffsets;
            internal readonly int[] Edges;
            internal readonly bool[] TraverseAllowed;
            internal readonly bool[] DestinationAllowed;

            internal ProfileBuildContext(int mapId, int width, int height, long generation, long captureFrame,
                TraverseKey key, int[] cellRegion, int[] districtByRegion, int[] edgeOffsets, int[] edges,
                bool[] traverseAllowed, bool[] destinationAllowed)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                RegionGeneration = generation;
                CaptureFrame = captureFrame;
                Key = key;
                CellRegion = cellRegion;
                DistrictByRegion = districtByRegion;
                EdgeOffsets = edgeOffsets;
                Edges = edges;
                TraverseAllowed = traverseAllowed;
                DestinationAllowed = destinationAllowed;
            }
        }

        internal sealed class ProfileSnapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long RegionGeneration;
            internal readonly long CaptureFrame;
            internal readonly TraverseKey Key;
            private readonly int[] cellRegion;
            private readonly int[] districtByRegion;
            private readonly int[] edgeOffsets;
            private readonly int[] edges;
            private readonly int[] components;
            private readonly bool[] destinationAllowed;

            internal ProfileSnapshot(int mapId, int width, int height, long generation, long captureFrame,
                TraverseKey key, int[] cellRegion, int[] districtByRegion, int[] edgeOffsets, int[] edges,
                int[] components, bool[] destinationAllowed)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                RegionGeneration = generation;
                CaptureFrame = captureFrame;
                Key = key;
                this.cellRegion = cellRegion;
                this.districtByRegion = districtByRegion;
                this.edgeOffsets = edgeOffsets;
                this.edges = edges;
                this.components = components;
                this.destinationAllowed = destinationAllowed;
            }

            internal Prediction Classify(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, Map map, TraverseParms traverseParams)
            {
                if (!start.InBounds(map) || !dest.Cell.InBounds(map))
                    return Prediction.Unknown;

                int startRegion = RegionAt(start);
                int destCellRegion = RegionAt(dest.Cell);

                if (startRegion >= 0 && destCellRegion >= 0 &&
                    traverseParams.mode != TraverseMode.NoPassClosedDoorsOrWater &&
                    traverseParams.mode != TraverseMode.PassAllDestroyableThingsNotWater)
                {
                    int a = districtByRegion[startRegion];
                    int b = districtByRegion[destCellRegion];
                    if (a != 0 && a == b)
                        return Prediction.Reachable;
                }

                int[] seedRegions = rootRegionScratch ?? (rootRegionScratch = new int[16]);
                int seedCount = GatherStartRegions(start, map, traverseParams, seedRegions);
                if (seedCount == 0)
                    return Prediction.Unreachable;

                int[] rootComponents = rootComponentScratch ?? (rootComponentScratch = new int[32]);
                int rootComponentCount = 0;
                for (int i = 0; i < seedCount; i++)
                {
                    int region = seedRegions[i];
                    AddUniqueComponent(components[region], rootComponents, ref rootComponentCount);
                    int from = edgeOffsets[region];
                    int to = edgeOffsets[region + 1];
                    for (int e = from; e < to; e++)
                    {
                        int next = edges[e];
                        if (next >= 0 && next < components.Length)
                            AddUniqueComponent(components[next], rootComponents, ref rootComponentCount);
                    }
                }

                if (peMode == PathEndMode.OnCell)
                    return RegionReachability(destCellRegion, seedRegions, seedCount, rootComponents, rootComponentCount);

                CellRect rect;
                if (dest.HasThing && dest.Thing != null)
                    rect = dest.Thing.OccupiedRect().ExpandedBy(1);
                else
                    rect = new CellRect(dest.Cell.x - 1, dest.Cell.z - 1, 3, 3);

                bool sawDestinationRegion = false;
                for (int z = rect.minZ; z <= rect.maxZ; z++)
                {
                    for (int x = rect.minX; x <= rect.maxX; x++)
                    {
                        if (x < 0 || z < 0 || x >= Width || z >= Height)
                            continue;
                        int region = cellRegion[x + z * Width];
                        if (region < 0 || region >= destinationAllowed.Length || !destinationAllowed[region])
                            continue;
                        sawDestinationRegion = true;
                        Prediction p = RegionReachability(region, seedRegions, seedCount, rootComponents, rootComponentCount);
                        if (p == Prediction.Reachable)
                            return Prediction.Reachable;
                    }
                }
                return sawDestinationRegion ? Prediction.Unreachable : Prediction.Unreachable;
            }

            private int GatherStartRegions(IntVec3 start, Map map, TraverseParms traverseParams, int[] output)
            {
                int count = 0;
                PathGrid grid;
                try
                {
                    grid = map.pathing.For(traverseParams).pathGrid;
                }
                catch
                {
                    return 0;
                }

                if (grid.WalkableFast(start))
                {
                    AddUniqueRegion(RegionAt(start), output, ref count);
                    return count;
                }

                for (int i = 0; i < 8; i++)
                {
                    IntVec3 c = start + GenAdj.AdjacentCells[i];
                    if (!c.InBounds(map) || !grid.WalkableFast(c))
                        continue;
                    AddUniqueRegion(RegionAt(c), output, ref count);
                }
                return count;
            }

            private Prediction RegionReachability(int targetRegion, int[] seedRegions, int seedCount, int[] rootComponents, int rootComponentCount)
            {
                if (targetRegion < 0 || targetRegion >= destinationAllowed.Length || !destinationAllowed[targetRegion])
                    return Prediction.Unreachable;

                for (int i = 0; i < seedCount; i++)
                    if (seedRegions[i] == targetRegion)
                        return Prediction.Reachable;

                int targetComponent = components[targetRegion];
                if (targetComponent == 0)
                    return Prediction.Unreachable;
                for (int i = 0; i < rootComponentCount; i++)
                    if (rootComponents[i] == targetComponent)
                        return Prediction.Reachable;
                return Prediction.Unreachable;
            }

            private int RegionAt(IntVec3 c)
            {
                if (c.x < 0 || c.z < 0 || c.x >= Width || c.z >= Height)
                    return -1;
                return cellRegion[c.x + c.z * Width];
            }

            private static void AddUniqueRegion(int region, int[] output, ref int count)
            {
                if (region < 0)
                    return;
                for (int i = 0; i < count; i++)
                    if (output[i] == region)
                        return;
                if (count < output.Length)
                    output[count++] = region;
            }

            private static void AddUniqueComponent(int component, int[] output, ref int count)
            {
                if (component <= 0)
                    return;
                for (int i = 0; i < count; i++)
                    if (output[i] == component)
                        return;
                if (count < output.Length)
                    output[count++] = component;
            }
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();
            public bool Equals(T x, T y) { return ReferenceEquals(x, y); }
            public int GetHashCode(T obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
