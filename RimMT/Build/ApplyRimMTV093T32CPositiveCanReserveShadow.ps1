$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T32-C anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T32-C Positive CanReserve Shadow Validation
# Applied on top of verified T32-B.1 / T32-A.
#
# Measurement-only proof layer:
# - first live positive for an exact T32-A ReserveKey stores only an observation entry
# - repeated positive-shadow candidates ALWAYS execute the live ReservationManager.CanReserve chain
# - finalizer compares the final live result; T32-C never writes __result=true and never skips original
# - exact key, target fingerprint and ReservationManager mutation epoch are required
# - any reservation mutation clears both the T32-A negative memo and T32-C positive shadows
# - target fingerprint changes discard the positive shadow and fall back live
# - positive->false shadow flips are measured, then the live false result flows through the
#   existing T32-A negative-store path so baseline false optimization behavior is preserved
# - chain authority-safe/unsafe candidates are counted separately for future proof decisions
# - no positive replay, no reservation mutation, no Job/state commit, no cross-package result

$t32Path='RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs'
$t32=Get-Content $t32Path -Raw

$t32=Replace-OrThrow $t32 @'
        private const int Capacity = 8192;
        private const int WarmupMatches = 16;
'@ @'
        private const int Capacity = 8192;
        private const int PositiveShadowCapacity = 8192;
        private const int WarmupMatches = 16;
'@ 'positive shadow capacity'

$t32=Replace-OrThrow $t32 @'
        private static long positiveLive;
        private static long negativeLive;
        private static long fingerprintBypass;
'@ @'
        private static long positiveLive;
        private static long negativeLive;

        private static long positiveShadowStores;
        private static long positiveShadowCandidates;
        private static long positiveShadowMatches;
        private static long positiveShadowMismatches;
        private static long positiveShadowAuthorityEligible;
        private static long positiveShadowAuthorityUnsafe;
        private static long positiveShadowFingerprintBypass;
        private static long positiveShadowCapacityBypass;
        private static long positiveShadowMutationClears;
        private static long positiveShadowEntriesCleared;

        private static long fingerprintBypass;
'@ 'positive shadow counters'

