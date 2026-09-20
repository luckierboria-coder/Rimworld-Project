$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T32-C.1 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T32-C.1 Positive CanReserve Authoritative Replay
# Applied on top of verified T32-C shadow validation.
#
# Conservative positive authority:
# - an exact positive key MUST first be observed live and stored in PositiveShadows
# - repeated exact positives remain live for an independent 32-match package warmup
# - after warmup, 1/64 repeated positive candidates remain live parity samples
# - all other trusted candidates replay true without running ReservationManager.CanReserve original
# - positive trust is independent from T32-A negative trust
# - any positive parity mismatch clears all positive entries in the package and permanently
#   quarantines positive replay for the runtime; negative replay remains independently governed
# - negative runtime quarantine or chain authority loss also forces positive live fallback
# - reservation mutation clears both result-class entries and resets package positive trust
# - exact ReserveKey + mutation epoch + target fingerprint are always required
# - no Job/state/reservation mutation, no reachability/priority/cross-package result, no workers/waits

$t32Path='RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs'
$t32=Get-Content $t32Path -Raw

$t32=Replace-OrThrow $t32 @'
        private const int Capacity = 8192;
        private const int PositiveShadowCapacity = 8192;
        private const int WarmupMatches = 16;
        private const int VerifyMask = 63; // 1/64 after warmup.
'@ @'
        private const int Capacity = 8192;
        private const int PositiveShadowCapacity = 8192;
        private const int WarmupMatches = 16;
        private const int VerifyMask = 63; // 1/64 after warmup.
        private const int PositiveWarmupMatches = 32;
        private const int PositiveVerifyMask = 63; // 1/64 after positive warmup.
'@ 'positive trust constants'

$t32=Replace-OrThrow $t32 @'
        private static bool chainAuthoritativeSafe;
        private static bool runtimeQuarantined;
        private static int installFailures;
'@ @'
        private static bool chainAuthoritativeSafe;
        private static bool runtimeQuarantined;
        private static bool positiveRuntimeQuarantined;
        private static int installFailures;
'@ 'positive runtime quarantine'

$t32=Replace-OrThrow $t32 @'
        private static long positiveShadowMutationClears;
        private static long positiveShadowEntriesCleared;

        private static long fingerprintBypass;
'@ @'
        private static long positiveShadowMutationClears;
        private static long positiveShadowEntriesCleared;

        private static long positiveVerifyRuns;
        private static long positiveVerifyMatches;
        private static long positiveVerifyMismatches;
        private static long positiveAuthoritativeHits;
        private static long positiveQuarantines;
        private static long positiveAuthorityBypass;

        private static long fingerprintBypass;
'@ 'positive replay counters'

$t32=Replace-OrThrow $t32 @'
                    Interlocked.Increment(ref positiveShadowCandidates);
                    if (chainAuthoritativeSafe && !runtimeQuarantined)
                        Interlocked.Increment(ref positiveShadowAuthorityEligible);
                    else
                        Interlocked.Increment(ref positiveShadowAuthorityUnsafe);

                    // Shadow only. The original + all foreign prefixes/postfixes/finalizers
                    // remain live; no positive result is ever replayed in T32-C.
                    __state = CallState.ForPositiveShadow(context, key, target);
                    return true;
'@ @'
                    Interlocked.Increment(ref positiveShadowCandidates);

                    bool positiveAuthoritySafe =
                        chainAuthoritativeSafe &&
                        !runtimeQuarantined &&
                        !positiveRuntimeQuarantined;

                    if (chainAuthoritativeSafe)
                        Interlocked.Increment(ref positiveShadowAuthorityEligible);
                    else
                        Interlocked.Increment(ref positiveShadowAuthorityUnsafe);

                    if (!positiveAuthoritySafe)
                    {
                        Interlocked.Increment(ref positiveAuthorityBypass);
                        __state = CallState.ForPositiveShadow(
                            context, key, target, false);
                        return true;
                    }

                    context.PositiveHitSerial++;
                    bool positiveVerify =
                        context.PositiveValidatedMatches < PositiveWarmupMatches ||
                        (context.PositiveHitSerial & PositiveVerifyMask) == 0;

                    if (positiveVerify)
                    {
                        __state = CallState.ForPositiveShadow(
                            context, key, target, true);
                        Interlocked.Increment(ref positiveVerifyRuns);
                        return true;
                    }

                    // T32-C.1 positive authority. The exact key was previously observed true
                    // in this package, the target fingerprint and mutation epoch still match,
                    // the chain is authority-safe, and independent positive trust has warmed.
                    __result = true;
                    __state = CallState.ForPositiveAuthoritative(context);
                    Interlocked.Increment(ref positiveAuthoritativeHits);
                    return false;
