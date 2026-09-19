$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T32-B anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T32-B Lazy Forbidden Fingerprint
# Runs after the verified T32-A build transform.
# It changes only T21 validator negative-entry fingerprint construction:
#   StoreCheap -> first repeat runs live validator -> on live false capture Forbidden -> Primed.
# Existing scanner/method trust, warmup, sampled parity and mismatch quarantine stay intact.

$t20Path='RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw

$t20=Replace-OrThrow $t20 @'
        private static long validatorPositiveLive;

        private static long reachObserved;
'@ @'
        private static long validatorPositiveLive;
        private static long validatorLazyStores;
        private static long validatorLazyPrimeAttempts;
        private static long validatorLazyPrimeSuccess;
        private static long validatorLazyPrimePositive;
        private static long validatorLazyPrimeReadFailures;

        private static long reachObserved;
'@ 'lazy fingerprint counters'

$pattern='(?s)            if \(!entry\.Fingerprint\.Matches\(context\.Pawn, thing\)\)\s*\{\s*context\.ValidatorNegatives\.Remove\(key\);\s*Interlocked\.Increment\(ref validatorFingerprintBypass\);\s*__state = ValidatorCallState\.Store\(context, key, scanner, thing\);\s*return true;\s*\}\s*Interlocked\.Increment\(ref validatorMemoCandidates\);\s*ValidatorTrustState trust = GetTrust\(__originalMethod, scanner\);'
$replacement=@'
            if (!entry.Fingerprint.MatchesCheap(thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.Store(context, key, scanner, thing);
                return true;
            }

            Interlocked.Increment(ref validatorMemoCandidates);

            // T32-B: the first repeat of a cheap-only entry is always live. Only if that
            // live validator is still false do we pay for IsForbidden and promote the entry
            // to a full fingerprint. One-shot entries therefore never call IsForbidden just
            // to populate a memo that is never reused.
            if (!entry.Fingerprint.HasForbidden)
            {
                Interlocked.Increment(ref validatorLazyPrimeAttempts);
                __state = ValidatorCallState.Prime(context, key, scanner, thing);
                return true;
            }

            if (!entry.Fingerprint.MatchesPrimed(context.Pawn, thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.Store(context, key, scanner, thing);
                return true;
            }

            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
'@
$new=[regex]::Replace($t20,$pattern,$replacement,1)
if($new -eq $t20){ throw 'T32-B anchor missing: validator lazy prefix' }
$t20=$new

$t20=Replace-OrThrow $t20 @'
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
'@ @'
            if (__state.Prime)
            {
                ValidatorNegativeEntry existing;
                if (!context.ValidatorNegatives.TryGetValue(__state.Key, out existing))
                    return;

                if (__result)
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorPositiveLive);
                    Interlocked.Increment(ref validatorLazyPrimePositive);
                    return;
                }

                ThingFingerprint primed;
                if (!ThingFingerprint.TryPrime(context.Pawn, __state.Thing, existing.Fingerprint, out primed))
                {
                    Interlocked.Increment(ref validatorLazyPrimeReadFailures);
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
'@ 'validator lazy postfix'

$t20=Replace-OrThrow $t20 @'
                context.ValidatorNegatives.Add(__state.Key,
                    new ValidatorNegativeEntry(ThingFingerprint.Capture(context.Pawn, __state.Thing)));
                Interlocked.Increment(ref validatorNegativeStores);
'@ @'
                context.ValidatorNegatives.Add(__state.Key,
                    new ValidatorNegativeEntry(ThingFingerprint.CaptureCheap(__state.Thing)));
                Interlocked.Increment(ref validatorNegativeStores);
                Interlocked.Increment(ref validatorLazyStores);
'@ 'cheap store'

$t20=Replace-OrThrow $t20 @'
                ", scannerResolveBypass=" + Interlocked.Read(ref validatorScannerResolveBypass) +
                ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) + "]" +
                ", reach[observed=" + Interlocked.Read(ref reachObserved) +
'@ @'
                ", scannerResolveBypass=" + Interlocked.Read(ref validatorScannerResolveBypass) +
                ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) +
                ", lazy[stores=" + Interlocked.Read(ref validatorLazyStores) +
                ", primeAttempts=" + Interlocked.Read(ref validatorLazyPrimeAttempts) +
                ", primeSuccess=" + Interlocked.Read(ref validatorLazyPrimeSuccess) +
                ", primePositive=" + Interlocked.Read(ref validatorLazyPrimePositive) +
                ", primeReadFailures=" + Interlocked.Read(ref validatorLazyPrimeReadFailures) +
                ", estimatedStoreForbiddenReadsAvoided=" +
                Math.Max(0L, Interlocked.Read(ref validatorLazyStores) -
                    Interlocked.Read(ref validatorLazyPrimeSuccess) -
                    Interlocked.Read(ref validatorLazyPrimeReadFailures)) + "]]" +
                ", reach[observed=" + Interlocked.Read(ref reachObserved) +
'@ 'lazy summary'

$t20=Replace-OrThrow $t20 @'
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;
'@ @'
            internal bool Store;
            internal bool Prime;
            internal bool Verify;
            internal bool AuthoritativeHit;
'@ 'validator state prime flag'

$t20=Replace-OrThrow $t20 @'
            internal static ValidatorCallState Verify(TransactionContext context, ValidatorKey key,
                ValidatorTrustState trust)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Trust = trust, Verify = true
                };
            }
