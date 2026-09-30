$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V19 transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

function Replace-RegexOnce {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Replacement,
        [string]$Label
    )
    $match = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "Unified Lean V19 regex anchor not found: $Label"
    }
    $second = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline, $match.Index + $match.Length)
    if ($second.Success) {
        throw "Unified Lean V19 regex anchor matched more than once: $Label"
    }
    return $Text.Substring(0, $match.Index) + $Replacement + $Text.Substring($match.Index + $match.Length)
}

# This script runs after ApplyUnifiedLeanTransforms.ps1. The source on disk is therefore the
# V0.4.18 production form even though the checked-in baseline file retains its V0.4.17 name.
$v17Path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$v17 = Get-Content $v17Path -Raw

$v17 = Replace-OrThrow $v17 @'
        private static long profileCaptures;
        private static long profileCaptureTicks;
        private static long profileCaptureTicksMax;
'@ @'
        private static long profileCaptures;
        private static long profileCaptureStarts;
        private static long profileCaptureDiscarded;
        private static long profileCaptureSlices;
        private static long profileCaptureBudgetBypass;
        private static long profileCaptureTicks;
        private static long profileCaptureTicksMax;
        private static long profileCaptureBudgetFrame = -1;
        private static long profileCaptureBudgetSpentTicks;
'@ 'ReachProfile V19 profile-capture counters'

$v17 = Replace-OrThrow $v17 @'
        private static long leaseRenewals;
        private static long leaseFailures;
        private static long forcedRefreshes;
'@ @'
        private static long leaseRenewals;
        private static long leaseFailures;
        private static long forcedRefreshCalls;
        private static long uniqueForcedRefreshes;
        private static long forcedRefreshCoalesced;
'@ 'ReachProfile V19 hard-refresh counters'

$v17 = Replace-OrThrow $v17 @'
                if (profileAge > HardMaxProfileAgeFrames)
                {
                    Interlocked.Increment(ref profileExpired);
                    Interlocked.Increment(ref forcedRefreshes);
                    Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                    Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }
'@ @'
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
'@ 'ReachProfile V19 hard-refresh coalescing'

$v17 = Replace-OrThrow $v17 @'
            Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
            Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, 0L);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
'@ @'
            Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
            Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, 0L);
            Interlocked.Exchange(ref __state.Slot.ForcedRefreshPending, 0);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
'@ 'ReachProfile V19 mismatch refresh reset'

$v17 = Replace-OrThrow $v17 @'
                Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
                Volatile.Write(ref slot.Published, profile);
'@ @'
                Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
                Interlocked.Exchange(ref slot.ForcedRefreshPending, 0);
                slot.CaptureState = null;
                Volatile.Write(ref slot.Published, profile);
