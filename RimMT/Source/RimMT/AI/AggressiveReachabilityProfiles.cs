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
    // V0.4.16 intentionally moves beyond the V0.4.15 "definitely disconnected only"
    // whole-map hint. Vanilla Reachability is not itself thread-safe: each Map owns one
    // Reachability instance with a shared working flag, queue and cache. Calling live
    // map.reachability.CanReach from multiple workers would therefore create races.
    //
    // Instead, the main thread periodically captures the exact live Region graph plus the
    // result of Region.Allows(traverseParms, destination/non-destination) for one Pawn and
    // one TraverseParms shape. Workers consume ONLY primitive arrays and label connected
    // components. Subsequent CanReach calls can use that immutable profile without running
    // RegionTraverser on the main thread.
    //
    // This is deliberately a bounded-risk optimization. Door state, danger and avoid grids
    // can change without rebuilding the Region graph. Each fresh profile therefore starts in
    // shadow-validation mode; later predictions are sampled against live Vanilla CanReach.
    // A mismatch disables authoritative use for that Pawn/profile for a cooldown, and repeated
    // mismatches globally suppress the feature. Final Job/reservation/state mutation remains
    // entirely Vanilla/main-thread.
    internal static class AggressiveReachabilityProfiles
    {
        internal const string FeatureId = "parallel.reachProfile";

        private const int MaxSnapshotCells = 160000;
        private const long MaxProfileAgeFrames = 180;
        private const long BuildCooldownFrames = 12;
        private const long MismatchCooldownFrames = 600;
        private const int WarmupSamples = 8;
        private const int SampleMask = 15; // 1/16 after warmup.
        private const int GlobalMismatchFuse = 16;

        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();

        private static volatile bool compatibilityReady;

        [ThreadStatic] private static int bypassDepth;
        [ThreadStatic] private static int[] rootRegionScratch;
        [ThreadStatic] private static int[] rootComponentScratch;

        private static long observed;
        private static long eligible;
        private static long priorPrefixOwned;
        private static long immediateHits;
        private static long unsupported;
        private static long profileHits;
        private static long profileMisses;
        private static long profileExpired;
        private static long profileCooldownBypass;
        private static long topologyBuilds;
        private static long topologyStale;
        private static long topologyFailures;
        private static long topologyCaptureTicks;
        private static long topologyCaptureTicksMax;
        private static long profileCaptures;
        private static long profileCaptureTicks;
        private static long profileCaptureTicksMax;
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
                    Log.Warning("[RimMT] parallel.reachProfile V0.4.16 unavailable: Reachability.CanReach target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AggressiveReachabilityProfiles), nameof(Prefix));
                // VFECore Phasing uses the normal priority. Running after it is intentional:
                // if a previous prefix already owns the result, __runOriginal is false and
                // RimMT leaves that result untouched.
                prefix.priority = Priority.Low;
                HarmonyMethod postfix = new HarmonyMethod(typeof(AggressiveReachabilityProfiles), nameof(Postfix));
                postfix.priority = Priority.First;
                harmony.Patch(target, prefix: prefix, postfix: postfix);

                Log.Message("[RimMT] parallel.reachProfile V0.4.16 installed. Per-Pawn Region.Allows snapshots are captured on the main thread, connected components are built on workers, and validated predictions may bypass live RegionTraverser. VFECore Phasing retains earlier-prefix authority; parity mismatches trigger per-profile cooldown and a global fuse.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "reachability profile patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.reachProfile V0.4.16 patch failed; Vanilla Reachability remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
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

            if (bypassDepth != 0 || !compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
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

            // Preserve Vanilla's cheap immediate shortcut exactly before consulting a
            // possibly-aged profile. This also protects adjacent/corner cases.
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

                if (now - profile.CaptureFrame > MaxProfileAgeFrames)
                {
                    Interlocked.Increment(ref profileExpired);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }

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
                    __state = new ReachSampleState(true, predicted, slot, profile.RegionGeneration);
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
                Log.Warning("[RimMT] parallel.reachProfile V0.4.16 query failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
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
                FeatureGate.Suppress(FeatureId, "V0.4.16 reachability parity fuse: " + mismatches + " sampled mismatches");
                Log.Warning("[RimMT] parallel.reachProfile V0.4.16 disabled by parity fuse after " + mismatches + " sampled mismatches. Vanilla Reachability is authoritative again.");
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

            long generationBefore = Interlocked.Read(ref mapState.RegionGeneration);
            if (topology.RegionGeneration != generationBefore)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                return;
            }

            long captureStart = Stopwatch.GetTimestamp();
            bool[] traverseAllowed = new bool[topology.RegionRefs.Length];
            bool[] destinationAllowed = new bool[topology.RegionRefs.Length];
            try
            {
                for (int i = 0; i < topology.RegionRefs.Length; i++)
                {
                    Region region = topology.RegionRefs[i];
                    if (region == null || !region.valid)
                    {
                        Volatile.Write(ref slot.BuildScheduled, 0);
                        Interlocked.Increment(ref buildsStale);
                        return;
                    }
                    traverseAllowed[i] = region.Allows(traverseParams, false);
                    destinationAllowed[i] = region.Allows(traverseParams, true);
                }
            }
            catch
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
                return;
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - captureStart;
                Interlocked.Increment(ref profileCaptures);
                Interlocked.Add(ref profileCaptureTicks, elapsed);
                UpdateMax(ref profileCaptureTicksMax, elapsed);
            }

            long generationAfter = Interlocked.Read(ref mapState.RegionGeneration);
            if (generationAfter != generationBefore)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsStale);
                return;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
                return;
            }

            ProfileBuildContext context = new ProfileBuildContext(
                topology.MapId,
                topology.Width,
                topology.Height,
                topology.RegionGeneration,
                now,
                key,
                topology.CellRegion,
                topology.DistrictByRegion,
                topology.EdgeOffsets,
                topology.Edges,
                traverseAllowed,
                destinationAllowed);

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                BuildAndPublishProfile(mapState, slot, context);
            });

            if (!accepted)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
                return;
            }

            Interlocked.Exchange(ref slot.LastScheduleFrame, now);
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
                HarmonyMethod postfix = new HarmonyMethod(typeof(AggressiveReachabilityProfiles), nameof(RegionDirtyPostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(target, postfix: postfix);
            }
            catch
            {
                // Age/parity sampling remains the dynamic fail-safe if a dirty signal cannot be patched.
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

        internal static string Summary()
        {
            long topo = Interlocked.Read(ref topologyBuilds);
            long captures = Interlocked.Read(ref profileCaptures);
            long published = Interlocked.Read(ref buildsPublished);
            long q = Interlocked.Read(ref profileHits);
            double avgTopoUs = topo == 0 ? 0.0 : (Interlocked.Read(ref topologyCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / topo;
            double maxTopoUs = Interlocked.Read(ref topologyCaptureTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgCaptureUs = captures == 0 ? 0.0 : (Interlocked.Read(ref profileCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / captures;
            double maxCaptureUs = Interlocked.Read(ref profileCaptureTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgBuildUs = published == 0 ? 0.0 : (Interlocked.Read(ref workerBuildTicks) * 1000000.0 / Stopwatch.Frequency) / published;
            double maxBuildUs = Interlocked.Read(ref workerBuildTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgQueryUs = q == 0 ? 0.0 : (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / q;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Aggressive reachability profile V0.4.16: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observed) +
                ", eligible=" + Interlocked.Read(ref eligible) +
                ", priorPrefixOwned=" + Interlocked.Read(ref priorPrefixOwned) +
                ", immediateHits=" + Interlocked.Read(ref immediateHits) +
                ", unsupported=" + Interlocked.Read(ref unsupported) +
                ", profileHits=" + q +
                ", profileMisses=" + Interlocked.Read(ref profileMisses) +
                ", profileExpired=" + Interlocked.Read(ref profileExpired) +
                ", cooldownBypass=" + Interlocked.Read(ref profileCooldownBypass) +
                ", topologyBuilds=" + topo +
                ", topologyStale=" + Interlocked.Read(ref topologyStale) +
                ", topologyFailures=" + Interlocked.Read(ref topologyFailures) +
                ", regionDirtyEvents=" + Interlocked.Read(ref regionDirtyEvents) +
                ", profileCaptures=" + captures +
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
                ", warmupSamples=" + WarmupSamples +
                ", sampleEvery=" + (SampleMask + 1) +
                ", maxProfileAgeFrames=" + MaxProfileAgeFrames +
                ", avgTopologyCaptureUs=" + avgTopoUs.ToString("F2") +
                ", maxTopologyCaptureUs=" + maxTopoUs.ToString("F2") +
                ", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +
                ", avgWorkerBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxWorkerBuildUs=" + maxBuildUs.ToString("F2") +
                ", avgQueryUs=" + avgQueryUs.ToString("F2") +
                ", maxQueryUs=" + maxQueryUs.ToString("F2") +
                ". Bounded-risk policy: live Region.Allows is captured only on the main thread; worker graph construction uses primitive arrays; authoritative predictions are shadow-sampled and auto-fused on mismatch.";
        }

        internal struct ReachSampleState
        {
            internal readonly bool Active;
            internal readonly bool Predicted;
            internal readonly ProfileSlot Slot;
            internal readonly long RegionGeneration;

            internal ReachSampleState(bool active, bool predicted, ProfileSlot slot, long generation)
            {
                Active = active;
                Predicted = predicted;
                Slot = slot;
                RegionGeneration = generation;
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

        private sealed class TopologySnapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long RegionGeneration;
            internal readonly Region[] RegionRefs; // Main-thread capture only; worker never dereferences this array.
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

                // Match Vanilla's same-District early success for ordinary modes.
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

                // Touch/ClosestTouch: use a permissive superset of Vanilla's allowed adjacent
                // cells. Ignoring corner-touch restrictions can create a reachable hint but
                // cannot create a false unreachable hint. Shadow sampling guards positives.
                CellRect rect;
                if (dest.HasThing && dest.Thing != null)
                    rect = dest.Thing.OccupiedRect().ExpandedBy(1);
                else
                {
                    rect = new CellRect(dest.Cell.x - 1, dest.Cell.z - 1, 3, 3);
                }

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
