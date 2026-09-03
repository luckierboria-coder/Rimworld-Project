$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "Unified Lean V21 transform anchor not found: $Label" }
    return $Text.Replace($Old, $New)
}

function Replace-RegexOnce {
    param([string]$Text,[string]$Pattern,[string]$Replacement,[string]$Label)
    $match = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) { throw "Unified Lean V21 regex anchor not found: $Label" }
    return $Text.Substring(0, $match.Index) + $Replacement + $Text.Substring($match.Index + $match.Length)
}

# V0.4.21 retires V20 stale-profile authority after runtime evidence showed six soft fuses and
# >1.6M cooldown live bypasses. V19 sliced Region.Allows capture remains, but progress is moved
# out of individual Reachability queries into one bounded round-robin pump per main-thread frame.
# This prevents hundreds of half-built profile arrays from accumulating and checks the time budget
# after every single Region.Allows(false/true) pair instead of after eight pairs.
$v17Path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$v17 = Get-Content $v17Path -Raw

$v17 = Replace-OrThrow $v17 @'
        private const long HardRefreshGraceFrames = 120;
        private const int HardRefreshSampleMask = 7; // 1/8 while stale-while-revalidate is active.
        private const int LeaseProbeSamplesRequired = 4;
        private const long BuildCooldownFrames = 12;
'@ @'
        private const long HardRefreshGraceFrames = 120; // retained only for binary/report compatibility; V21 grants no stale authority.
        private const int HardRefreshSampleMask = 7;
        private const int LeaseProbeSamplesRequired = 4;
        private const int MaxQueuedProfileCaptures = 64;
        private const long ProfileCaptureBackoffFrames = 600;
        private const int SlowProfilePairAbortMicroseconds = 8000;
        private const long BuildCooldownFrames = 12;
'@ 'V21 capture queue constants'

$v17 = Replace-OrThrow $v17 @'
        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();
'@ @'
        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();
        private static readonly List<ProfileCaptureWorkItem> ProfileCaptureQueue = new List<ProfileCaptureWorkItem>(MaxQueuedProfileCaptures);
        private static int profileCaptureQueueCursor;
        private static long profileCaptureLastPumpFrame = -1;
'@ 'V21 capture queue storage'

$v17 = Replace-OrThrow $v17 @'
        private static long profileCaptureBudgetFrame = -1;
        private static long profileCaptureBudgetSpentTicks;
'@ @'
        private static long profileCaptureBudgetFrame = -1;
        private static long profileCaptureBudgetSpentTicks;
        private static long profileCaptureQueuePeak;
        private static long profileCaptureQueueAdmissionBypass;
        private static long profileCapturePumpFrames;
        private static long profileCapturePairs;
        private static long profileCaptureSlowPairAborts;
        private static long profileCapturePairTicksMax;
'@ 'V21 capture queue counters'

# Pump capture work even while the prediction authority fuse is in cooldown. The pump is main-thread
# only and guarded to once per RimMT main-thread frame.
$v17 = Replace-OrThrow $v17 @'
            UpdateRollingFuseMode();
            if (reachFuseMode == ReachFuseMode.Cooldown)
'@ @'
            PumpProfileCaptures(RimMTRuntime.MainThreadFrames);
            UpdateRollingFuseMode();
            if (reachFuseMode == ReachFuseMode.Cooldown)
'@ 'V21 capture pump call'