'@ 'ReachProfile V19 publish refresh reset'

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
            long generation = Interlocked.Read(ref mapState.RegionGeneration);
            ProfileCaptureState capture = slot.CaptureState;

            if (capture != null &&
                (capture.RegionGeneration != generation || capture.MapId != map.uniqueID ||
                 capture.Width != map.Size.x || capture.Height != map.Size.z || !capture.Key.Equals(key)))
            {
                DiscardProfileCapture(slot, true);
                capture = null;
            }

            if (capture == null)
            {
                // BuildScheduled without a capture state means the immutable worker build is pending.
                if (Volatile.Read(ref slot.BuildScheduled) != 0) return;

                long last = Interlocked.Read(ref slot.LastScheduleFrame);
                if (last != 0 && now - last < BuildCooldownFrames) return;
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
                Interlocked.Increment(ref profileCaptureStarts);
                // Do not stack a profile capture slice on top of a topology slice in the same frame.
                return;
            }

            if (capture.LastSliceFrame == now) return;
            long budgetTicks = RemainingProfileCaptureBudgetTicks(now);
            if (budgetTicks <= 0L)
            {
                Interlocked.Increment(ref profileCaptureBudgetBypass);
                return;
            }

            capture.LastSliceFrame = now;
            long sliceStart = Stopwatch.GetTimestamp();
            bool complete = false;
            bool discarded = false;
            try
            {
                int processed = 0;
                Region[] regions = capture.Topology.RegionRefs;
                while (capture.RegionCursor < regions.Length)
                {
                    if (Interlocked.Read(ref mapState.RegionGeneration) != capture.RegionGeneration)
                    {
                        discarded = true;
                        DiscardProfileCapture(slot, true);
                        return;
                    }

                    int i = capture.RegionCursor;
                    Region region = regions[i];
                    if (region == null || !region.valid)
                    {
                        discarded = true;
                        DiscardProfileCapture(slot, true);
                        return;
                    }

                    capture.TraverseAllowed[i] = region.Allows(traverseParams, false);
                    capture.DestinationAllowed[i] = region.Allows(traverseParams, true);
                    capture.RegionCursor++;
                    processed++;

                    // Stopwatch checks are deliberately batched: the capture itself is hot, while
                    // eight Region.Allows pairs are still a much finer preemption unit than the old
                    // whole-profile capture.
                    if ((processed & 7) == 0 && Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
                        break;
                }
                complete = capture.RegionCursor >= regions.Length;
            }
            catch
            {
                discarded = true;
                DiscardProfileCapture(slot, false);
                return;
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - sliceStart;
                RecordProfileCaptureBudget(now, elapsed);
                Interlocked.Increment(ref profileCaptureSlices);
                Interlocked.Add(ref profileCaptureTicks, elapsed);
                UpdateMax(ref profileCaptureTicksMax, elapsed);
            }

            if (discarded || !complete) return;
            if (Interlocked.Read(ref mapState.RegionGeneration) != capture.RegionGeneration)
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
                capture.MapId,
                capture.Width,
                capture.Height,
                capture.RegionGeneration,
                now,
                capture.Key,
                capture.Topology.CellRegion,
                capture.Topology.DistrictByRegion,
                capture.Topology.EdgeOffsets,
                capture.Topology.Edges,
                capture.TraverseAllowed,
                capture.DestinationAllowed);

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

        private static long RemainingProfileCaptureBudgetTicks(long frame)
        {
            if (profileCaptureBudgetFrame != frame)
            {
                profileCaptureBudgetFrame = frame;
                profileCaptureBudgetSpentTicks = 0L;
            }
            long budget = ProfileCaptureBudgetTicks();
            long remaining = budget - profileCaptureBudgetSpentTicks;
            return remaining > 0L ? remaining : 0L;
        }

        private static void RecordProfileCaptureBudget(long frame, long elapsed)
        {
            if (profileCaptureBudgetFrame != frame)
            {
                profileCaptureBudgetFrame = frame;
                profileCaptureBudgetSpentTicks = 0L;
            }
            profileCaptureBudgetSpentTicks += Math.Max(0L, elapsed);
        }

        private static long ProfileCaptureBudgetTicks()
        {
            return Math.Max(1L, Stopwatch.Frequency * ProfileCaptureBudgetMicroseconds() / 1000000L);
        }

        private static int ProfileCaptureBudgetMicroseconds()
        {
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: return 1500;
                case LoadPressure.Normal: return 1000;
                case LoadPressure.High: return 750;
                default: return 500;
            }
        }

        private static TopologySnapshot EnsureTopology
'@
$v17 = Replace-RegexOnce $v17 $ensurePattern $ensureReplacement 'ReachProfile V19 sliced profile capture method'

