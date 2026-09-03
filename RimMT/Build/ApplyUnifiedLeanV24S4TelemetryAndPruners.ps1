$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V24 S4 transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V24 remains S4-only. ReachProfile is deliberately untouched and stays on V0.4.18.
# 1) Separate cheap-prefilter rejects from rejects produced by the original validator so
#    heavyWorkGivers reflects real remaining work rather than successful pruning.
# 2) Restore the previously runtime-proven FeedHemogen and VisitSickPawn cheap-negative pruners,
#    using the stricter V23 authority check across HasJobOnThing/JobOnThing inheritance.
$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long targetedHaulCorpsesRejected;
        private static long targetedHoldingPlatformRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long failures;
'@ @'
        private static long targetedHaulCorpsesRejected;
        private static long targetedHoldingPlatformRejected;
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long actualValidatorCalls;
        private static long failures;
'@ 'S4 V24 telemetry and targeted counters'

$s4 = Replace-OrThrow $s4 @'
            int localValidatorRejected = 0;
            int localReachRejected = 0;
            WorkGiver_Scanner resolvedScanner = TryResolveScanner(validator);
'@ @'
            int localValidatorCalls = 0;
            int localValidatorRejected = 0;
            int localReachRejected = 0;
            WorkGiver_Scanner resolvedScanner = TryResolveScanner(validator);
'@ 'S4 V24 local actual-validator call counter'

# Prefilter rejects are not original validator rejects. Keep their dedicated counters only.
$s4 = Replace-OrThrow $s4 @'
                    if (!PassPenCheapNegative(penKind, traverseParms.pawn, candidate.Thing))
                    {
                        localValidatorRejected++;
                        penPrefilterRejected++;
'@ @'
                    if (!PassPenCheapNegative(penKind, traverseParms.pawn, candidate.Thing))
                    {
                        penPrefilterRejected++;
'@ 'S4 V24 remove Pen rejects from actual validator telemetry'

$s4 = Replace-OrThrow $s4 @'
                        if (!PassTargetedCheapNegative(targetedKind, traverseParms.pawn, candidate.Thing))
                        {
                            localValidatorRejected++;
                            targetedPrefilterRejected++;
                            if (targetedKind == TargetedPrefilterKind.HaulCorpses) targetedHaulCorpsesRejected++;
                            else targetedHoldingPlatformRejected++;
'@ @'
                        if (!PassTargetedCheapNegative(targetedKind, traverseParms.pawn, candidate.Thing))
                        {
                            targetedPrefilterRejected++;
                            if (targetedKind == TargetedPrefilterKind.HaulCorpses) targetedHaulCorpsesRejected++;
                            else if (targetedKind == TargetedPrefilterKind.TakeEntityToHoldingPlatform) targetedHoldingPlatformRejected++;
                            else if (targetedKind == TargetedPrefilterKind.FeedHemogen) targetedFeedHemogenRejected++;
                            else targetedVisitSickRejected++;
'@ 'S4 V24 targeted reject attribution without validator pollution'

$s4 = Replace-OrThrow $s4 @'
                Thing thing = candidates[i].Thing;
                if (validator != null && !validator(thing))
                {
                    localValidatorRejected++;
                    continue;
                }
'@ @'
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
'@ 'S4 V24 count actual validator invocations'

$s4 = Replace-OrThrow $s4 @'
                RecordRoute(route, localValidatorRejected, localReachRejected, validator);
                result = thing;
'@ @'
                RecordRoute(route, localValidatorCalls, localValidatorRejected, localReachRejected, validator);
                result = thing;
'@ 'S4 V24 successful route actual validator calls'

$s4 = Replace-OrThrow $s4 @'
            RecordRoute(route, localValidatorRejected, localReachRejected, validator);
            result = null;
'@ @'
            RecordRoute(route, localValidatorCalls, localValidatorRejected, localReachRejected, validator);
            result = null;
'@ 'S4 V24 null route actual validator calls'

$s4 = Replace-OrThrow $s4 @'
        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return TargetedPrefilterKind.None;
            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_HaulCorpses)) return TargetedPrefilterKind.HaulCorpses;
            if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform)) return TargetedPrefilterKind.TakeEntityToHoldingPlatform;
            return TargetedPrefilterKind.None;
        }
