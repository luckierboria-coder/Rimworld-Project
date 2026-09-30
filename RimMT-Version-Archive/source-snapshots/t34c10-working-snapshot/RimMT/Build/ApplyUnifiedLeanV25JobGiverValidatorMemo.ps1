$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V25 JobGiver transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V25 is deliberately limited to the remaining JobGiver_Work validator hot set seen in V24:
#   HaulToCarrier / HaulMechsToCharger / Train.
# It does NOT touch Vehicle Framework's CarryToVehicle validator and does NOT modify ReachProfile.
#
# Strategy: during one synchronous JobGiver_Work.TryIssueJobPackage only, remember exact
# scanner+Thing pairs whose original JobGiver_Work validator already returned false. Repeated
# searches in that same package may reuse only that negative result. Nothing crosses package/tick/
# pawn boundaries. Positive results are never cached. Final JobOnThing, Reservation and
# Reachability stay live/Vanilla-authoritative.
#
# Safety: only exact JobGiver_Work compiler closures are eligible; only the three defNames above
# are whitelisted; any Harmony owner anywhere on HasJobOnThing/JobOnThing inheritance disables
# memoization for that runtime scanner type. Capacity is bounded and overflow simply runs Vanilla.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
using System.Reflection;
using HarmonyLib;
'@ @'
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
'@ 'V25 RuntimeHelpers import'

$s4 = Replace-OrThrow $s4 @'
        private static long targetedPrefilterAuthorityBypass;
        private static long actualValidatorCalls;
        private static long failures;
'@ @'
        private static long targetedPrefilterAuthorityBypass;
        private static long actualValidatorCalls;
        private static long validatorMemoEligibleScopes;
        private static long validatorMemoLookups;
        private static long validatorMemoHits;
        private static long validatorMemoStores;
        private static long validatorMemoCapacityBypass;
        private static long validatorMemoAuthorityBypass;
        private static long validatorMemoHaulToCarrierHits;
        private static long validatorMemoHaulMechsToChargerHits;
        private static long validatorMemoTrainHits;
        [ThreadStatic] private static long validatorMemoScopeStartTicks;
        [ThreadStatic] private static Dictionary<ValidatorMemoKey, byte> validatorNegativeMemo;
        private const int ValidatorNegativeMemoCapacity = 4096;
        private static long failures;
'@ 'V25 JobGiver memo counters and bounded package-local state'

$s4 = Replace-OrThrow $s4 @'
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ @'
            ValidatorMemoKind validatorMemoKind = ResolveValidatorMemoKind(validator, resolvedScanner);
            bool validatorMemoEnabled = false;
            if (validatorMemoKind != ValidatorMemoKind.None)
            {
                validatorMemoEligibleScopes++;
                if (IsTargetedPrefilterAuthoritySafe(resolvedScanner))
                {
                    PrepareValidatorMemoScope();
                    validatorMemoEnabled = validatorNegativeMemo != null;
                }
                else
                {
                    validatorMemoAuthorityBypass++;
                }
            }

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ 'V25 select authority-safe JobGiver memo route'

$s4 = Replace-OrThrow $s4 @'
                Thing thing = candidates[i].Thing;
                if (validator != null)
                {
                    localValidatorCalls++;
                    if (!validator(thing))
                    {
                        localValidatorRejected++;
                        continue;
                    }
                }
'@ @'
                Thing thing = candidates[i].Thing;
                if (validator != null)
                {
                    if (validatorMemoEnabled)
                    {
                        validatorMemoLookups++;
                        if (IsValidatorMemoNegative(resolvedScanner, thing))
                        {
                            validatorMemoHits++;
                            if (validatorMemoKind == ValidatorMemoKind.HaulToCarrier) validatorMemoHaulToCarrierHits++;
                            else if (validatorMemoKind == ValidatorMemoKind.HaulMechsToCharger) validatorMemoHaulMechsToChargerHits++;
                            else if (validatorMemoKind == ValidatorMemoKind.Train) validatorMemoTrainHits++;
                            continue;
                        }
                    }

                    localValidatorCalls++;
                    if (!validator(thing))
                    {
                        localValidatorRejected++;
                        if (validatorMemoEnabled)
                            StoreValidatorMemoNegative(resolvedScanner, thing);
                        continue;
                    }
                }
'@ 'V25 reuse only package-local false validator outcomes'