$v17 = Replace-OrThrow $v17 @'
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
                            if ((build.RegionCursor & 3) == 0 && BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
'@ @'
                    case TopologyBuildPhase.Adjacency:
                        while (build.RegionCursor < build.Regions.Count)
                        {
                            int i = build.RegionCursor;
                            Region region = build.Regions[i];
                            if (region == null || !region.valid) return TopologyAdvanceResult.Stale;

                            List<int> adjacency = build.Adjacency[i];
                            if (adjacency == null)
                            {
                                adjacency = new List<int>(4);
                                build.Adjacency[i] = adjacency;
                                build.AdjacencyLinkCursor = 0;
                            }

                            List<RegionLink> links = region.links;
                            int linkCount = links == null ? 0 : links.Count;
                            while (build.AdjacencyLinkCursor < linkCount)
                            {
                                RegionLink link = links[build.AdjacencyLinkCursor++];
                                if (link != null)
                                {
                                    for (int side = 0; side < 2; side++)
                                    {
                                        Region other = link.regions[side];
                                        if (other == null || ReferenceEquals(other, region) || !other.valid) continue;
                                        int otherIndex;
                                        if (!build.RegionIndex.TryGetValue(other, out otherIndex)) continue;
                                        if (!adjacency.Contains(otherIndex)) adjacency.Add(otherIndex);
                                    }
                                }

                                // Topology builds are rare, so checking after every link is worth the
                                // tiny timestamp overhead to prevent one high-degree region from
                                // monopolizing a whole slice.
                                if (BudgetSpent(sliceStart, budgetTicks))
                                    return TopologyAdvanceResult.Pending;
                            }

                            build.RegionCursor++;
                            build.AdjacencyLinkCursor = 0;
                            if (BudgetSpent(sliceStart, budgetTicks))
                                return TopologyAdvanceResult.Pending;
                        }
'@ 'ReachProfile V19 adjacency link cursor slicing'

$v17 = Replace-OrThrow $v17 @'
            internal int CellCursor;
            internal int RegionCursor;
            internal int NextDistrict = 1;
'@ @'
            internal int CellCursor;
            internal int RegionCursor;
            internal int AdjacencyLinkCursor;
            internal int NextDistrict = 1;
'@ 'ReachProfile V19 topology adjacency cursor field'

$v17 = Replace-OrThrow $v17 @'
        internal sealed class ProfileSlot
        {
            internal int BuildScheduled;
            internal long LastScheduleFrame;
            internal long DisabledUntilFrame;
            internal int ValidatedMatches;
            internal int PredictionSerial;
            internal int LeaseProbeMatches;
            internal long LeaseUntilFrame;
            internal ProfileSnapshot Published;
        }
'@ @'
        internal sealed class ProfileSlot
        {
            internal int BuildScheduled;
            internal long LastScheduleFrame;
            internal long DisabledUntilFrame;
            internal int ValidatedMatches;
            internal int PredictionSerial;
            internal int LeaseProbeMatches;
            internal long LeaseUntilFrame;
            internal int ForcedRefreshPending;
            internal ProfileCaptureState CaptureState;
            internal ProfileSnapshot Published;
        }

        private sealed class ProfileCaptureState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long RegionGeneration;
            internal readonly TraverseKey Key;
            internal readonly TopologySnapshot Topology;
            internal readonly bool[] TraverseAllowed;
            internal readonly bool[] DestinationAllowed;
            internal int RegionCursor;
            internal long LastSliceFrame;

            internal ProfileCaptureState(TopologySnapshot topology, TraverseKey key, long frame)
            {
                Topology = topology;
                MapId = topology.MapId;
                Width = topology.Width;
                Height = topology.Height;
                RegionGeneration = topology.RegionGeneration;
                Key = key;
                TraverseAllowed = new bool[topology.RegionRefs.Length];
                DestinationAllowed = new bool[topology.RegionRefs.Length];
                RegionCursor = 0;
                LastSliceFrame = frame;
            }
        }
'@ 'ReachProfile V19 profile capture state'

$v17 = Replace-OrThrow $v17 @'
            long captures = Interlocked.Read(ref profileCaptures);
            long published = Interlocked.Read(ref buildsPublished);
'@ @'
            long captures = Interlocked.Read(ref profileCaptures);
            long captureSlices = Interlocked.Read(ref profileCaptureSlices);
            long published = Interlocked.Read(ref buildsPublished);
'@ 'ReachProfile V19 summary capture-slice local'