'@ @'
        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return TargetedPrefilterKind.None;
            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_HaulCorpses)) return TargetedPrefilterKind.HaulCorpses;
            if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform)) return TargetedPrefilterKind.TakeEntityToHoldingPlatform;
            if (scanner.def != null && scanner.def.defName == "FeedHemogen" && type == typeof(Workgiver_AdministerHemogen))
                return TargetedPrefilterKind.FeedHemogen;
            if (scanner.def != null && scanner.def.defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            return TargetedPrefilterKind.None;
        }
'@ 'S4 V24 restore proven targeted resolver entries'

$s4 = Replace-OrThrow $s4 @'
                if (kind == TargetedPrefilterKind.TakeEntityToHoldingPlatform)
                {
                    if (thing == null) return false;
                    CompHoldingPlatformTarget comp = thing.TryGetComp<CompHoldingPlatformTarget>();
                    if (comp == null || comp.targetHolder == null) return false;
                    Thing holder = comp.targetHolder;
                    if (holder.Destroyed || holder.MapHeld != thing.MapHeld) return false;

                    // EntityHolder should be present whenever the target comp is valid. If an
                    // unexpected mod state violates that invariant, fail open to Vanilla instead.
                    if (comp.EntityHolder == null) return true;
                    if (comp.EntityHolder.HeldPawn != null) return false;
                    return true;
                }

                return true;
'@ @'
                if (kind == TargetedPrefilterKind.TakeEntityToHoldingPlatform)
                {
                    if (thing == null) return false;
                    CompHoldingPlatformTarget comp = thing.TryGetComp<CompHoldingPlatformTarget>();
                    if (comp == null || comp.targetHolder == null) return false;
                    Thing holder = comp.targetHolder;
                    if (holder.Destroyed || holder.MapHeld != thing.MapHeld) return false;

                    // EntityHolder should be present whenever the target comp is valid. If an
                    // unexpected mod state violates that invariant, fail open to Vanilla instead.
                    if (comp.EntityHolder == null) return true;
                    if (comp.EntityHolder.HeldPawn != null) return false;
                    return true;
                }

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

                return true;
'@ 'S4 V24 restore proven targeted cheap negatives'

$s4 = Replace-OrThrow $s4 @'
        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
        {
            validatorRejected += validatorRejects;
'@ @'
        private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
        {
            actualValidatorCalls += validatorCalls;
            validatorRejected += validatorRejects;
'@ 'S4 V24 RecordRoute actual-validator signature'

$s4 = Replace-OrThrow $s4 @'
                   ", validatorRejected=" + validatorRejected +
                   " [static=" + staticLargeValidatorRejected + ", tailList=" + tailListValidatorRejected + ", custom=" + customTailValidatorRejected + "]" +
                   ", reachRejected=" + reachRejected +
'@ @'
                   ", validatorCallsActual=" + actualValidatorCalls +
                   ", validatorRejectedActual=" + validatorRejected +
                   " [static=" + staticLargeValidatorRejected + ", tailList=" + tailListValidatorRejected + ", custom=" + customTailValidatorRejected + "]" +
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected) +
                   ", reachRejected=" + reachRejected +
'@ 'S4 V24 truthful validator summary labels'

$s4 = Replace-OrThrow $s4 @'
                   ", targetedPrefilterRejected=" + targetedPrefilterRejected +
                   " [haulCorpses=" + targetedHaulCorpsesRejected + ", holdingPlatform=" + targetedHoldingPlatformRejected + "]" +
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
'@ @'
                   ", targetedPrefilterRejected=" + targetedPrefilterRejected +
                   " [haulCorpses=" + targetedHaulCorpsesRejected + ", holdingPlatform=" + targetedHoldingPlatformRejected +
                   ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected + "]" +
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
'@ 'S4 V24 targeted summary extensions'

$s4 = Replace-OrThrow $s4 @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform }
'@ @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn }
'@ 'S4 V24 targeted enum'

Set-Content $s4Path $s4 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'S4 tail=32ms + heavy attribution + authority-safe corpse/holding-platform pruners;' 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'V24 diagnostics S4 policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V24 S4 transforms: truthful original-validator telemetry plus authority-safe HaulCorpses/HoldingPlatform/FeedHemogen/VisitSick pruning; ReachProfile remains V0.4.18.'