$t32=Replace-OrThrow $t32 @'
            NegativeEntry entry;
            if (!context.Negatives.TryGetValue(key, out entry))
            {
                __state = CallState.ForStore(context, key, target);
                return true;
            }

            if (entry.MutationEpoch != context.MutationEpoch ||
'@ @'
            NegativeEntry entry;
            if (!context.Negatives.TryGetValue(key, out entry))
            {
                PositiveEntry positiveEntry;
                if (context.PositiveShadows.TryGetValue(key, out positiveEntry))
                {
                    if (positiveEntry.MutationEpoch != context.MutationEpoch ||
                        !positiveEntry.Fingerprint.Matches(target))
                    {
                        context.PositiveShadows.Remove(key);
                        Interlocked.Increment(ref positiveShadowFingerprintBypass);
                        __state = CallState.ForStore(context, key, target);
                        return true;
                    }

                    Interlocked.Increment(ref positiveShadowCandidates);
                    if (chainAuthoritativeSafe && !runtimeQuarantined)
                        Interlocked.Increment(ref positiveShadowAuthorityEligible);
                    else
                        Interlocked.Increment(ref positiveShadowAuthorityUnsafe);

                    // Shadow only. The original + all foreign prefixes/postfixes/finalizers
                    // remain live; no positive result is ever replayed in T32-C.
                    __state = CallState.ForPositiveShadow(context, key, target);
                    return true;
                }

                __state = CallState.ForStore(context, key, target);
                return true;
            }

            if (entry.MutationEpoch != context.MutationEpoch ||
'@ 'positive shadow prefix lookup'

$t32=Replace-OrThrow $t32 @'
            if (__state.AuthoritativeHit)
                return __exception;

            if (__state.Verify)
'@ @'
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

            if (__state.Verify)
'@ 'positive shadow finalizer compare'

$t32=Replace-OrThrow $t32 @'
            if (__result)
            {
                Interlocked.Increment(ref positiveLive);
                return __exception;
            }

            Interlocked.Increment(ref negativeLive);
'@ @'
            if (__result)
            {
                Interlocked.Increment(ref positiveLive);

                if (context.PositiveShadows.Count >= PositiveShadowCapacity)
                {
                    Interlocked.Increment(ref positiveShadowCapacityBypass);
                    return __exception;
                }

                if (!context.PositiveShadows.ContainsKey(__state.Key))
                {
                    context.PositiveShadows.Add(
                        __state.Key,
                        new PositiveEntry(
                            context.MutationEpoch,
                            TargetFingerprint.Capture(__state.Target)));
                    Interlocked.Increment(ref positiveShadowStores);
                }
                return __exception;
            }

            Interlocked.Increment(ref negativeLive);
'@ 'positive shadow store'

$t32=Replace-OrThrow $t32 @'
            context.MutationEpoch++;
            context.Negatives.Clear();
            context.ValidatedMatches = 0;
            Interlocked.Increment(ref mutationInvalidations);
'@ @'
            context.MutationEpoch++;
            context.Negatives.Clear();

            int positiveCount = context.PositiveShadows.Count;
            if (positiveCount > 0)
            {
                context.PositiveShadows.Clear();
                Interlocked.Increment(ref positiveShadowMutationClears);
                Interlocked.Add(ref positiveShadowEntriesCleared, positiveCount);
            }

            context.ValidatedMatches = 0;
            Interlocked.Increment(ref mutationInvalidations);
'@ 'positive mutation invalidation'

$t32=Replace-OrThrow $t32 @'
                ", live[positive/negative]=" +
                Interlocked.Read(ref positiveLive) + "/" +
                Interlocked.Read(ref negativeLive) +
                ", invalidation[reservationMutation/fingerprint]=" +
'@ @'
                ", live[positive/negative]=" +
                Interlocked.Read(ref positiveLive) + "/" +
                Interlocked.Read(ref negativeLive) +
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
                ", invalidation[reservationMutation/fingerprint]=" +
'@ 'positive shadow summary'

$t32=Replace-OrThrow $t32 @'
                ". False-only exact CanReserve memo; reservation mutations clear the package memo; " +
                "foreign prefixes/postfixes remain in-chain; foreign transpiler/finalizer => live fallback; " +
                "no reservation/Job/priority/reachability/cross-package result is created or cached.";
'@ @'
                ". False-only exact CanReserve memo remains the only authoritative reservation replay; " +
                "T32-C positive shadow always executes live and only measures exact positive stability; " +
                "reservation mutations clear negative memo + positive shadows; " +
                "foreign prefixes/postfixes remain in-chain; foreign transpiler/finalizer => negative live fallback; " +
                "no positive replay/reservation/Job/priority/reachability/cross-package result is created or cached.";
'@ 'positive shadow summary wording'

$t32=Replace-OrThrow $t32 @'
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;
'@ @'
            internal bool Store;
            internal bool Verify;
            internal bool PositiveShadow;
            internal bool AuthoritativeHit;
'@ 'positive shadow call state field'

$t32=Replace-OrThrow $t32 @'
            internal static CallState ForVerify(
                PackageContext context,
                ReserveKey key,
                LocalTargetInfo target)
            {
                return new CallState
                {
                    Context = context,
                    Key = key,
                    Target = target,
                    Verify = true
                };
            }

            internal static CallState ForAuthoritative(PackageContext context)
'@ @'
            internal static CallState ForVerify(
                PackageContext context,
                ReserveKey key,
                LocalTargetInfo target)
            {
                return new CallState
                {
                    Context = context,
                    Key = key,
                    Target = target,
                    Verify = true
                };
            }

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
'@ 'positive shadow state factory'

$t32=Replace-OrThrow $t32 @'
            internal readonly Dictionary<ReserveKey, NegativeEntry> Negatives =
                new Dictionary<ReserveKey, NegativeEntry>();

            internal long MutationEpoch;
'@ @'
            internal readonly Dictionary<ReserveKey, NegativeEntry> Negatives =
                new Dictionary<ReserveKey, NegativeEntry>();
            internal readonly Dictionary<ReserveKey, PositiveEntry> PositiveShadows =
                new Dictionary<ReserveKey, PositiveEntry>();

            internal long MutationEpoch;
'@ 'positive shadow package dictionary'

$t32=Replace-OrThrow $t32 @'
        internal struct NegativeEntry
        {
            internal readonly long MutationEpoch;
            internal readonly TargetFingerprint Fingerprint;

            internal NegativeEntry(long mutationEpoch, TargetFingerprint fingerprint)
            {
                MutationEpoch = mutationEpoch;
                Fingerprint = fingerprint;
            }
        }

        internal struct ReserveKey : IEquatable<ReserveKey>
'@ @'
        internal struct NegativeEntry
        {
            internal readonly long MutationEpoch;
            internal readonly TargetFingerprint Fingerprint;

            internal NegativeEntry(long mutationEpoch, TargetFingerprint fingerprint)
            {
                MutationEpoch = mutationEpoch;
                Fingerprint = fingerprint;
            }
        }

        internal struct PositiveEntry
        {
            internal readonly long MutationEpoch;
            internal readonly TargetFingerprint Fingerprint;

            internal PositiveEntry(long mutationEpoch, TargetFingerprint fingerprint)
            {
                MutationEpoch = mutationEpoch;
                Fingerprint = fingerprint;
            }
        }

        internal struct ReserveKey : IEquatable<ReserveKey>
'@ 'positive entry struct'

# Version / visible labels.
$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t32b1-adaptive-forbidden-fingerprint";' 'internal const string Version = "0.9.3-t32c-positive-canreserve-shadow";' 'bootstrap version'
$boot=$boot.Replace('[RimMT] V0.9.3-T32B.1 Adaptive Forbidden Fingerprint initialized.',
                    '[RimMT] V0.9.3-T32C Positive CanReserve Shadow initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T32B.1 Adaptive Forbidden Fingerprint','V0.9.3-T32C Positive CanReserve Shadow')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T32B.1 Adaptive Forbidden Fingerprint','V0.9.3-T32C Positive CanReserve Shadow')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T32-C for RimWorld 1.5. Verified T32-B.1 adaptive Forbidden fingerprints and T32-A false-only CanReserve replay remain unchanged. T32-C adds a bounded package-local measurement-only shadow for exact live-positive ReservationManager.CanReserve queries. Repeated positive candidates always execute the original live chain; the final result is compared under the same exact key, target fingerprint and reservation-mutation epoch. Reservation mutations clear shadows. Positive-to-false flips are measured and then flow into the existing T32-A negative-store path. No positive result is replayed, no reservation/Job/priority/reachability/cross-package result is created, and zero-wait/FullParallel HARD_OFF remain unchanged.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.12.0";' 'internal const string Version = "0.13.0";' 'Diagnostics v0.13 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.12','RimMT Diagnostics v0.13')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T32-C. v0.13 surfaces T32-C positive CanReserve shadow stores, exact-repeat candidates, live matches/mismatches, authority-safe evidence, fingerprint bypass and mutation invalidation alongside retained T32-B.1/T32-A counters. Positive replay remains OFF.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Set-Content $t32Path $t32 -Encoding UTF8
Write-Host 'Applied RimMT T32-C Positive CanReserve Shadow + Diagnostics v0.13.'