$v17 = Replace-OrThrow $v17 @'
            double avgCaptureUs = captures == 0 ? 0.0 : (Interlocked.Read(ref profileCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / captures;
            double maxCaptureUs = Interlocked.Read(ref profileCaptureTicksMax) * 1000000.0 / Stopwatch.Frequency;
'@ @'
            double profileCaptureWorkMs = Interlocked.Read(ref profileCaptureTicks) * 1000.0 / Stopwatch.Frequency;
            double avgCaptureUs = captures == 0 ? 0.0 : (Interlocked.Read(ref profileCaptureTicks) * 1000000.0 / Stopwatch.Frequency) / captures;
            double maxCaptureUs = Interlocked.Read(ref profileCaptureTicksMax) * 1000000.0 / Stopwatch.Frequency;
'@ 'ReachProfile V19 summary capture work metrics'

$v17 = Replace-OrThrow $v17 '            return "Aggressive reachability profile V0.4.18 sliced/local-first/lease: compatibilityReady=" + compatibilityReady +' '            return "Aggressive reachability profile V0.4.19 sliced-profile/local-first/lease: compatibilityReady=" + compatibilityReady +' 'ReachProfile V19 summary label'
$v17 = Replace-OrThrow $v17 '[RimMT] parallel.reachProfile V0.4.18 installed: topology capture is frame-sliced with finer budget checks; expired profiles require four clean forced-live lease probes; mismatch handling remains local-slot-first and emergency hard fuse remains 16/256.' '[RimMT] parallel.reachProfile V0.4.19 installed: topology and per-pawn Region.Allows profile capture are both main-thread budget-sliced; 4-probe lease and local-first fuse remain authoritative safeguards.' 'ReachProfile V19 install log'

$v17 = Replace-OrThrow $v17 @'
                ", profileCaptures=" + captures +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
'@ @'
                ", profileCaptureStarts=" + Interlocked.Read(ref profileCaptureStarts) +
                ", profileCaptures=" + captures +
                ", profileCaptureDiscarded=" + Interlocked.Read(ref profileCaptureDiscarded) +
                ", profileCaptureSlices=" + captureSlices +
                ", profileCaptureBudgetUs=" + ProfileCaptureBudgetMicroseconds() +
                ", profileCaptureBudgetBypass=" + Interlocked.Read(ref profileCaptureBudgetBypass) +
                ", profileCaptureWorkMs=" + profileCaptureWorkMs.ToString("F2") +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
'@ 'ReachProfile V19 summary capture counters'

$v17 = Replace-OrThrow $v17 @'
                ", forcedRefreshes=" + Interlocked.Read(ref forcedRefreshes) +
                ", rollingMode=" + reachFuseMode +
'@ @'
                ", forcedRefreshCalls=" + Interlocked.Read(ref forcedRefreshCalls) +
                ", uniqueForcedRefreshes=" + Interlocked.Read(ref uniqueForcedRefreshes) +
                ", forcedRefreshCoalesced=" + Interlocked.Read(ref forcedRefreshCoalesced) +
                ", rollingMode=" + reachFuseMode +
'@ 'ReachProfile V19 summary hard refresh counters'

$v17 = Replace-OrThrow $v17 @'
                ", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +
'@ @'
                ", avgProfileCaptureWorkUs=" + avgCaptureUs.ToString("F2") +
                ", maxProfileCaptureSliceUs=" + maxCaptureUs.ToString("F2") +
'@ 'ReachProfile V19 summary sliced capture labels'

$v17 = Replace-OrThrow $v17 'Topology is captured incrementally on the main thread; workers consume primitive immutable arrays only.' 'Topology and Region.Allows profile arrays are captured incrementally on the main thread; workers consume primitive immutable arrays only.' 'ReachProfile V19 safety summary text'

Set-Content $v17Path $v17 -Encoding UTF8

# Extend the S4 targeted prefilter only where RimWorld 1.5 has explicit cheap-negative conditions.
# Before any duplicated Vanilla condition is used, the full HasJobOnThing inheritance chain is
# checked for Harmony patches. Any foreign authority makes this prefilter fail open for that type.
$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long penPrefilterTakeToPenRejected;
        private static long penPrefilterRoamingRejected;
        private static long failures;
'@ @'
        private static long penPrefilterTakeToPenRejected;
        private static long penPrefilterRoamingRejected;
        private static long targetedPrefilterCalls;
        private static long targetedPrefilterRejected;
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedDoctorAnimalsRejected;
        private static long prefilterAuthorityBypass;
        private static long failures;
'@ 'S4 V19 targeted prefilter counters'

$s4 = Replace-OrThrow $s4 @'
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyWorkGivers = new Dictionary<string, HeavyValidatorStats>();
        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();
'@ @'
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyWorkGivers = new Dictionary<string, HeavyValidatorStats>();
        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, bool> PrefilterAuthorityCache = new Dictionary<Type, bool>();
'@ 'S4 V19 prefilter authority cache'

$s4 = Replace-OrThrow $s4 @'
            WorkGiver_Scanner resolvedScanner = TryResolveScanner(validator);
            PenPrefilterKind penKind = ResolvePenPrefilter(resolvedScanner);
            if (penKind != PenPrefilterKind.None && kept > 0)
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    penPrefilterCalls++;
                    Candidate candidate = candidates[i];
                    if (!PassPenCheapNegative(penKind, traverseParms.pawn, candidate.Thing))
                    {
                        localValidatorRejected++;
                        penPrefilterRejected++;
                        if (penKind == PenPrefilterKind.TakeRoamingAnimalsToPen) penPrefilterRoamingRejected++;
                        else penPrefilterTakeToPenRejected++;
                        continue;
                    }
                    candidates[write++] = candidate;
                }
                kept = write;
            }