# Retire V20 stale-while-revalidate authority. A hard-aged profile schedules/coalesces refresh and
# immediately falls back to Vanilla until the replacement is published.
$graceBranch = @'
                bool hardRefreshGrace = false;
                if (profileAge > HardMaxProfileAgeFrames)
                {
                    Interlocked.Increment(ref profileExpired);
                    Interlocked.Increment(ref forcedRefreshCalls);
                    bool firstRefresh = Interlocked.CompareExchange(ref slot.ForcedRefreshPending, 1, 0) == 0;
                    if (firstRefresh)
                    {
                        Interlocked.Increment(ref uniqueForcedRefreshes);
                        Interlocked.Exchange(ref slot.ForcedRefreshStartFrame, now);
                        Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                    }
                    else
                    {
                        Interlocked.Increment(ref forcedRefreshCoalesced);
                    }

                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);

                    long refreshStart = Interlocked.Read(ref slot.ForcedRefreshStartFrame);
                    if (refreshStart <= 0L || now - refreshStart > HardRefreshGraceFrames)
                    {
                        Interlocked.Increment(ref hardRefreshGraceExpired);
                        return true;
                    }

                    // The profile already passed generation/key/map checks above. Keep it only as a
                    // bounded stale-while-revalidate profile while a replacement is being built.
                    hardRefreshGrace = true;
                    Interlocked.Increment(ref hardRefreshGraceQueries);

                    // If its prior lease has expired, regain temporary authority only through the
                    // same four forced-live Vanilla comparisons as the normal lease path.
                    if (leaseUntil <= now)
                    {
                        Prediction refreshPrediction = profile.Classify(start, dest, peMode, map, traverseParams);
                        if (refreshPrediction == Prediction.Unknown)
                        {
                            Interlocked.Increment(ref queriesUnknown);
                            Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                            return true;
                        }

                        bool refreshPredicted = refreshPrediction == Prediction.Reachable;
                        if (refreshPredicted) Interlocked.Increment(ref predictedReachable);
                        else Interlocked.Increment(ref predictedUnreachable);
                        __state = new ReachSampleState(true, refreshPredicted, slot, profile.RegionGeneration, true);
                        Interlocked.Increment(ref shadowSamples);
                        Interlocked.Increment(ref leaseProbeCalls);
                        Interlocked.Increment(ref hardRefreshGraceShadowSamples);
                        return true;
                    }
                }
'@
$hardRefreshBranch = @'
                if (profileAge > HardMaxProfileAgeFrames)
                {
                    Interlocked.Increment(ref profileExpired);
                    Interlocked.Increment(ref forcedRefreshCalls);
                    if (Interlocked.CompareExchange(ref slot.ForcedRefreshPending, 1, 0) == 0)
                        Interlocked.Increment(ref uniqueForcedRefreshes);
                    else
                        Interlocked.Increment(ref forcedRefreshCoalesced);
                    Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                    Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }
'@
$v17 = Replace-OrThrow $v17 $graceBranch $hardRefreshBranch 'V21 retire hard refresh grace branch'

$v17 = Replace-OrThrow $v17 @'
                int validated = Volatile.Read(ref slot.ValidatedMatches);
                int serial = Interlocked.Increment(ref slot.PredictionSerial);
                bool probation = reachFuseMode == ReachFuseMode.Probation;
                int sampleMask = hardRefreshGrace ? HardRefreshSampleMask : SampleMask;
                bool sample = probation || validated < WarmupSamples || (serial & sampleMask) == 0;
                if (sample)
                {
                    __state = new ReachSampleState(true, predicted, slot, profile.RegionGeneration);
                    Interlocked.Increment(ref shadowSamples);
                    if (hardRefreshGrace) Interlocked.Increment(ref hardRefreshGraceShadowSamples);
                    if (probation) probationForcedShadow++;
                    return true;
                }

                __result = predicted;
                if (hardRefreshGrace) Interlocked.Increment(ref hardRefreshGraceAuthoritative);
                if (predicted) Interlocked.Increment(ref authoritativeTrue);
                else Interlocked.Increment(ref authoritativeFalse);
                return false;
'@ @'
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
'@ 'V21 restore normal sampling after grace removal'

