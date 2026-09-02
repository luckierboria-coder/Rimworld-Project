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
    /// <summary>
    /// V0.4.17 keeps the proven JS1.1 profile semantics but changes two production-cost boundaries:
    /// 1) map topology capture is incremental and frame-budgeted instead of monolithic; and
    /// 2) parity mismatches quarantine the affected profile slot first. A global soft fuse now
    /// requires mismatch density across multiple independent slots, while the emergency hard fuse
    /// remains global. Workers still consume primitive immutable arrays only; no Verse object is
    /// dereferenced off-thread and Vanilla remains authoritative on every miss/bypass/sample.
    /// </summary>
    internal static class AggressiveReachabilityProfilesV17
    {
        internal const string FeatureId = AggressiveReachabilityProfiles.FeatureId;

        private const int MaxSnapshotCells = 160000;
        private const long MaxProfileAgeFrames = 180;
        private const long BuildCooldownFrames = 12;
        private const long MismatchCooldownFrames = 600;
        private const int WarmupSamples = 8;
        private const int SampleMask = 127; // Unified Lean production cadence: 1/128 after warmup.

        private const int GlobalWindowSamples = 8192;
        private const int GlobalMismatchLimit = 8;
        private const int GlobalDistinctSlotLimit = 3;
        private const long GlobalCooldownFrames = 3600;
        private const int ProbationSamples = 256;
        private const int EmergencyWindowSamples = 256;
        private const int EmergencyMismatchLimit = 16;

        private const int SliceCheckMask = 63;

        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();

        private static readonly bool[] GlobalMismatchWindow = new bool[GlobalWindowSamples];
        private static readonly ProfileSlot[] GlobalMismatchSlotWindow = new ProfileSlot[GlobalWindowSamples];
        private static readonly Dictionary<ProfileSlot, int> GlobalMismatchSlotCounts = new Dictionary<ProfileSlot, int>();
        private static readonly bool[] EmergencyMismatchWindow = new bool[EmergencyWindowSamples];

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
        private static long profileExpired;
        private static long profileCooldownBypass;
        private static long topologyBuildStarts;
        private static long topologyBuilds;
        private static long topologyBuildDiscarded;
        private static long topologyStale;
        private static long topologyFailures;
        private static long topologySlices;
        private static long topologySliceTicks;
        private static long topologySliceTicksMax;
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
        private static long localSlotQuarantines;
        private static long localOnlyFuseDeferrals;

        // Rolling-fuse state is main-thread owned.
        private static ReachFuseMode reachFuseMode;
        private static int globalWindowPos;
        private static int globalWindowCount;
        private static int globalWindowMismatches;
        private static int emergencyWindowPos;
        private static int emergencyWindowCount;
        private static int emergencyWindowMismatches;
        private static long cooldownUntilFrame;
        private static int probationRemaining;
        private static int probationMatches;
        private static long rollingSamples;
        private static long rollingMismatches;
        private static long softFuses;
        private static long cooldownLiveBypass;
        private static long probationForcedShadow;
        private static long probationPasses;
        private static long probationFailures;
        private static long hardFuses;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

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
                    Log.Warning("[RimMT] parallel.reachProfile V0.4.17 unavailable: Reachability.CanReach target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), nameof(Prefix));
                prefix.priority = Priority.Low;
                HarmonyMethod postfix = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), nameof(Postfix));
                postfix.priority = Priority.First;
                harmony.Patch(target, prefix: prefix, postfix: postfix);

                Log.Message("[RimMT] parallel.reachProfile V0.4.17 installed: topology capture is frame-sliced with adaptive budgets; mismatch handling is local-slot-first with multi-slot global soft fuse; emergency hard fuse remains 16/256.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "reachability profile V0.4.17 patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.reachProfile V0.4.17 patch failed; Vanilla Reachability remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
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

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            UpdateRollingFuseMode();
            if (reachFuseMode == ReachFuseMode.Cooldown)
            {
                cooldownLiveBypass++;
                return true;
            }
            if (reachFuseMode == ReachFuseMode.HardFused)
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
                long disabledUntil = Interlocked.Read(ref slot.DisabledUntilFrame);
                if (disabledUntil > now)
                {
                    Interlocked.Increment(ref profileCooldownBypass);
                    // Do not build profiles that will expire before a local quarantine ends.
                    if (disabledUntil - now <= BuildCooldownFrames)
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
                if (predicted) Interlocked.Increment(ref predictedReachable);
                else Interlocked.Increment(ref predictedUnreachable);

                int validated = Volatile.Read(ref slot.ValidatedMatches);
                int serial = Interlocked.Increment(ref slot.PredictionSerial);
                bool probation = reachFuseMode == ReachFuseMode.Probation;
                bool sample = probation || validated < WarmupSamples || (serial & SampleMask) == 0;
                if (sample)
                {
                    __state = new ReachSampleState(true, predicted, slot, profile.RegionGeneration);
                    Interlocked.Increment(ref shadowSamples);
                    if (probation) probationForcedShadow++;
                    return true;
                }

                __result = predicted;
                if (predicted) Interlocked.Increment(ref authoritativeTrue);
                else Interlocked.Increment(ref authoritativeFalse);
                return false;
            }
            catch (Exception ex)
            {
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.reachProfile V0.4.17 query failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                RecordElapsed(ref queryTicks, ref queryTicksMax, started);
            }
        }

        public static void Postfix(bool __result, ReachSampleState __state)
        {
            if (!__state.Active || __state.Slot == null) return;

            bool mismatch = __result != __state.Predicted;
            if (!mismatch)
            {
                Interlocked.Increment(ref shadowMatches);
                Interlocked.Increment(ref __state.Slot.ValidatedMatches);
                ObserveRollingSample(false, __state.Slot);
                return;
            }

            Interlocked.Increment(ref parityMismatches);
            if (__state.Predicted) Interlocked.Increment(ref mismatchReachableToFalse);
            else Interlocked.Increment(ref mismatchUnreachableToTrue);

            Interlocked.Exchange(ref __state.Slot.ValidatedMatches, 0);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
            Volatile.Write(ref __state.Slot.Published, null);
            Interlocked.Increment(ref localSlotQuarantines);

            ObserveRollingSample(true, __state.Slot);
        }

        private static void UpdateRollingFuseMode()
        {
            if (reachFuseMode != ReachFuseMode.Cooldown) return;
            if (RimMTRuntime.MainThreadFrames < cooldownUntilFrame) return;

            reachFuseMode = ReachFuseMode.Probation;
            probationRemaining = ProbationSamples;
            probationMatches = 0;
            ClearGlobalWindow();
            ClearEmergencyWindow();
            Log.Message("[RimMT] ReachProfile V0.4.17 global cooldown ended; entering 256-sample forced-live probation.");
        }

        private static void ObserveRollingSample(bool mismatch, ProfileSlot slot)
        {
            rollingSamples++;
            if (mismatch) rollingMismatches++;

            if (reachFuseMode == ReachFuseMode.HardFused || reachFuseMode == ReachFuseMode.Cooldown)
                return;

            PushGlobalWindow(mismatch, slot);
            PushBoolWindow(EmergencyMismatchWindow, ref emergencyWindowPos, ref emergencyWindowCount,
                ref emergencyWindowMismatches, mismatch);

            if (emergencyWindowMismatches >= EmergencyMismatchLimit)
            {
                reachFuseMode = ReachFuseMode.HardFused;
                hardFuses++;
                FeatureGate.Suppress(FeatureId,
                    "V0.4.17 emergency ReachProfile hard fuse: " + emergencyWindowMismatches + "/" + emergencyWindowCount + " sampled mismatches");
                Log.Warning("[RimMT] ReachProfile HARD FUSE V0.4.17: " + emergencyWindowMismatches + "/" + emergencyWindowCount + " mismatches in the emergency sample window. Vanilla Reachability is authoritative for the rest of this run.");
                return;
            }

            if (reachFuseMode == ReachFuseMode.Probation)
            {
                if (mismatch)
                {
                    probationFailures++;
                    EnterGlobalCooldown("probation mismatch");
                    return;
                }

                probationMatches++;
                if (probationRemaining > 0) probationRemaining--;
                if (probationRemaining <= 0)
                {
                    reachFuseMode = ReachFuseMode.Normal;
                    probationPasses++;
                    ClearGlobalWindow();
                    ClearEmergencyWindow();
                    Log.Message("[RimMT] ReachProfile V0.4.17 probation passed 256 clean live-shadow samples; profile authority restored.");
                }
                return;
            }

            if (reachFuseMode == ReachFuseMode.Normal && globalWindowMismatches >= GlobalMismatchLimit)
            {
                int distinct = GlobalMismatchSlotCounts.Count;
                if (distinct >= GlobalDistinctSlotLimit)
                    EnterGlobalCooldown("rolling mismatch density " + globalWindowMismatches + "/" + globalWindowCount +
                        " across " + distinct + " slots");
                else if (mismatch)
                    localOnlyFuseDeferrals++;
            }
        }

        private static void EnterGlobalCooldown(string reason)
        {
            reachFuseMode = ReachFuseMode.Cooldown;
            cooldownUntilFrame = RimMTRuntime.MainThreadFrames + GlobalCooldownFrames;
            probationRemaining = 0;
            probationMatches = 0;
            softFuses++;
            ClearGlobalWindow();
            ClearEmergencyWindow();
            Log.Warning("[RimMT] ReachProfile SOFT FUSE V0.4.17: " + reason + ". Global profile authority is bypassed for 3600 main-thread frames; affected slots are already quarantined independently.");
        }

        private static void PushGlobalWindow(bool mismatch, ProfileSlot slot)
        {
            if (globalWindowCount >= GlobalMismatchWindow.Length)
            {
                if (GlobalMismatchWindow[globalWindowPos])
                {
                    globalWindowMismatches--;
                    ProfileSlot oldSlot = GlobalMismatchSlotWindow[globalWindowPos];
                    if (oldSlot != null)
                    {
                        int count;
                        if (GlobalMismatchSlotCounts.TryGetValue(oldSlot, out count))
                        {
                            if (count <= 1) GlobalMismatchSlotCounts.Remove(oldSlot);
                            else GlobalMismatchSlotCounts[oldSlot] = count - 1;
                        }
                    }
                }
            }
            else
            {
                globalWindowCount++;
            }

            GlobalMismatchWindow[globalWindowPos] = mismatch;
            GlobalMismatchSlotWindow[globalWindowPos] = mismatch ? slot : null;
            if (mismatch)
            {
                globalWindowMismatches++;
                if (slot != null)
                {
                    int count;
                    GlobalMismatchSlotCounts.TryGetValue(slot, out count);
                    GlobalMismatchSlotCounts[slot] = count + 1;
                }
            }
            globalWindowPos = (globalWindowPos + 1) % GlobalMismatchWindow.Length;
        }

        private static void PushBoolWindow(bool[] window, ref int pos, ref int count, ref int mismatchCount, bool mismatch)
        {
            if (count < window.Length)
            {
                window[pos] = mismatch;
                if (mismatch) mismatchCount++;
                count++;
                pos = (pos + 1) % window.Length;
                return;
            }

            if (window[pos]) mismatchCount--;
            window[pos] = mismatch;
            if (mismatch) mismatchCount++;
            pos = (pos + 1) % window.Length;
        }

        private static void ClearGlobalWindow()
        {
            Array.Clear(GlobalMismatchWindow, 0, GlobalMismatchWindow.Length);
            Array.Clear(GlobalMismatchSlotWindow, 0, GlobalMismatchSlotWindow.Length);
            GlobalMismatchSlotCounts.Clear();
            globalWindowPos = 0;
            globalWindowCount = 0;
            globalWindowMismatches = 0;
        }

        private static void ClearEmergencyWindow()
        {
            Array.Clear(EmergencyMismatchWindow, 0, EmergencyMismatchWindow.Length);
            emergencyWindowPos = 0;
            emergencyWindowCount = 0;
            emergencyWindowMismatches = 0;
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
            if (last != 0 && now - last < BuildCooldownFrames) return;
            if (Interlocked.CompareExchange(ref slot.BuildScheduled, 1, 0) != 0) return;

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

            bool accepted = scheduler.TryEnqueue(FeatureId, AdaptiveLoadBalancer.RecommendedOffloadPriority, delegate
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
            if (cells <= 0 || cells > MaxSnapshotCells) return null;

            long frame = RimMTRuntime.MainThreadFrames;
            TopologyBuildState build = state.TopologyBuild;
            if (build == null || build.RegionGeneration != generation || build.MapId != map.uniqueID ||
                build.Width != map.Size.x || build.Height != map.Size.z)
            {
                if (build != null) Interlocked.Increment(ref topologyBuildDiscarded);
                build = StartTopologyBuild(map, generation, cells);
                if (build == null)
                {
                    Interlocked.Increment(ref topologyFailures);
                    return null;
                }
                state.TopologyBuild = build;
                Interlocked.Increment(ref topologyBuildStarts);
            }

            if (build.LastSliceFrame == frame) return null;
            build.LastSliceFrame = frame;

            long sliceStart = Stopwatch.GetTimestamp();
            TopologyAdvanceResult advance;
            try
            {
                advance = AdvanceTopologyBuild(map, state, build, sliceStart, TopologySliceBudgetTicks());
            }
            catch (Exception ex)
            {
                state.TopologyBuild = null;
                Interlocked.Increment(ref topologyFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                advance = TopologyAdvanceResult.Failed;
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - sliceStart;
                Interlocked.Increment(ref topologySlices);
                Interlocked.Add(ref topologySliceTicks, elapsed);
                UpdateMax(ref topologySliceTicksMax, elapsed);
            }

            if (advance == TopologyAdvanceResult.Pending) return null;
            if (advance == TopologyAdvanceResult.Stale)
            {
                state.TopologyBuild = null;
                Interlocked.Increment(ref topologyStale);
                Interlocked.Increment(ref topologyBuildDiscarded);
                return null;
            }
            if (advance == TopologyAdvanceResult.Failed)
            {
                state.TopologyBuild = null;
                return null;
            }

            long after = Interlocked.Read(ref state.RegionGeneration);
            if (after != build.RegionGeneration)
            {
                state.TopologyBuild = null;
                Interlocked.Increment(ref topologyStale);
                Interlocked.Increment(ref topologyBuildDiscarded);
                return null;
            }

            TopologySnapshot built = new TopologySnapshot(
                build.MapId, build.Width, build.Height, after,
                build.Regions.ToArray(), build.CellRegion, build.DistrictByRegion, build.EdgeOffsets, build.Edges);
            Volatile.Write(ref state.Topology, built);
            state.TopologyBuild = null;
            Interlocked.Increment(ref topologyBuilds);
            return built;
        }

        private static TopologyBuildState StartTopologyBuild(Map map, long generation, int cells)
        {
            Region[] direct = map.regionGrid.DirectGrid;
            if (direct == null || direct.Length != cells) return null;
            return new TopologyBuildState(map.uniqueID, map.Size.x, map.Size.z, generation, direct, cells);
        }

        private static TopologyAdvanceResult AdvanceTopologyBuild(
            Map map, MapState state, TopologyBuildState build, long sliceStart, long budgetTicks)
        {
            if (Interlocked.Read(ref state.RegionGeneration) != build.RegionGeneration)
                return TopologyAdvanceResult.Stale;

            while (true)
            {
                switch (build.Phase)
                {
                    case TopologyBuildPhase.Cells:
                        while (build.CellCursor < build.Direct.Length)
                        {
                            int i = build.CellCursor++;
                            Region region = build.Direct[i];
                            if (region != null && region.valid)
                            {
                                int ordinal;
                                if (!build.RegionIndex.TryGetValue(region, out ordinal))
                                {
                                    ordinal = build.Regions.Count;
                                    build.RegionIndex.Add(region, ordinal);
                                    build.Regions.Add(region);
                                }
                                build.CellRegion[i] = ordinal + 1; // zero is reserved until normalization.
                            }
                            if ((build.CellCursor & SliceCheckMask) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
                        build.Phase = TopologyBuildPhase.NormalizeCells;
                        build.CellCursor = 0;
                        break;

                    case TopologyBuildPhase.NormalizeCells:
                        while (build.CellCursor < build.CellRegion.Length)
                        {
                            build.CellRegion[build.CellCursor] = build.CellRegion[build.CellCursor] - 1;
                            build.CellCursor++;
                            if ((build.CellCursor & SliceCheckMask) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
                        build.DistrictByRegion = new int[build.Regions.Count];
                        build.Adjacency = new List<int>[build.Regions.Count];
                        build.Phase = TopologyBuildPhase.Districts;
                        build.RegionCursor = 0;
                        break;

                    case TopologyBuildPhase.Districts:
                        while (build.RegionCursor < build.Regions.Count)
                        {
                            int i = build.RegionCursor++;
                            Region region = build.Regions[i];
                            if (region == null || !region.valid) return TopologyAdvanceResult.Stale;
                            District district = region.District;
                            if (district != null)
                            {
                                int ordinal;
                                if (!build.DistrictIndex.TryGetValue(district, out ordinal))
                                {
                                    ordinal = build.NextDistrict++;
                                    build.DistrictIndex.Add(district, ordinal);
                                }
                                build.DistrictByRegion[i] = ordinal;
                            }
                            if ((build.RegionCursor & SliceCheckMask) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
                        build.Phase = TopologyBuildPhase.Adjacency;
                        build.RegionCursor = 0;
                        break;

                    case TopologyBuildPhase.Adjacency:
                        while (build.RegionCursor < build.Regions.Count)
                        {
                            int i = build.RegionCursor++;
                            Region region = build.Regions[i];
                            if (region == null || !region.valid) return TopologyAdvanceResult.Stale;
                            List<int> adjacency = new List<int>(4);
                            build.Adjacency[i] = adjacency;
                            List<RegionLink> links = region.links;
                            if (links != null)
                            {
                                for (int li = 0; li < links.Count; li++)
                                {
                                    RegionLink link = links[li];
                                    if (link == null) continue;
                                    for (int side = 0; side < 2; side++)
                                    {
                                        Region other = link.regions[side];
                                        if (other == null || ReferenceEquals(other, region) || !other.valid) continue;
                                        int otherIndex;
                                        if (!build.RegionIndex.TryGetValue(other, out otherIndex)) continue;
                                        if (!adjacency.Contains(otherIndex)) adjacency.Add(otherIndex);
                                    }
                                }
                            }
                            if ((build.RegionCursor & 15) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
                        build.EdgeOffsets = new int[build.Regions.Count + 1];
                        build.Phase = TopologyBuildPhase.EdgeOffsets;
                        build.RegionCursor = 0;
                        build.EdgeCount = 0;
                        break;

                    case TopologyBuildPhase.EdgeOffsets:
                        while (build.RegionCursor < build.Regions.Count)
                        {
                            int i = build.RegionCursor++;
                            build.EdgeOffsets[i] = build.EdgeCount;
                            List<int> adjacency = build.Adjacency[i];
                            if (adjacency != null) build.EdgeCount += adjacency.Count;
                            if ((build.RegionCursor & SliceCheckMask) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
                        build.EdgeOffsets[build.Regions.Count] = build.EdgeCount;
                        build.Edges = new int[build.EdgeCount];
                        build.Phase = TopologyBuildPhase.FlattenEdges;
                        build.FlattenRegion = 0;
                        build.FlattenLocal = 0;
                        build.EdgeWrite = 0;
                        break;

                    case TopologyBuildPhase.FlattenEdges:
                        while (build.FlattenRegion < build.Regions.Count)
                        {
                            List<int> adjacency = build.Adjacency[build.FlattenRegion];
                            int count = adjacency == null ? 0 : adjacency.Count;
                            while (build.FlattenLocal < count)
                            {
                                build.Edges[build.EdgeWrite++] = adjacency[build.FlattenLocal++];
                                if ((build.EdgeWrite & SliceCheckMask) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                    return TopologyAdvanceResult.Pending;
                            }
                            build.FlattenRegion++;
                            build.FlattenLocal = 0;
                        }
                        build.Phase = TopologyBuildPhase.Complete;
                        break;

                    default:
                        return Interlocked.Read(ref state.RegionGeneration) == build.RegionGeneration
                            ? TopologyAdvanceResult.Complete
                            : TopologyAdvanceResult.Stale;
                }

                if (Interlocked.Read(ref state.RegionGeneration) != build.RegionGeneration)
                    return TopologyAdvanceResult.Stale;
                if (BudgetSpent(sliceStart, budgetTicks))
                    return build.Phase == TopologyBuildPhase.Complete ? TopologyAdvanceResult.Complete : TopologyAdvanceResult.Pending;
            }
        }

        private static bool BudgetSpent(long sliceStart, long budgetTicks)
        {
            return Stopwatch.GetTimestamp() - sliceStart >= budgetTicks;
        }

        private static long TopologySliceBudgetTicks()
        {
            int microseconds;
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: microseconds = 2000; break;
                case LoadPressure.Normal: microseconds = 1500; break;
                case LoadPressure.High: microseconds = 1000; break;
                default: microseconds = 500; break;
            }
            return Math.Max(1L, Stopwatch.Frequency * microseconds / 1000000L);
        }

        private static int TopologySliceBudgetMicroseconds()
        {
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: return 2000;
                case LoadPressure.Normal: return 1500;
                case LoadPressure.High: return 1000;
                default: return 500;
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
                    if (!context.TraverseAllowed[i] || components[i] != 0) continue;
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
                            if (next < 0 || next >= regionCount || components[next] != 0 || !context.TraverseAllowed[next]) continue;
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
                    context.MapId, context.Width, context.Height, context.RegionGeneration, context.CaptureFrame,
                    context.Key, context.CellRegion, context.DistrictByRegion, context.EdgeOffsets, context.Edges,
                    components, context.DestinationAllowed);

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
                if (target == null) return;
                HarmonyMethod postfix = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), nameof(RegionDirtyPostfix));
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
            if (map == null) return;
            MapState state = MapStates.GetValue(map, delegate(Map m)
            {
                return new MapState(m.uniqueID, m.Size.x, m.Size.z);
            });
            Interlocked.Increment(ref state.RegionGeneration);
            Volatile.Write(ref state.Topology, null);
            if (state.TopologyBuild != null)
            {
                state.TopologyBuild = null;
                Interlocked.Increment(ref topologyBuildDiscarded);
            }
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
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
            }
        }

        internal static string Summary()
        {
            long topoPublished = Interlocked.Read(ref topologyBuilds);
            long slices = Interlocked.Read(ref topologySlices);
            long captures = Interlocked.Read(ref profileCaptures);
            long published = Interlocked.Read(ref buildsPublished);
            long q = Interlocked.Read(ref profileHits);
            double topologyWorkMs = Interlocked.Read(ref topologySliceTicks) * 1000.0 / Stopwatch.Frequency;
            double avgSliceUs = slices == 0 ? 0.0 : (Interlocked.Read(ref topologySliceTicks) * 1000000.0 / Stopwatch.Frequency) / slices;
            double maxSliceUs = Interlocked.Read(ref topologySliceTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgCaptureUs = captures == 0 ? 0.0 : (Interlocked.Read(ref profileCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / captures;
            double maxCaptureUs = Interlocked.Read(ref profileCaptureTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgBuildUs = published == 0 ? 0.0 : (Interlocked.Read(ref workerBuildTicks) * 1000000.0 / Stopwatch.Frequency) / published;
            double maxBuildUs = Interlocked.Read(ref workerBuildTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgQueryUs = q == 0 ? 0.0 : (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / q;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Aggressive reachability profile V0.4.17 sliced/local-first: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observed) +
                ", eligible=" + Interlocked.Read(ref eligible) +
                ", priorPrefixOwned=" + Interlocked.Read(ref priorPrefixOwned) +
                ", immediateHits=" + Interlocked.Read(ref immediateHits) +
                ", unsupported=" + Interlocked.Read(ref unsupported) +
                ", profileHits=" + q +
                ", profileMisses=" + Interlocked.Read(ref profileMisses) +
                ", profileExpired=" + Interlocked.Read(ref profileExpired) +
                ", cooldownBypass=" + Interlocked.Read(ref profileCooldownBypass) +
                ", topologyBuildStarts=" + Interlocked.Read(ref topologyBuildStarts) +
                ", topologyBuilds=" + topoPublished +
                ", topologyDiscarded=" + Interlocked.Read(ref topologyBuildDiscarded) +
                ", topologyStale=" + Interlocked.Read(ref topologyStale) +
                ", topologyFailures=" + Interlocked.Read(ref topologyFailures) +
                ", topologySlices=" + slices +
                ", topologyBudgetUs=" + TopologySliceBudgetMicroseconds() +
                ", topologyWorkMs=" + topologyWorkMs.ToString("F2") +
                ", avgTopologySliceUs=" + avgSliceUs.ToString("F2") +
                ", maxTopologySliceUs=" + maxSliceUs.ToString("F2") +
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
                ", localSlotQuarantines=" + Interlocked.Read(ref localSlotQuarantines) +
                ", rollingMode=" + reachFuseMode +
                ", rollingSamples=" + rollingSamples +
                ", rollingMismatches=" + rollingMismatches +
                ", globalWindow=" + globalWindowMismatches + "/" + globalWindowCount +
                ", globalDistinctMismatchSlots=" + GlobalMismatchSlotCounts.Count +
                ", localOnlyFuseDeferrals=" + Interlocked.Read(ref localOnlyFuseDeferrals) +
                ", emergencyWindow=" + emergencyWindowMismatches + "/" + emergencyWindowCount +
                ", softFuses=" + softFuses +
                ", cooldownUntilFrame=" + cooldownUntilFrame +
                ", cooldownLiveBypass=" + cooldownLiveBypass +
                ", probationRemaining=" + probationRemaining +
                ", probationMatches=" + probationMatches +
                ", probationForcedShadow=" + probationForcedShadow +
                ", probationPasses=" + probationPasses +
                ", probationFailures=" + probationFailures +
                ", hardFuses=" + hardFuses +
                ", unknown=" + Interlocked.Read(ref queriesUnknown) +
                ", warmupSamples=" + WarmupSamples +
                ", sampleEvery=" + (SampleMask + 1) +
                ", maxProfileAgeFrames=" + MaxProfileAgeFrames +
                ", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +
                ", avgWorkerBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxWorkerBuildUs=" + maxBuildUs.ToString("F2") +
                ", avgQueryUs=" + avgQueryUs.ToString("F2") +
                ", maxQueryUs=" + maxQueryUs.ToString("F2") +
                ". Topology is captured incrementally on the main thread; workers consume primitive immutable arrays only. Vanilla remains authoritative during incomplete slices, local quarantine, global cooldown, misses and shadow validation.";
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

        private enum ReachFuseMode { Normal, Cooldown, Probation, HardFused }
        internal enum Prediction { Unknown, Reachable, Unreachable }
        private enum TopologyAdvanceResult { Pending, Complete, Stale, Failed }
        private enum TopologyBuildPhase { Cells, NormalizeCells, Districts, Adjacency, EdgeOffsets, FlattenEdges, Complete }

        private sealed class MapState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly ConditionalWeakTable<Pawn, PawnState> Pawns = new ConditionalWeakTable<Pawn, PawnState>();
            internal long RegionGeneration = 1;
            internal TopologySnapshot Topology;
            internal TopologyBuildState TopologyBuild;

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
            public override bool Equals(object obj) { return obj is TraverseKey && Equals((TraverseKey)obj); }
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

        private sealed class TopologyBuildState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long RegionGeneration;
            internal readonly Region[] Direct;
            internal readonly Dictionary<Region, int> RegionIndex = new Dictionary<Region, int>(ReferenceEqualityComparer<Region>.Instance);
            internal readonly List<Region> Regions = new List<Region>();
            internal readonly int[] CellRegion;
            internal readonly Dictionary<District, int> DistrictIndex = new Dictionary<District, int>(ReferenceEqualityComparer<District>.Instance);
            internal int[] DistrictByRegion;
            internal List<int>[] Adjacency;
            internal int[] EdgeOffsets;
            internal int[] Edges;
            internal TopologyBuildPhase Phase;
            internal int CellCursor;
            internal int RegionCursor;
            internal int NextDistrict = 1;
            internal int EdgeCount;
            internal int FlattenRegion;
            internal int FlattenLocal;
            internal int EdgeWrite;
            internal long LastSliceFrame = -1;

            internal TopologyBuildState(int mapId, int width, int height, long generation, Region[] direct, int cells)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                RegionGeneration = generation;
                Direct = direct;
                CellRegion = new int[cells];
                Phase = TopologyBuildPhase.Cells;
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
                if (!start.InBounds(map) || !dest.Cell.InBounds(map)) return Prediction.Unknown;

                int startRegion = RegionAt(start);
                int destCellRegion = RegionAt(dest.Cell);
                if (startRegion >= 0 && destCellRegion >= 0 &&
                    traverseParams.mode != TraverseMode.NoPassClosedDoorsOrWater &&
                    traverseParams.mode != TraverseMode.PassAllDestroyableThingsNotWater)
                {
                    int a = districtByRegion[startRegion];
                    int b = districtByRegion[destCellRegion];
                    if (a != 0 && a == b) return Prediction.Reachable;
                }

                int[] seedRegions = rootRegionScratch ?? (rootRegionScratch = new int[16]);
                int seedCount = GatherStartRegions(start, map, traverseParams, seedRegions);
                if (seedCount == 0) return Prediction.Unreachable;

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
                if (dest.HasThing && dest.Thing != null) rect = dest.Thing.OccupiedRect().ExpandedBy(1);
                else rect = new CellRect(dest.Cell.x - 1, dest.Cell.z - 1, 3, 3);

                for (int z = rect.minZ; z <= rect.maxZ; z++)
                {
                    for (int x = rect.minX; x <= rect.maxX; x++)
                    {
                        if (x < 0 || z < 0 || x >= Width || z >= Height) continue;
                        int region = cellRegion[x + z * Width];
                        if (region < 0 || region >= destinationAllowed.Length || !destinationAllowed[region]) continue;
                        Prediction p = RegionReachability(region, seedRegions, seedCount, rootComponents, rootComponentCount);
                        if (p == Prediction.Reachable) return Prediction.Reachable;
                    }
                }
                return Prediction.Unreachable;
            }

            private int GatherStartRegions(IntVec3 start, Map map, TraverseParms traverseParams, int[] output)
            {
                int count = 0;
                PathGrid grid;
                try { grid = map.pathing.For(traverseParams).pathGrid; }
                catch { return 0; }

                if (grid.WalkableFast(start))
                {
                    AddUniqueRegion(RegionAt(start), output, ref count);
                    return count;
                }

                for (int i = 0; i < 8; i++)
                {
                    IntVec3 c = start + GenAdj.AdjacentCells[i];
                    if (!c.InBounds(map) || !grid.WalkableFast(c)) continue;
                    AddUniqueRegion(RegionAt(c), output, ref count);
                }
                return count;
            }

            private Prediction RegionReachability(int targetRegion, int[] seedRegions, int seedCount,
                int[] rootComponents, int rootComponentCount)
            {
                if (targetRegion < 0 || targetRegion >= destinationAllowed.Length || !destinationAllowed[targetRegion])
                    return Prediction.Unreachable;
                for (int i = 0; i < seedCount; i++)
                    if (seedRegions[i] == targetRegion) return Prediction.Reachable;
                int targetComponent = components[targetRegion];
                if (targetComponent == 0) return Prediction.Unreachable;
                for (int i = 0; i < rootComponentCount; i++)
                    if (rootComponents[i] == targetComponent) return Prediction.Reachable;
                return Prediction.Unreachable;
            }

            private int RegionAt(IntVec3 c)
            {
                if (c.x < 0 || c.z < 0 || c.x >= Width || c.z >= Height) return -1;
                return cellRegion[c.x + c.z * Width];
            }

            private static void AddUniqueRegion(int region, int[] output, ref int count)
            {
                if (region < 0) return;
                for (int i = 0; i < count; i++) if (output[i] == region) return;
                if (count < output.Length) output[count++] = region;
            }

            private static void AddUniqueComponent(int component, int[] output, ref int count)
            {
                if (component <= 0) return;
                for (int i = 0; i < count; i++) if (output[i] == component) return;
                if (count < output.Length) output[count++] = component;
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