'@ @'
            WorkGiver_Scanner resolvedScanner = TryResolveScanner(validator);
            bool authoritySafe = IsPrefilterAuthoritySafe(resolvedScanner);
            if (resolvedScanner != null && !authoritySafe) prefilterAuthorityBypass++;

            PenPrefilterKind penKind = authoritySafe ? ResolvePenPrefilter(resolvedScanner) : PenPrefilterKind.None;
            if (penKind != PenPrefilterKind.None && kept > 0)
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    penPrefilterCalls++;
                    Candidate candidate = candidates[i];
                    if (!PassPenCheapNegative(penKind, traverseParms.pawn, candidate.Thing))
                    {
                        localValidatorRejected++;
                        penPrefilterRejected++;
                        if (penKind == PenPrefilterKind.TakeRoamingAnimalsToPen) penPrefilterRoamingRejected++;
                        else penPrefilterTakeToPenRejected++;
                        continue;
                    }
                    candidates[write++] = candidate;
                }
                kept = write;
            }

            TargetedPrefilterKind targetedKind = authoritySafe ? ResolveTargetedPrefilter(resolvedScanner) : TargetedPrefilterKind.None;
            if (targetedKind != TargetedPrefilterKind.None && kept > 0)
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    targetedPrefilterCalls++;
                    Candidate candidate = candidates[i];
                    if (!PassTargetedCheapNegative(targetedKind, traverseParms.pawn, candidate.Thing))
                    {
                        localValidatorRejected++;
                        targetedPrefilterRejected++;
                        if (targetedKind == TargetedPrefilterKind.FeedHemogen) targetedFeedHemogenRejected++;
                        else if (targetedKind == TargetedPrefilterKind.VisitSickPawn) targetedVisitSickRejected++;
                        else targetedDoctorAnimalsRejected++;
                        continue;
                    }
                    candidates[write++] = candidate;
                }
                kept = write;
            }
'@ 'S4 V19 authority-safe targeted candidate compaction'

