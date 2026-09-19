$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T32-B anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T32-B Lazy Validator Fingerprint
# Starts from verified T32-A.
# Goal: remove store-time Thing.IsForbidden(pawn) from T21 validator-negative entries.
#
# Safety:
# - first negative store captures only cheap Thing facts
# - first reuse of an unprimed entry always runs the live validator
# - only if that live result is still false do we capture current IsForbidden and mark Primed
# - positive on lazy-prime removes the stale entry; it does not quarantine because forbidden
#   state between the original store and first reuse was intentionally not captured
# - only Primed entries can reach the existing T21 warmup/parity/authoritative path
# - cheap-fact or forbidden mismatch fails open to live
# - no WorkGiver/mod special case; no new Harmony target

$corePath='RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$core=Get-Content $corePath -Raw

$core=Replace-OrThrow $core @'
        private static long validatorFingerprintBypass;
        private static long validatorCapacityBypass;
'@ @'
        private static long validatorFingerprintBypass;
        private static long validatorLazyStores;
        private static long validatorLazyPrimeAttempts;
        private static long validatorLazyPrimeSuccess;
        private static long validatorLazyPrimePositive;
        private static long validatorLazyPrimeForbiddenReads;
        private static long validatorLazyPrimeForbiddenFailures;
        private static long validatorCapacityBypass;
'@ 'lazy fingerprint counters'

$core=Replace-OrThrow $core @'
            if (!entry.Fingerprint.Matches(context.Pawn, thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                return true;
            }

            Interlocked.Increment(ref validatorMemoCandidates);
            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
'@ @'
            if (!entry.Fingerprint.CheapMatches(thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                return true;
            }

            // T32-B: an entry is never authoritative on its first reuse. We did not read
            // Forbidden at store time, so the current live validator must establish a fresh
            // safe baseline before the entry can join the existing T21 parity/trust path.
            if (!entry.Fingerprint.ForbiddenKnown)
            {
                Interlocked.Increment(ref validatorLazyPrimeAttempts);
                __state = ValidatorCallState.LazyPrime(context, key, scanner, thing);
                return true;
            }

            if (!entry.Fingerprint.ForbiddenMatches(context.Pawn, thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                return true;
            }

            Interlocked.Increment(ref validatorMemoCandidates);
            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
'@ 'lazy first-reuse gate'

$core=Replace-OrThrow $core @'
            if (__state.Verify && __state.Trust != null)
            {
                if (!__result)
                {
                    __state.Trust.ValidatedMatches++;
                    Interlocked.Increment(ref validatorVerifyMatches);
                }
                else
                {
                    __state.Trust.Quarantined = true;
                    __state.Trust.ValidatedMatches = 0;
                    context.ValidatorNegatives.Clear();
                    Interlocked.Increment(ref validatorMismatches);
                    Interlocked.Increment(ref validatorQuarantines);
                }
                return;
            }

            if (__result)
            {
                Interlocked.Increment(ref validatorPositiveLive);
                return;
            }
'@ @'
            if (__state.LazyPrime)
            {
                if (__result)
                {
                    // The original negative may have become positive because of state we
                    // intentionally did not capture at store time. Remove and fail open;
                    // this is not a parity violation and must not quarantine the scanner.
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimePositive);
                    Interlocked.Increment(ref validatorPositiveLive);
                    return;
                }

                ValidatorNegativeEntry existing;
                if (!context.ValidatorNegatives.TryGetValue(__state.Key, out existing) ||
                    __state.Thing == null ||
                    !existing.Fingerprint.CheapMatches(__state.Thing))
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorFingerprintBypass);
                    return;
                }

                ThingFingerprint primed;
                Interlocked.Increment(ref validatorLazyPrimeForbiddenReads);
                if (!ThingFingerprint.TryCapturePrimed(context.Pawn, __state.Thing, out primed))
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimeForbiddenFailures);
                    return;
                }

                context.ValidatorNegatives[__state.Key] = new ValidatorNegativeEntry(primed);
                Interlocked.Increment(ref validatorLazyPrimeSuccess);
                return;
            }

            if (__state.Verify && __state.Trust != null)
            {
                if (!__result)
                {
                    __state.Trust.ValidatedMatches++;
                    Interlocked.Increment(ref validatorVerifyMatches);
                }
                else
                {
                    __state.Trust.Quarantined = true;
                    __state.Trust.ValidatedMatches = 0;
                    context.ValidatorNegatives.Clear();
                    Interlocked.Increment(ref validatorMismatches);
                    Interlocked.Increment(ref validatorQuarantines);
                }
                return;
            }

            if (__result)
            {
                Interlocked.Increment(ref validatorPositiveLive);
                return;
            }