'@ 'positive replay prefix'

$t32=Replace-OrThrow $t32 @'
            if (__state.AuthoritativeHit)
                return __exception;

            if (__state.PositiveShadow)
            {
                if (__result)
                {
                    Interlocked.Increment(ref positiveShadowMatches);
                    return __exception;
                }

                // This was a live call and it flipped positive -> false. Record the proof
                // failure, remove the positive observation, then deliberately fall through
                // to the ordinary Store=false path below. That preserves T32-A's baseline
                // behavior: this live false may become a normal negative memo entry.
                context.PositiveShadows.Remove(__state.Key);
                Interlocked.Increment(ref positiveShadowMismatches);
            }
'@ @'
            if (__state.AuthoritativeHit || __state.PositiveAuthoritativeHit)
                return __exception;

            if (__state.PositiveShadow)
            {
                if (__result)
                {
                    Interlocked.Increment(ref positiveShadowMatches);
                    if (__state.PositiveTrustSample)
                    {
                        context.PositiveValidatedMatches++;
                        Interlocked.Increment(ref positiveVerifyMatches);
                    }
                    return __exception;
                }

                // A repeated exact positive changed to false under a fully live call.
                // Always remove the stale entry and preserve the live false so it can
                // flow into T32-A's ordinary negative-store path below.
                context.PositiveShadows.Remove(__state.Key);
                Interlocked.Increment(ref positiveShadowMismatches);

                // Only authority-eligible parity samples can quarantine positive replay.
                // Authority-unsafe/bypass observations remain measurement-only.
                if (__state.PositiveTrustSample)
                {
                    int positiveCount = context.PositiveShadows.Count;
                    context.PositiveShadows.Clear();
                    context.PositiveValidatedMatches = 0;
                    context.PositiveHitSerial = 0;
                    positiveRuntimeQuarantined = true;
                    Interlocked.Increment(ref positiveVerifyMismatches);
                    Interlocked.Increment(ref positiveQuarantines);
                    if (positiveCount > 0)
                        Interlocked.Add(ref positiveShadowEntriesCleared, positiveCount);
                }
            }
'@ 'positive parity finalizer'

$t32=Replace-OrThrow $t32 @'
            int positiveCount = context.PositiveShadows.Count;
            if (positiveCount > 0)
            {
                context.PositiveShadows.Clear();
                Interlocked.Increment(ref positiveShadowMutationClears);
                Interlocked.Add(ref positiveShadowEntriesCleared, positiveCount);
            }

            context.ValidatedMatches = 0;
'@ @'
            int positiveCount = context.PositiveShadows.Count;
            if (positiveCount > 0)
            {
                context.PositiveShadows.Clear();
                Interlocked.Increment(ref positiveShadowMutationClears);
                Interlocked.Add(ref positiveShadowEntriesCleared, positiveCount);
            }

            context.PositiveValidatedMatches = 0;
            context.PositiveHitSerial = 0;
            context.ValidatedMatches = 0;
'@ 'positive mutation trust reset'

$t32=Replace-OrThrow $t32 @'
            if (wasSafe && !nowSafe)
            {
                chainAuthoritativeSafe = false;
                PackageContext context = current;
                if (context != null)
                    context.Negatives.Clear();
                Interlocked.Increment(ref lateUnsafeTransitions);
            }
