$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T32-B anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T32-B Lazy Forbidden Fingerprint
# Applied on top of verified T32-A.
# Scope is deliberately narrow: only T21 validator-negative fingerprint construction changes.
#
# Safety model:
# - first negative store captures cheap Thing facts only; no IsForbidden call
# - first revisit of an unprimed entry is ALWAYS live
# - Forbidden is sampled immediately before that live validator call
# - after the live false result, cheap facts and Forbidden are sampled again
# - only if pre/post Forbidden are identical and cheap facts stayed stable is the entry Primed
# - only Primed entries may enter the existing T21 warmup/parity/authoritative replay path
# - live positive on the first revisit simply removes the unprimed entry; no quarantine is needed
#   because no cached result has yet been replayed
# - existing T21 mismatch quarantine remains unchanged after Primed
#
# This removes eager store-time IsForbidden reads while preserving the original authority boundary.

$t20Path='RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw

$t20=Replace-OrThrow $t20 @'
        private static long validatorPositiveLive;

        private static long reachObserved;
'@ @'
        private static long validatorPositiveLive;
        private static long validatorLazyStores;
        private static long validatorLazyRepeatProbes;
        private static long validatorLazyPrimeSuccess;
        private static long validatorLazyPrimePositive;
        private static long validatorLazyPrimeUnstable;
        private static long validatorForbiddenReads;
        private static long validatorStoreForbiddenReadsAvoided;

        private static long reachObserved;
'@ 'lazy fingerprint counters'

$t20=Replace-OrThrow $t20 'if (!entry.Fingerprint.Matches(context.Pawn, thing))' 'if (!entry.Fingerprint.MatchesCheap(thing))' 'cheap fingerprint gate'

$t20=Replace-OrThrow $t20 @'
            Interlocked.Increment(ref validatorMemoCandidates);
            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
'@ @'
            if (!entry.Primed)
            {
                bool forbiddenBefore;
                if (!TryReadForbidden(context.Pawn, thing, out forbiddenBefore))
                {
                    context.ValidatorNegatives.Remove(key);
                    Interlocked.Increment(ref validatorFingerprintBypass);
                    __state = ValidatorCallState.Store(context, key, scanner, thing);
                    return true;
                }

                Interlocked.Increment(ref validatorLazyRepeatProbes);
                __state = new ValidatorCallState
                {
                    Context = context,
                    Key = key,
                    Scanner = scanner,
                    Thing = thing,
                    Prime = true,
                    PrimeFingerprint = entry.Fingerprint,
                    PrimeForbiddenBefore = forbiddenBefore
                };
                return true;
            }

            if (!entry.Fingerprint.MatchesForbidden(context.Pawn, thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.Store(context, key, scanner, thing);
                return true;
            }

            Interlocked.Increment(ref validatorMemoCandidates);
            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
'@ 'lazy prime before trust'

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
                if (__result)
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimePositive);
                    return;
                }

                Thing thing = __state.Thing;
                if (thing == null || !__state.PrimeFingerprint.MatchesCheap(thing))
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimeUnstable);
                    return;
                }

                bool forbiddenAfter;
                if (!TryReadForbidden(context.Pawn, thing, out forbiddenAfter) ||
                    forbiddenAfter != __state.PrimeForbiddenBefore)
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimeUnstable);
                    return;
                }

                ValidatorNegativeEntry existing;
                if (context.ValidatorNegatives.TryGetValue(__state.Key, out existing) &&
                    existing.Fingerprint.MatchesCheap(thing))
                {
                    existing.Primed = true;
                    existing.Fingerprint = existing.Fingerprint.WithForbidden(forbiddenAfter);
                    context.ValidatorNegatives[__state.Key] = existing;
                    Interlocked.Increment(ref validatorLazyPrimeSuccess);
                }
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
                    new ValidatorNegativeEntry(ThingFingerprint.CaptureCheap(__state.Thing), false));
                Interlocked.Increment(ref validatorNegativeStores);
                Interlocked.Increment(ref validatorLazyStores);
                Interlocked.Increment(ref validatorStoreForbiddenReadsAvoided);
'@ 'cheap store'

$t20=Replace-OrThrow $t20 @'
                ", scannerResolveBypass=" + Interlocked.Read(ref validatorScannerResolveBypass) +
                ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) + "]" +
                ", reach[observed=" + Interlocked.Read(ref reachObserved) +