$ensurePattern = '(?s)        private static void EnsureProfileScheduled\(.*?\r?\n        \}\r?\n\r?\n        private static TopologySnapshot EnsureTopology'
$ensureReplacement = @'
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
            if (Interlocked.Read(ref slot.CaptureBackoffUntilFrame) > now) return;

            long generation = Interlocked.Read(ref mapState.RegionGeneration);
            ProfileCaptureState capture = slot.CaptureState;
            if (capture != null &&
                (capture.RegionGeneration != generation || capture.MapId != map.uniqueID ||
                 capture.Width != map.Size.x || capture.Height != map.Size.z || !capture.Key.Equals(key)))
            {
                DiscardProfileCapture(slot, true);
                capture = null;
            }

            // V21 capture progress is owned by the once-per-frame round-robin pump, never by an
            // individual Reachability query. Repeated hot queries therefore remain O(1).
            if (capture != null) return;
            if (Volatile.Read(ref slot.BuildScheduled) != 0) return;

            long last = Interlocked.Read(ref slot.LastScheduleFrame);
            if (last != 0 && now - last < BuildCooldownFrames) return;

            if (ProfileCaptureQueue.Count >= MaxQueuedProfileCaptures)
            {
                Interlocked.Increment(ref profileCaptureQueueAdmissionBypass);
                return;
            }

            if (Interlocked.CompareExchange(ref slot.BuildScheduled, 1, 0) != 0) return;

            TopologySnapshot topology = EnsureTopology(map, mapState);
            if (topology == null)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                return;
            }

            generation = Interlocked.Read(ref mapState.RegionGeneration);
            if (topology.RegionGeneration != generation || topology.MapId != map.uniqueID ||
                topology.Width != map.Size.x || topology.Height != map.Size.z)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                return;
            }

            capture = new ProfileCaptureState(topology, key, now);
            slot.CaptureState = capture;
            ProfileCaptureQueue.Add(new ProfileCaptureWorkItem(map, mapState, pawn, traverseParams, slot, capture));
            Interlocked.Increment(ref profileCaptureStarts);
            UpdateMax(ref profileCaptureQueuePeak, ProfileCaptureQueue.Count);
        }

        private static void PumpProfileCaptures(long now)
        {
            if (!RimMTThreadGuard.IsMainThread || profileCaptureLastPumpFrame == now) return;
            profileCaptureLastPumpFrame = now;
            if (ProfileCaptureQueue.Count == 0) return;

            Interlocked.Increment(ref profileCapturePumpFrames);
            long pumpStart = Stopwatch.GetTimestamp();
            long budgetTicks = ProfileCaptureBudgetTicks();
            bool didWork = false;

            while (ProfileCaptureQueue.Count > 0)
            {
                if (didWork && Stopwatch.GetTimestamp() - pumpStart >= budgetTicks)
                {
                    Interlocked.Increment(ref profileCaptureBudgetBypass);
                    break;
                }

                if (profileCaptureQueueCursor >= ProfileCaptureQueue.Count)
                    profileCaptureQueueCursor = 0;

                ProfileCaptureWorkItem item = ProfileCaptureQueue[profileCaptureQueueCursor];
                bool remove = false;
                ProfileCaptureState capture = item.Capture;
                ProfileSlot slot = item.Slot;

                if (item.Map == null || item.Map.Disposed || item.Pawn == null || !item.Pawn.Spawned || item.Pawn.Map != item.Map ||
                    slot == null || !ReferenceEquals(slot.CaptureState, capture) ||
                    Interlocked.Read(ref item.MapState.RegionGeneration) != capture.RegionGeneration)
                {
                    if (slot != null && ReferenceEquals(slot.CaptureState, capture))
                        DiscardProfileCapture(slot, true);
                    remove = true;
                }
                else if (capture.RegionCursor >= capture.Topology.RegionRefs.Length)
                {
                    FinalizeProfileCapture(item, now);
                    remove = true;
                }
                else
                {
                    int i = capture.RegionCursor;
                    Region region = capture.Topology.RegionRefs[i];
                    if (region == null || !region.valid)
                    {
                        DiscardProfileCapture(slot, true);
                        remove = true;
                    }
                    else
                    {
                        long pairStart = Stopwatch.GetTimestamp();
                        bool traverseAllowed;
                        bool destinationAllowed;
                        try
                        {
                            traverseAllowed = region.Allows(item.TraverseParms, false);
                            destinationAllowed = region.Allows(item.TraverseParms, true);
                        }
                        catch
                        {
                            DiscardProfileCapture(slot, false);
                            remove = true;
                            traverseAllowed = false;
                            destinationAllowed = false;
                        }

                        long pairElapsed = Stopwatch.GetTimestamp() - pairStart;
                        didWork = true;
                        Interlocked.Increment(ref profileCapturePairs);
                        Interlocked.Add(ref profileCaptureTicks, pairElapsed);
                        UpdateMax(ref profileCaptureTicksMax, pairElapsed);
                        UpdateMax(ref profileCapturePairTicksMax, pairElapsed);

                        if (!remove)
                        {
                            if (pairElapsed >= SlowProfilePairAbortTicks())
                            {
                                Interlocked.Increment(ref profileCaptureSlowPairAborts);
                                Interlocked.Exchange(ref slot.CaptureBackoffUntilFrame, now + ProfileCaptureBackoffFrames);
                                Interlocked.Exchange(ref slot.ForcedRefreshPending, 0);
                                Interlocked.Exchange(ref slot.ForcedRefreshStartFrame, 0L);
                                DiscardProfileCapture(slot, false);
                                remove = true;
                            }
                            else
                            {
                                capture.TraverseAllowed[i] = traverseAllowed;
                                capture.DestinationAllowed[i] = destinationAllowed;
                                capture.RegionCursor++;
                                capture.LastSliceFrame = now;
                                if (capture.RegionCursor >= capture.Topology.RegionRefs.Length)
                                {
                                    FinalizeProfileCapture(item, now);
                                    remove = true;
                                }
                            }
                        }
                    }
                }

                if (remove)
                {
                    if (profileCaptureQueueCursor < ProfileCaptureQueue.Count)
                        ProfileCaptureQueue.RemoveAt(profileCaptureQueueCursor);
                    if (profileCaptureQueueCursor >= ProfileCaptureQueue.Count)
                        profileCaptureQueueCursor = 0;
                }
                else
                {
                    profileCaptureQueueCursor++;
                    if (profileCaptureQueueCursor >= ProfileCaptureQueue.Count)
                        profileCaptureQueueCursor = 0;
                }
            }

            if (didWork) Interlocked.Increment(ref profileCaptureSlices);
        }

        private static void FinalizeProfileCapture(ProfileCaptureWorkItem item, long now)
        {
            if (item == null || item.Slot == null || item.Capture == null) return;
            ProfileSlot slot = item.Slot;
            ProfileCaptureState capture = item.Capture;
            if (!ReferenceEquals(slot.CaptureState, capture)) return;
            if (Interlocked.Read(ref item.MapState.RegionGeneration) != capture.RegionGeneration)
            {
                DiscardProfileCapture(slot, true);
                return;
            }

            slot.CaptureState = null;
            Interlocked.Increment(ref profileCaptures);

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
                return;
            }

            ProfileBuildContext context = new ProfileBuildContext(
                capture.MapId, capture.Width, capture.Height, capture.RegionGeneration, now, capture.Key,
                capture.Topology.CellRegion, capture.Topology.DistrictByRegion, capture.Topology.EdgeOffsets,
                capture.Topology.Edges, capture.TraverseAllowed, capture.DestinationAllowed);

            bool accepted = scheduler.TryEnqueue(FeatureId, AdaptiveLoadBalancer.RecommendedOffloadPriority, delegate
            {
                BuildAndPublishProfile(item.MapState, slot, context);
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

        private static void DiscardProfileCapture(ProfileSlot slot, bool stale)
        {
            if (slot == null) return;
            if (slot.CaptureState != null)
            {
                slot.CaptureState = null;
                Interlocked.Increment(ref profileCaptureDiscarded);
            }
            Volatile.Write(ref slot.BuildScheduled, 0);
            if (stale) Interlocked.Increment(ref buildsStale);
            else Interlocked.Increment(ref buildsRejected);
        }

        private static long SlowProfilePairAbortTicks()
        {
            return Math.Max(1L, Stopwatch.Frequency * SlowProfilePairAbortMicroseconds / 1000000L);
        }

        private static long ProfileCaptureBudgetTicks()
        {
            return Math.Max(1L, Stopwatch.Frequency * ProfileCaptureBudgetMicroseconds() / 1000000L);
        }

        private static int ProfileCaptureBudgetMicroseconds()
        {
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: return 2000;
                case LoadPressure.Normal: return 1500;
                case LoadPressure.High: return 1000;
                default: return 500;
            }
        }

        private static TopologySnapshot EnsureTopology
'@
$v17 = Replace-RegexOnce $v17 $ensurePattern $ensureReplacement 'V21 queued profile capture implementation'

# Add the queue item and per-slot capture backoff field to the generated V19 state types.
$v17 = Replace-OrThrow $v17 @'
        internal sealed class ProfileCaptureState
        {
'@ @'
        internal sealed class ProfileCaptureWorkItem
        {
            internal readonly Map Map;
            internal readonly MapState MapState;
            internal readonly Pawn Pawn;
            internal readonly TraverseParms TraverseParms;
            internal readonly ProfileSlot Slot;
            internal readonly ProfileCaptureState Capture;

            internal ProfileCaptureWorkItem(Map map, MapState mapState, Pawn pawn, TraverseParms traverseParms, ProfileSlot slot, ProfileCaptureState capture)
            {
                Map = map;
                MapState = mapState;
                Pawn = pawn;
                TraverseParms = traverseParms;
                Slot = slot;
                Capture = capture;
            }
        }

        internal sealed class ProfileCaptureState
        {
'@ 'V21 capture work item type'

$v17 = Replace-OrThrow $v17 @'
            internal int ForcedRefreshPending;
            internal long ForcedRefreshStartFrame;
            internal ProfileCaptureState CaptureState;
'@ @'
            internal int ForcedRefreshPending;
            internal long ForcedRefreshStartFrame;
            internal long CaptureBackoffUntilFrame;
            internal ProfileCaptureState CaptureState;
'@ 'V21 per-slot capture backoff'

# V21 report: stale grace counters are deliberately removed from the production summary. New queue
# metrics make backlog and single-region pathologies visible without a resident profiler.
$v17 = Replace-OrThrow $v17 @'
                ", forcedRefreshCoalesced=" + Interlocked.Read(ref forcedRefreshCoalesced) +
                ", hardRefreshGraceQueries=" + Interlocked.Read(ref hardRefreshGraceQueries) +
                ", hardRefreshGraceShadow=" + Interlocked.Read(ref hardRefreshGraceShadowSamples) +
                ", hardRefreshGraceAuthoritative=" + Interlocked.Read(ref hardRefreshGraceAuthoritative) +
                ", hardRefreshGraceExpired=" + Interlocked.Read(ref hardRefreshGraceExpired) +
                ", rollingMode=" + reachFuseMode +
'@ @'
                ", forcedRefreshCoalesced=" + Interlocked.Read(ref forcedRefreshCoalesced) +
                ", captureQueue=" + ProfileCaptureQueue.Count +
                ", captureQueuePeak=" + Interlocked.Read(ref profileCaptureQueuePeak) +
                ", captureQueueAdmissionBypass=" + Interlocked.Read(ref profileCaptureQueueAdmissionBypass) +
                ", capturePumpFrames=" + Interlocked.Read(ref profileCapturePumpFrames) +
                ", capturePairs=" + Interlocked.Read(ref profileCapturePairs) +
                ", slowPairAborts=" + Interlocked.Read(ref profileCaptureSlowPairAborts) +
                ", rollingMode=" + reachFuseMode +
'@ 'V21 queue summary counters'

$v17 = Replace-OrThrow $v17 @'
                ", hardMaxProfileAgeFrames=" + HardMaxProfileAgeFrames +
                ", hardRefreshGraceFrames=" + HardRefreshGraceFrames +
                ", hardRefreshSampleEvery=8" +
                ", leaseProbeRequired=" + LeaseProbeSamplesRequired +
'@ @'
                ", hardMaxProfileAgeFrames=" + HardMaxProfileAgeFrames +
                ", profileCaptureQueueLimit=" + MaxQueuedProfileCaptures +
                ", slowPairAbortUs=" + SlowProfilePairAbortMicroseconds +
                ", leaseProbeRequired=" + LeaseProbeSamplesRequired +
'@ 'V21 queue policy summary'

$v17 = Replace-OrThrow $v17 '            return "Aggressive reachability profile V0.4.20 sliced-profile/bounded-refresh-grace: compatibilityReady=" + compatibilityReady +' '            return "Aggressive reachability profile V0.4.21 queued-sliced-profile/local-first: compatibilityReady=" + compatibilityReady +' 'V21 summary label'
$v17 = Replace-OrThrow $v17 '[RimMT] parallel.reachProfile V0.4.20 installed: V19 sliced capture retained; hard refresh uses a bounded 120-frame stale-while-revalidate grace with 4-probe lease recovery and 1/8 shadow validation; local-first fuse remains active.' '[RimMT] parallel.reachProfile V0.4.21 installed: stale hard-refresh authority retired; Region.Allows capture is queued and round-robin pumped once per frame with a 64-state cap, per-pair budget checks, and 8ms slow-pair backoff; local-first fuse remains active.' 'V21 install log'
$v17 = Replace-OrThrow $v17 'Topology and Region.Allows profile arrays are captured incrementally on the main thread; workers consume primitive immutable arrays only. Hard-refresh stale authority is bounded to 120 frames, generation-stable only, lease-gated, and shadowed at 1/8; Vanilla remains authoritative after grace expiry or any mismatch.' 'Topology and Region.Allows profile arrays are captured incrementally on the main thread; workers consume primitive immutable arrays only. V21 grants no stale hard-refresh authority; capture progress is bounded by a once-per-frame round-robin queue and any >8ms single Region.Allows pair is backed off for 600 frames.' 'V21 safety summary text'
Set-Content $v17Path $v17 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'ReachProfile=V0.4.20 sliced capture + bounded hard-refresh grace + local-first fuse;' 'ReachProfile=V0.4.21 queued sliced capture + no stale authority + local-first fuse;' 'diagnostics V21 ReachProfile policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V21 transforms: retired stale grace; queued round-robin profile capture; 64-state cap; per-Region pair budget checks; slow-pair backoff.'