'@ @'
            if (wasSafe && !nowSafe)
            {
                chainAuthoritativeSafe = false;
                PackageContext context = current;
                if (context != null)
                {
                    context.Negatives.Clear();
                    context.PositiveShadows.Clear();
                    context.ValidatedMatches = 0;
                    context.PositiveValidatedMatches = 0;
                    context.PositiveHitSerial = 0;
                }
                Interlocked.Increment(ref lateUnsafeTransitions);
            }
'@ 'late authority loss positive invalidation'

$t32=Replace-OrThrow $t32 @'
                ", positiveShadow[stores/candidates/matches/mismatches]=" +
                Interlocked.Read(ref positiveShadowStores) + "/" +
                Interlocked.Read(ref positiveShadowCandidates) + "/" +
                Interlocked.Read(ref positiveShadowMatches) + "/" +
                Interlocked.Read(ref positiveShadowMismatches) +
                ", authorityEligible/unsafe=" +
                Interlocked.Read(ref positiveShadowAuthorityEligible) + "/" +
                Interlocked.Read(ref positiveShadowAuthorityUnsafe) +
                ", fingerprintBypass/capacityBypass=" +
                Interlocked.Read(ref positiveShadowFingerprintBypass) + "/" +
                Interlocked.Read(ref positiveShadowCapacityBypass) +
                ", mutationClears/entriesCleared=" +
                Interlocked.Read(ref positiveShadowMutationClears) + "/" +
                Interlocked.Read(ref positiveShadowEntriesCleared) + "]" +
'@ @'
                ", positiveShadow[stores/candidates/liveMatches/liveMismatches]=" +
                Interlocked.Read(ref positiveShadowStores) + "/" +
                Interlocked.Read(ref positiveShadowCandidates) + "/" +
                Interlocked.Read(ref positiveShadowMatches) + "/" +
                Interlocked.Read(ref positiveShadowMismatches) +
                ", authorityEligible/unsafe=" +
                Interlocked.Read(ref positiveShadowAuthorityEligible) + "/" +
                Interlocked.Read(ref positiveShadowAuthorityUnsafe) +
                ", fingerprintBypass/capacityBypass=" +
                Interlocked.Read(ref positiveShadowFingerprintBypass) + "/" +
                Interlocked.Read(ref positiveShadowCapacityBypass) +
                ", mutationClears/entriesCleared=" +
                Interlocked.Read(ref positiveShadowMutationClears) + "/" +
                Interlocked.Read(ref positiveShadowEntriesCleared) + "]" +
                ", positiveReplay[runtimeQuarantined=" + positiveRuntimeQuarantined +
                ", warmup=" + PositiveWarmupMatches +
                ", verifyEvery=" + (PositiveVerifyMask + 1) +
                ", verify[runs/matches/mismatches/quarantines]=" +
                Interlocked.Read(ref positiveVerifyRuns) + "/" +
                Interlocked.Read(ref positiveVerifyMatches) + "/" +
                Interlocked.Read(ref positiveVerifyMismatches) + "/" +
                Interlocked.Read(ref positiveQuarantines) +
                ", authoritativeHits=" +
                Interlocked.Read(ref positiveAuthoritativeHits) +
                ", authorityBypass=" +
                Interlocked.Read(ref positiveAuthorityBypass) + "]" +
'@ 'positive replay summary counters'

$t32=Replace-OrThrow $t32 @'
                ". False-only exact CanReserve memo remains the only authoritative reservation replay; " +
                "T32-C positive shadow always executes live and only measures exact positive stability; " +
                "reservation mutations clear negative memo + positive shadows; " +
                "foreign prefixes/postfixes remain in-chain; foreign transpiler/finalizer => negative live fallback; " +
                "no positive replay/reservation/Job/priority/reachability/cross-package result is created or cached.";