'@ @'
                ", scannerResolveBypass=" + Interlocked.Read(ref validatorScannerResolveBypass) +
                ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) +
                ", lazy[stores=" + Interlocked.Read(ref validatorLazyStores) +
                ", repeatProbes=" + Interlocked.Read(ref validatorLazyRepeatProbes) +
                ", primeSuccess=" + Interlocked.Read(ref validatorLazyPrimeSuccess) +
                ", primePositive=" + Interlocked.Read(ref validatorLazyPrimePositive) +
                ", primeUnstable=" + Interlocked.Read(ref validatorLazyPrimeUnstable) +
                ", forbiddenReads=" + Interlocked.Read(ref validatorForbiddenReads) +
                ", storeForbiddenReadsAvoided=" + Interlocked.Read(ref validatorStoreForbiddenReadsAvoided) + "]]" +
                ", reach[observed=" + Interlocked.Read(ref reachObserved) +
'@ 'lazy summary'

$t20=Replace-OrThrow $t20 @'
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;
'@ @'
            internal bool Store;
            internal bool Verify;
            internal bool Prime;
            internal bool PrimeForbiddenBefore;
            internal ThingFingerprint PrimeFingerprint;
            internal bool AuthoritativeHit;
'@ 'validator state prime fields'



$t20=Replace-OrThrow $t20 @'
        internal struct ValidatorNegativeEntry
        {
            internal readonly ThingFingerprint Fingerprint;
            internal ValidatorNegativeEntry(ThingFingerprint fingerprint) { Fingerprint = fingerprint; }
        }
'@ @'
        internal struct ValidatorNegativeEntry
        {
            internal ThingFingerprint Fingerprint;
            internal bool Primed;

            internal ValidatorNegativeEntry(ThingFingerprint fingerprint, bool primed)
            {
                Fingerprint = fingerprint;
                Primed = primed;
            }
        }
'@ 'validator entry primed state'

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
        private static bool TryReadForbidden(Pawn pawn, Thing thing, out bool forbidden)
        {
            forbidden = false;
            if (pawn == null || thing == null) return false;
            try
            {
                forbidden = thing.IsForbidden(pawn);
                Interlocked.Increment(ref validatorForbiddenReads);
                return true;
            }
            catch
            {
                return false;
            }
        }

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
                MapHeld = mapHeld;
                PositionHeld = positionHeld;
                Spawned = spawned;
                StackCount = stackCount;
                HitPoints = hitPoints;
                HasForbidden = hasForbidden;
                Forbidden = forbidden;
            }

            // Store path: deliberately cheap. No IsForbidden call here.
            internal static ThingFingerprint CaptureCheap(Thing thing)
            {
                if (thing == null)
                    return new ThingFingerprint(null, IntVec3.Invalid, false, 0, 0, false, false);

                return new ThingFingerprint(
                    thing.MapHeld,
                    thing.PositionHeld,
                    thing.Spawned,
                    thing.stackCount,
                    thing.HitPoints,
                    false,
                    false);
            }

            internal ThingFingerprint WithForbidden(bool forbidden)
            {
                return new ThingFingerprint(
                    MapHeld, PositionHeld, Spawned, StackCount, HitPoints, true, forbidden);
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

            internal bool MatchesForbidden(Pawn pawn, Thing thing)
            {
                if (!HasForbidden || !MatchesCheap(thing))
                    return false;

                bool currentForbidden;
                return TryReadForbidden(pawn, thing, out currentForbidden) &&
                    Forbidden == currentForbidden;
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
    '<description>RimMT T32-B for RimWorld 1.5. T32-A Reservation Transaction remains active. T32-B reduces T21 validator fingerprint self-overhead without weakening its false-only authority boundary: first negative stores capture only cheap Thing facts; the first revisit stays live, samples Forbidden before and after the live validator, and becomes Primed only when the live result is false and both cheap facts and Forbidden remain stable. Existing scanner/method trust, warmup, sampled parity and mismatch quarantine remain unchanged after priming. No global forbidden cache, Job mutation, reservation mutation, worker wait or cross-package result is introduced.</description>')
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
    '<description>Optional diagnostics companion for RimMT T32-B. v0.11 surfaces T32-A Reservation Transaction counters plus T21 lazy Forbidden fingerprint counters. T30/T31 experimental profilers remain absent.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T32-B Lazy Forbidden Fingerprint + Diagnostics v0.11.'