$s4 = Replace-OrThrow $s4 @'
        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ @'
        private static bool IsPrefilterAuthoritySafe(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return false;
            Type type = scanner.GetType();
            bool cached;
            if (PrefilterAuthorityCache.TryGetValue(type, out cached)) return cached;

            bool safe = true;
            try
            {
                Type current = type;
                Type[] args = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
                while (current != null && typeof(WorkGiver).IsAssignableFrom(current))
                {
                    MethodInfo method = current.GetMethod("HasJobOnThing",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, args, null);
                    if (method != null)
                    {
                        Patches info = Harmony.GetPatchInfo(method);
                        if (info != null &&
                            (info.Prefixes.Count != 0 || info.Postfixes.Count != 0 ||
                             info.Transpilers.Count != 0 || info.Finalizers.Count != 0))
                        {
                            safe = false;
                            break;
                        }
                    }
                    current = current.BaseType;
                }
            }
            catch
            {
                safe = false;
            }

            PrefilterAuthorityCache[type] = safe;
            return safe;
        }

        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null || scanner.def == null) return TargetedPrefilterKind.None;
            string defName = scanner.def.defName;
            Type type = scanner.GetType();
            if (defName == "FeedHemogen" && type == typeof(Workgiver_AdministerHemogen))
                return TargetedPrefilterKind.FeedHemogen;
            if (defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            if (defName == "DoctorTendToAnimals" && type == typeof(WorkGiver_TendOther_Animal))
                return TargetedPrefilterKind.DoctorTendAnimals;
            return TargetedPrefilterKind.None;
        }

        private static bool PassTargetedCheapNegative(TargetedPrefilterKind kind, Pawn worker, Thing thing)
        {
            try
            {
                if (kind == TargetedPrefilterKind.FeedHemogen)
                {
                    Pawn patient = thing as Pawn;
                    if (patient == null || ReferenceEquals(patient, worker)) return false;
                    Gene_Hemogen gene = patient.genes == null ? null : patient.genes.GetFirstGeneOfType<Gene_Hemogen>();
                    if (gene == null || gene.ValuePercent >= 0.95f) return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.VisitSickPawn)
                {
                    Pawn sick = thing as Pawn;
                    if (sick == null || worker == null) return false;
                    if (!sick.IsColonist || sick.IsSlave || worker.IsSlave || worker.RaceProps == null ||
                        !worker.RaceProps.Humanlike || sick.Dead || ReferenceEquals(worker, sick) ||
                        !sick.InBed() || !sick.Awake() || sick.IsForbidden(worker))
                        return false;
                    if (sick.needs == null || sick.needs.joy == null || sick.needs.joy.CurCategory > JoyCategory.VeryLow)
                        return false;
                    if (!InteractionUtility.CanReceiveInteraction(sick)) return false;
                    if (sick.needs.food != null && sick.needs.food.Starving) return false;
                    if (sick.needs.rest != null && sick.needs.rest.CurLevel <= 0.33f) return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.DoctorTendAnimals)
                {
                    Pawn patient = thing as Pawn;
                    if (patient == null || worker == null || ReferenceEquals(patient, worker)) return false;
                    if (patient.RaceProps == null || !patient.RaceProps.Animal) return false;
                    if (!WorkGiver_Tend.GoodLayingStatusForTend(patient, worker)) return false;
                    return true;
                }
                return true;
            }
            catch
            {
                // Fail open: the original validator remains authoritative on any unexpected state.
                return true;
            }
        }

        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ 'S4 V19 authority and targeted helpers'

$s4 = Replace-OrThrow $s4 @'
                   ", penPrefilterRejected=" + penPrefilterRejected +
                   " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "]" +
                   ", failures=" + failures +
'@ @'
                   ", penPrefilterRejected=" + penPrefilterRejected +
                   " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "]" +
                   ", targetedPrefilterCalls=" + targetedPrefilterCalls +
                   ", targetedPrefilterRejected=" + targetedPrefilterRejected +
                   " [feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected +
                   ", doctorAnimals=" + targetedDoctorAnimalsRejected + "]" +
                   ", prefilterAuthorityBypass=" + prefilterAuthorityBypass +
                   ", failures=" + failures +
'@ 'S4 V19 targeted prefilter summary'

$s4 = Replace-OrThrow $s4 @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
'@ @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, FeedHemogen, VisitSickPawn, DoctorTendAnimals }
'@ 'S4 V19 targeted prefilter enum'

Set-Content $s4Path $s4 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'ReachProfile=V0.4.18 sliced topology + 4-probe lease + local-first fuse;' 'ReachProfile=V0.4.19 sliced topology/profile capture + 4-probe lease + local-first fuse;' 'diagnostics V19 ReachProfile policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V19 transforms: globally budgeted sliced Region.Allows capture; hard-refresh coalescing; adjacency link cursor slicing; authority-safe targeted S4 prefilters.'