'@ @'
                ". T32-A false replay and T32-C.1 positive replay use independent trust; " +
                "positive authority requires prior exact live=true observation + stable fingerprint/epoch + " +
                "32 live matches, then keeps 1/64 live parity; any positive parity mismatch quarantines " +
                "positive replay only. Reservation mutations clear both result classes and package trust; " +
                "foreign transpiler/finalizer or authority loss => live fallback; " +
                "no reservation/Job/priority/reachability/cross-package result is created or cached.";
'@ 'positive replay summary wording'

$t32=Replace-OrThrow $t32 @'
            internal bool Store;
            internal bool Verify;
            internal bool PositiveShadow;
            internal bool AuthoritativeHit;
'@ @'
            internal bool Store;
            internal bool Verify;
            internal bool PositiveShadow;
            internal bool PositiveTrustSample;
            internal bool PositiveAuthoritativeHit;
            internal bool AuthoritativeHit;
'@ 'positive replay call state fields'

$t32=Replace-OrThrow $t32 @'
            internal static CallState ForPositiveShadow(
                PackageContext context,
                ReserveKey key,
                LocalTargetInfo target)
            {
                return new CallState
                {
                    Context = context,
                    Key = key,
                    Target = target,
                    Store = true,
                    PositiveShadow = true
                };
            }

            internal static CallState ForAuthoritative(PackageContext context)
'@ @'
            internal static CallState ForPositiveShadow(
                PackageContext context,
                ReserveKey key,
                LocalTargetInfo target,
                bool trustSample)
            {
                return new CallState
                {
                    Context = context,
                    Key = key,
                    Target = target,
                    Store = true,
                    PositiveShadow = true,
                    PositiveTrustSample = trustSample
                };
            }

            internal static CallState ForPositiveAuthoritative(PackageContext context)
            {
                return new CallState
                {
                    Context = context,
                    PositiveAuthoritativeHit = true
                };
            }

            internal static CallState ForAuthoritative(PackageContext context)
'@ 'positive replay state factories'

$t32=Replace-OrThrow $t32 @'
            internal long MutationEpoch;
            internal long HitSerial;
            internal int ValidatedMatches;
'@ @'
            internal long MutationEpoch;
            internal long HitSerial;
            internal int ValidatedMatches;
            internal long PositiveHitSerial;
            internal int PositiveValidatedMatches;
'@ 'positive package trust fields'

# Version / visible labels.
$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t32c-positive-canreserve-shadow";' 'internal const string Version = "0.9.3-t32c1-positive-canreserve-replay";' 'bootstrap version'
$boot=$boot.Replace('[RimMT] V0.9.3-T32C Positive CanReserve Shadow initialized.',
                    '[RimMT] V0.9.3-T32C.1 Positive CanReserve Replay initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T32C Positive CanReserve Shadow','V0.9.3-T32C.1 Positive CanReserve Replay')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T32C Positive CanReserve Shadow','V0.9.3-T32C.1 Positive CanReserve Replay')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T32-C.1 for RimWorld 1.5. Verified T32-C shadow evidence (716,201/716,201 authority-eligible repeat positives matched in the test workload) graduates to conservative package-local positive CanReserve replay. Every exact key is first observed live=true. Independent positive trust requires 32 live repeat matches in the current JobGiver_Work package, then 1/64 candidates remain live parity samples. Any positive parity mismatch clears positive entries and quarantines positive replay for the runtime without altering T32-A negative trust. Reservation mutations and authority loss clear package positive state. T32-B.1 adaptive Forbidden, zero-wait and FullParallel HARD_OFF remain unchanged.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.13.0";' 'internal const string Version = "0.14.0";' 'Diagnostics v0.14 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.13','RimMT Diagnostics v0.14')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T32-C.1. v0.14 surfaces positive CanReserve warmup/parity runs, matches/mismatches, authoritative positive hits, authority bypass and positive runtime quarantine alongside retained T32-C shadow, T32-A negative and T32-B.1 counters.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Set-Content $t32Path $t32 -Encoding UTF8
Write-Host 'Applied RimMT T32-C.1 Positive CanReserve Replay + Diagnostics v0.14.'