'@ 'lazy prime postfix'

$core=Replace-OrThrow $core @'
                context.ValidatorNegatives.Add(__state.Key,
                    new ValidatorNegativeEntry(ThingFingerprint.Capture(context.Pawn, __state.Thing)));
                Interlocked.Increment(ref validatorNegativeStores);
'@ @'
                context.ValidatorNegatives.Add(__state.Key,
                    new ValidatorNegativeEntry(ThingFingerprint.CaptureCheap(__state.Thing)));
                Interlocked.Increment(ref validatorNegativeStores);
                Interlocked.Increment(ref validatorLazyStores);
'@ 'cheap-only store'

$core=Replace-OrThrow $core @'
                ", fingerprintBypass=" + Interlocked.Read(ref validatorFingerprintBypass) +
                ", capBypass=" + Interlocked.Read(ref validatorCapacityBypass) +
'@ @'
                ", fingerprintBypass=" + Interlocked.Read(ref validatorFingerprintBypass) +
                ", lazyFingerprint[stores=" + Interlocked.Read(ref validatorLazyStores) +
                ", primeAttempts=" + Interlocked.Read(ref validatorLazyPrimeAttempts) +
                ", primeSuccess=" + Interlocked.Read(ref validatorLazyPrimeSuccess) +
                ", primePositive=" + Interlocked.Read(ref validatorLazyPrimePositive) +
                ", forbiddenReads=" + Interlocked.Read(ref validatorLazyPrimeForbiddenReads) +
                ", forbiddenReadFailures=" + Interlocked.Read(ref validatorLazyPrimeForbiddenFailures) +
                ", netStoreReadAvoided=" + Math.Max(0L,
                    Interlocked.Read(ref validatorLazyStores) -
                    Interlocked.Read(ref validatorLazyPrimeForbiddenReads)) + "]" +
                ", capBypass=" + Interlocked.Read(ref validatorCapacityBypass) +
'@ 'lazy fingerprint summary'

$core=Replace-OrThrow $core @'
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;
'@ @'
            internal bool Store;
            internal bool Verify;
            internal bool LazyPrime;
            internal bool AuthoritativeHit;
'@ 'lazy state flag'

$core=Replace-OrThrow $core @'
            internal static ValidatorCallState ForVerify(TransactionContext context, ValidatorKey key,
                ValidatorTrustState trust)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Trust = trust, Verify = true
                };
            }
'@ @'
            internal static ValidatorCallState ForVerify(TransactionContext context, ValidatorKey key,
                ValidatorTrustState trust)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Trust = trust, Verify = true
                };
            }

            internal static ValidatorCallState LazyPrime(TransactionContext context, ValidatorKey key,
                WorkGiver_Scanner scanner, Thing thing)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Scanner = scanner, Thing = thing, LazyPrime = true
                };
            }
'@ 'lazy state factory'