$s4 = Replace-OrThrow $s4 @'
        private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ @'
        private static ValidatorMemoKind ResolveValidatorMemoKind(Predicate<Thing> validator, WorkGiver_Scanner scanner)
        {
            if (validator == null || scanner == null || scanner.def == null) return ValidatorMemoKind.None;
            try
            {
                MethodInfo method = validator.Method;
                Type closure = method == null ? null : method.DeclaringType;
                if (closure == null || closure.DeclaringType != typeof(JobGiver_Work)) return ValidatorMemoKind.None;

                string defName = scanner.def.defName;
                if (defName == "HaulToCarrier") return ValidatorMemoKind.HaulToCarrier;
                if (defName == "HaulMechsToCharger") return ValidatorMemoKind.HaulMechsToCharger;
                if (defName == "Train") return ValidatorMemoKind.Train;
            }
            catch { }
            return ValidatorMemoKind.None;
        }

        private static void PrepareValidatorMemoScope()
        {
            long scope = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scope <= 0L) return;
            if (validatorNegativeMemo == null)
                validatorNegativeMemo = new Dictionary<ValidatorMemoKey, byte>(256);
            if (validatorMemoScopeStartTicks != scope)
            {
                validatorNegativeMemo.Clear();
                validatorMemoScopeStartTicks = scope;
            }
        }

        private static bool IsValidatorMemoNegative(WorkGiver_Scanner scanner, Thing thing)
        {
            if (scanner == null || thing == null || validatorNegativeMemo == null) return false;
            try { return validatorNegativeMemo.ContainsKey(new ValidatorMemoKey(scanner, thing)); }
            catch { return false; }
        }

        private static void StoreValidatorMemoNegative(WorkGiver_Scanner scanner, Thing thing)
        {
            if (scanner == null || thing == null || validatorNegativeMemo == null) return;
            try
            {
                ValidatorMemoKey key = new ValidatorMemoKey(scanner, thing);
                if (validatorNegativeMemo.ContainsKey(key)) return;
                if (validatorNegativeMemo.Count >= ValidatorNegativeMemoCapacity)
                {
                    validatorMemoCapacityBypass++;
                    return;
                }
                validatorNegativeMemo[key] = 1;
                validatorMemoStores++;
            }
            catch { }
        }

        private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ 'V25 JobGiver memo helpers'

$s4 = Replace-OrThrow $s4 @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", failures=" + failures +
'@ @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", jobGiverMemoEligibleScopes=" + validatorMemoEligibleScopes +
                   ", jobGiverMemoLookups=" + validatorMemoLookups +
                   ", jobGiverMemoHits=" + validatorMemoHits +
                   " [haulToCarrier=" + validatorMemoHaulToCarrierHits +
                   ", haulMechsToCharger=" + validatorMemoHaulMechsToChargerHits +
                   ", train=" + validatorMemoTrainHits + "]" +
                   ", jobGiverMemoStores=" + validatorMemoStores +
                   ", jobGiverMemoCapacityBypass=" + validatorMemoCapacityBypass +
                   ", jobGiverMemoAuthorityBypass=" + validatorMemoAuthorityBypass +
                   ", failures=" + failures +
'@ 'V25 JobGiver memo telemetry summary'

$s4 = Replace-OrThrow $s4 @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn }
'@ @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn }
        private enum ValidatorMemoKind { None, HaulToCarrier, HaulMechsToCharger, Train }

        private struct ValidatorMemoKey : IEquatable<ValidatorMemoKey>
        {
            internal readonly WorkGiver_Scanner Scanner;
            internal readonly Thing Thing;
            internal ValidatorMemoKey(WorkGiver_Scanner scanner, Thing thing)
            {
                Scanner = scanner;
                Thing = thing;
            }
            public bool Equals(ValidatorMemoKey other)
            {
                return ReferenceEquals(Scanner, other.Scanner) && ReferenceEquals(Thing, other.Thing);
            }
            public override bool Equals(object obj)
            {
                return obj is ValidatorMemoKey && Equals((ValidatorMemoKey)obj);
            }
            public override int GetHashCode()
            {
                unchecked
                {
                    return (RuntimeHelpers.GetHashCode(Scanner) * 397) ^ RuntimeHelpers.GetHashCode(Thing);
                }
            }
        }
'@ 'V25 JobGiver memo key and whitelist enum'

Set-Content $s4Path $s4 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'S4 tail=32ms + true-validator attribution + authority-safe pruners + package-local negative memo for HaulToCarrier/HaulMechsToCharger/Train;' 'V25 diagnostics S4 policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V25 S4 transform: package-local authority-safe negative validator memo for HaulToCarrier/HaulMechsToCharger/Train; Vehicle CarryToVehicle untouched; ReachProfile remains V0.4.18.'
