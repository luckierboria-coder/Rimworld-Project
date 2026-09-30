$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V20 transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V0.4.20 fixes the V19 hard-refresh throughput regression without undoing sliced capture.
# A profile older than the 720-frame hard age may remain temporarily usable only while its
# replacement capture/build is in progress, for at most 120 main-thread frames. Existing lease
# authority is preserved; if no lease is active, the old profile must first pass the same four
# forced-live Vanilla comparisons used by the normal lease path. During this bounded grace window
# ordinary authority is shadow-sampled at 1/8 instead of 1/128. Any mismatch still clears the
# profile and locally quarantines the slot. Region-generation mismatches never enter this path.
$v17Path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$v17 = Get-Content $v17Path -Raw

$v17 = Replace-OrThrow $v17 @'
        private const long HardMaxProfileAgeFrames = 720;
        private const int LeaseProbeSamplesRequired = 4;
        private const long BuildCooldownFrames = 12;
'@ @'
        private const long HardMaxProfileAgeFrames = 720;
        private const long HardRefreshGraceFrames = 120;
        private const int HardRefreshSampleMask = 7; // 1/8 while stale-while-revalidate is active.
        private const int LeaseProbeSamplesRequired = 4;
        private const long BuildCooldownFrames = 12;
'@ 'ReachProfile V20 hard-refresh grace constants'

$v17 = Replace-OrThrow $v17 @'
        private static long forcedRefreshCalls;
        private static long uniqueForcedRefreshes;
        private static long forcedRefreshCoalesced;
'@ @'
        private static long forcedRefreshCalls;
        private static long uniqueForcedRefreshes;
        private static long forcedRefreshCoalesced;
        private static long hardRefreshGraceQueries;
        private static long hardRefreshGraceShadowSamples;
        private static long hardRefreshGraceAuthoritative;
        private static long hardRefreshGraceExpired;
'@ 'ReachProfile V20 grace counters'

$v17 = Replace-OrThrow $v17 @'
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
'@ @'
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
'@ 'ReachProfile V20 bounded hard-refresh stale-while-revalidate branch'

$v17 = Replace-OrThrow $v17 @'
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
'@ @'
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
'@ 'ReachProfile V20 accelerated shadow cadence during hard-refresh grace'

$v17 = Replace-OrThrow $v17 @'
            Interlocked.Exchange(ref __state.Slot.ForcedRefreshPending, 0);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
'@ @'
            Interlocked.Exchange(ref __state.Slot.ForcedRefreshPending, 0);
            Interlocked.Exchange(ref __state.Slot.ForcedRefreshStartFrame, 0L);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
'@ 'ReachProfile V20 mismatch grace reset'

$v17 = Replace-OrThrow $v17 @'
                Interlocked.Exchange(ref slot.ForcedRefreshPending, 0);
                slot.CaptureState = null;
                Volatile.Write(ref slot.Published, profile);
'@ @'
                Interlocked.Exchange(ref slot.ForcedRefreshPending, 0);
                Interlocked.Exchange(ref slot.ForcedRefreshStartFrame, 0L);
                slot.CaptureState = null;
                Volatile.Write(ref slot.Published, profile);
'@ 'ReachProfile V20 publish grace reset'

$v17 = Replace-OrThrow $v17 @'
            internal long LeaseUntilFrame;
            internal int ForcedRefreshPending;
            internal ProfileCaptureState CaptureState;
'@ @'
            internal long LeaseUntilFrame;
            internal int ForcedRefreshPending;
            internal long ForcedRefreshStartFrame;
            internal ProfileCaptureState CaptureState;
'@ 'ReachProfile V20 slot refresh-start field'

$v17 = Replace-OrThrow $v17 @'
                ", forcedRefreshCoalesced=" + Interlocked.Read(ref forcedRefreshCoalesced) +
                ", rollingMode=" + reachFuseMode +
'@ @'
                ", forcedRefreshCoalesced=" + Interlocked.Read(ref forcedRefreshCoalesced) +
                ", hardRefreshGraceQueries=" + Interlocked.Read(ref hardRefreshGraceQueries) +
                ", hardRefreshGraceShadow=" + Interlocked.Read(ref hardRefreshGraceShadowSamples) +
                ", hardRefreshGraceAuthoritative=" + Interlocked.Read(ref hardRefreshGraceAuthoritative) +
                ", hardRefreshGraceExpired=" + Interlocked.Read(ref hardRefreshGraceExpired) +
                ", rollingMode=" + reachFuseMode +
'@ 'ReachProfile V20 summary grace counters'

$v17 = Replace-OrThrow $v17 @'
                ", hardMaxProfileAgeFrames=" + HardMaxProfileAgeFrames +
                ", leaseProbeRequired=" + LeaseProbeSamplesRequired +
'@ @'
                ", hardMaxProfileAgeFrames=" + HardMaxProfileAgeFrames +
                ", hardRefreshGraceFrames=" + HardRefreshGraceFrames +
                ", hardRefreshSampleEvery=8" +
                ", leaseProbeRequired=" + LeaseProbeSamplesRequired +
'@ 'ReachProfile V20 summary grace policy'

$v17 = Replace-OrThrow $v17 '            return "Aggressive reachability profile V0.4.19 sliced-profile/local-first/lease: compatibilityReady=" + compatibilityReady +' '            return "Aggressive reachability profile V0.4.20 sliced-profile/bounded-refresh-grace: compatibilityReady=" + compatibilityReady +' 'ReachProfile V20 summary label'
$v17 = Replace-OrThrow $v17 '[RimMT] parallel.reachProfile V0.4.19 installed: topology and per-pawn Region.Allows profile capture are both main-thread budget-sliced; 4-probe lease and local-first fuse remain authoritative safeguards.' '[RimMT] parallel.reachProfile V0.4.20 installed: V19 sliced capture retained; hard refresh uses a bounded 120-frame stale-while-revalidate grace with 4-probe lease recovery and 1/8 shadow validation; local-first fuse remains active.' 'ReachProfile V20 install log'
$v17 = Replace-OrThrow $v17 'Topology and Region.Allows profile arrays are captured incrementally on the main thread; workers consume primitive immutable arrays only.' 'Topology and Region.Allows profile arrays are captured incrementally on the main thread; workers consume primitive immutable arrays only. Hard-refresh stale authority is bounded to 120 frames, generation-stable only, lease-gated, and shadowed at 1/8; Vanilla remains authoritative after grace expiry or any mismatch.' 'ReachProfile V20 safety summary text'

Set-Content $v17Path $v17 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'ReachProfile=V0.4.19 sliced topology/profile capture + 4-probe lease + local-first fuse;' 'ReachProfile=V0.4.20 sliced capture + bounded hard-refresh grace + local-first fuse;' 'diagnostics V20 ReachProfile policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V20 transforms: bounded stale-while-revalidate hard refresh; 1/8 grace shadow cadence; no S4 or worker-policy changes.'