$fpPattern='(?s)        internal struct ThingFingerprint\s*\{.*?\n        \}\n\n        internal struct TargetFingerprint'
if(-not [regex]::IsMatch($core,$fpPattern)){ throw 'T32-B anchor missing: ThingFingerprint block' }
$newFp=@'
        internal struct ThingFingerprint
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;
            internal readonly int HitPoints;
            internal readonly bool ForbiddenKnown;
            internal readonly bool Forbidden;

            internal ThingFingerprint(Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount,
                int hitPoints, bool forbiddenKnown, bool forbidden)
            {
                MapHeld = mapHeld; PositionHeld = positionHeld; Spawned = spawned;
                StackCount = stackCount; HitPoints = hitPoints;
                ForbiddenKnown = forbiddenKnown; Forbidden = forbidden;
            }

            // First negative store: cheap facts only. No IsForbidden call here.
            internal static ThingFingerprint CaptureCheap(Thing thing)
            {
                if (thing == null)
                    return new ThingFingerprint(null, IntVec3.Invalid, false, 0, 0, false, false);

                return new ThingFingerprint(thing.MapHeld, thing.PositionHeld, thing.Spawned,
                    thing.stackCount, thing.HitPoints, false, false);
            }

            // Called only after an unprimed entry is encountered again and the live validator
            // has just returned false. This establishes the forbidden baseline for future reuse.
            internal static bool TryCapturePrimed(Pawn pawn, Thing thing, out ThingFingerprint fingerprint)
            {
                fingerprint = default(ThingFingerprint);
                if (thing == null) return false;

                try
                {
                    bool forbidden = pawn != null && thing.IsForbidden(pawn);
                    fingerprint = new ThingFingerprint(
                        thing.MapHeld, thing.PositionHeld, thing.Spawned,
                        thing.stackCount, thing.HitPoints, true, forbidden);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            internal bool CheapMatches(Thing thing)
            {
                return thing != null &&
                    ReferenceEquals(MapHeld, thing.MapHeld) &&
                    PositionHeld == thing.PositionHeld &&
                    Spawned == thing.Spawned &&
                    StackCount == thing.stackCount &&
                    HitPoints == thing.HitPoints;
            }

            internal bool ForbiddenMatches(Pawn pawn, Thing thing)
            {
                if (!ForbiddenKnown || !CheapMatches(thing))
                    return false;

                try
                {
                    return Forbidden == (pawn != null && thing.IsForbidden(pawn));
                }
                catch
                {
                    return false;
                }
            }
        }

        internal struct TargetFingerprint
'@
$core=[regex]::Replace($core,$fpPattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $newFp },1)

Set-Content $corePath $core -Encoding UTF8

# Version/report metadata. T32-A Reservation Transaction remains active and unchanged.
$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t32a-reservation-transaction";' 'internal const string Version = "0.9.3-t32b-lazy-validator-fingerprint";' 'bootstrap version'
$boot=$boot.Replace('[RimMT] V0.9.3-T32A Reservation Transaction initialized.',
                    '[RimMT] V0.9.3-T32B Lazy Validator Fingerprint initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T32A Reservation Transaction','V0.9.3-T32B Lazy Validator Fingerprint')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T32A Reservation Transaction','V0.9.3-T32B Lazy Validator Fingerprint')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T32-B for RimWorld 1.5. T32-A Reservation Transaction remains unchanged. T32-B reduces T21 validator transaction self-overhead by making the forbidden component of negative Thing fingerprints lazy: first stores capture only cheap Thing facts; first reuse always runs the live validator; only a repeated live false primes the forbidden baseline. Only primed entries can enter the existing T21 warmup/parity/authoritative false path. No WorkGiver-specific rule, positive validator replay, Job, reservation, priority or cross-package gameplay result is added.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.10.0";' 'internal const string Version = "0.11.0";' 'Diagnostics v0.11 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.10','RimMT Diagnostics v0.11')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T32-B. v0.11 surfaces the existing T32-A Reservation Transaction plus T21 lazy-validator-fingerprint counters through the normal production summaries. T30/T31 experimental profilers remain absent.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T32-B Lazy Validator Fingerprint + Diagnostics v0.11.'