'@ @'
            internal static ValidatorCallState Prime(TransactionContext context, ValidatorKey key,
                WorkGiver_Scanner scanner, Thing thing)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Scanner = scanner, Thing = thing, Prime = true
                };
            }

            internal static ValidatorCallState Verify(TransactionContext context, ValidatorKey key,
                ValidatorTrustState trust)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Trust = trust, Verify = true
                };
            }
'@ 'validator state prime constructor'

$oldFingerprint=@'
        internal struct ThingFingerprint
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;
            internal readonly int HitPoints;
            internal readonly bool Forbidden;

            internal ThingFingerprint(Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount,
                int hitPoints, bool forbidden)
            {
                MapHeld = mapHeld; PositionHeld = positionHeld; Spawned = spawned;
                StackCount = stackCount; HitPoints = hitPoints; Forbidden = forbidden;
            }

            internal static ThingFingerprint Capture(Pawn pawn, Thing thing)
            {
                bool forbidden = false;
                try { forbidden = pawn != null && thing.IsForbidden(pawn); } catch { }
                return new ThingFingerprint(thing.MapHeld, thing.PositionHeld, thing.Spawned,
                    thing.stackCount, thing.HitPoints, forbidden);
            }

            internal bool Matches(Pawn pawn, Thing thing)
            {
                if (thing == null || !ReferenceEquals(MapHeld, thing.MapHeld) || PositionHeld != thing.PositionHeld ||
                    Spawned != thing.Spawned || StackCount != thing.stackCount || HitPoints != thing.HitPoints)
                    return false;
                try { return Forbidden == (pawn != null && thing.IsForbidden(pawn)); }
                catch { return false; }
            }
        }
'@

$newFingerprint=@'
        internal struct ThingFingerprint
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;
            internal readonly int HitPoints;
            internal readonly bool HasForbidden;
            internal readonly bool Forbidden;

            internal ThingFingerprint(Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount,
                int hitPoints, bool hasForbidden, bool forbidden)
            {
                MapHeld = mapHeld; PositionHeld = positionHeld; Spawned = spawned;
                StackCount = stackCount; HitPoints = hitPoints;
                HasForbidden = hasForbidden; Forbidden = forbidden;
            }

            // Store path: deliberately cheap. No IsForbidden call here.
            internal static ThingFingerprint CaptureCheap(Thing thing)
            {
                if (thing == null)
                    return new ThingFingerprint(null, IntVec3.Invalid, false, 0, 0, false, false);
                return new ThingFingerprint(thing.MapHeld, thing.PositionHeld, thing.Spawned,
                    thing.stackCount, thing.HitPoints, false, false);
            }

            internal bool MatchesCheap(Thing thing)
            {
                return thing != null &&
                    ReferenceEquals(MapHeld, thing.MapHeld) &&
                    PositionHeld == thing.PositionHeld &&
                    Spawned == thing.Spawned &&
                    StackCount == thing.stackCount &&
                    HitPoints == thing.HitPoints;
            }

            // Promotion happens only after the same entry was encountered again and the live
            // validator returned false again. Capture the post-live Forbidden state so future
            // authoritative hits retain the old mutation guard.
            internal static bool TryPrime(Pawn pawn, Thing thing, ThingFingerprint cheap,
                out ThingFingerprint primed)
            {
                primed = cheap;
                if (!cheap.MatchesCheap(thing))
                    return false;

                try
                {
                    bool forbidden = pawn != null && thing.IsForbidden(pawn);
                    primed = new ThingFingerprint(
                        thing.MapHeld, thing.PositionHeld, thing.Spawned,
                        thing.stackCount, thing.HitPoints, true, forbidden);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            internal bool MatchesPrimed(Pawn pawn, Thing thing)
            {
                if (!HasForbidden || !MatchesCheap(thing))
                    return false;
                try { return Forbidden == (pawn != null && thing.IsForbidden(pawn)); }
                catch { return false; }
            }
        }
'@
$t20=Replace-OrThrow $t20 $oldFingerprint $newFingerprint 'ThingFingerprint lazy replacement'

Set-Content $t20Path $t20 -Encoding UTF8

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t32a-reservation-transaction";' 'internal const string Version = "0.9.3-t32b-lazy-forbidden-fingerprint";' 'bootstrap version'
$boot=$boot.Replace('[RimMT] V0.9.3-T32A Reservation Transaction initialized.',
                    '[RimMT] V0.9.3-T32B Lazy Forbidden Fingerprint initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T32A Reservation Transaction','V0.9.3-T32B Lazy Forbidden Fingerprint')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T32A Reservation Transaction','V0.9.3-T32B Lazy Forbidden Fingerprint')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T32-B for RimWorld 1.5. T32-A Reservation Transaction remains active. T32-B reduces T21 validator memo self-overhead by storing only cheap Thing facts on first negative observation. IsForbidden is deferred until that exact entry is encountered again and the live validator is still negative; only then is the entry promoted to the original full mutation guard. Existing scanner/method trust, warmup, sampled parity, mismatch quarantine and fail-open behavior remain intact. No global forbidden cache, Job mutation, reservation mutation, worker wait or cross-package result is introduced.</description>')
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
    '<description>Optional diagnostics companion for RimMT T32-B. v0.11 surfaces the existing T32-A Reservation Transaction counters and the T21 lazy Forbidden fingerprint counters. No T30/T31 experimental primitive profiler is included.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T32-B Lazy Forbidden Fingerprint + Diagnostics v0.11.'
