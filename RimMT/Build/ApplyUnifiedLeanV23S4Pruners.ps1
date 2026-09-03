$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V23 S4 transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V23 is intentionally S4-only. ReachProfile stays pinned to the proven V0.4.18 production form.
# The prefilters below duplicate only deterministic negative checks that occur before expensive
# Vanilla work. Any Harmony patch on HasJobOnThing/JobOnThing anywhere in the scanner inheritance
# chain disables the targeted prefilter for that exact runtime scanner type.
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
        private static long targetedHaulCorpsesRejected;
        private static long targetedHoldingPlatformRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long failures;
'@ 'S4 targeted counters'

$s4 = Replace-OrThrow $s4 @'
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyWorkGivers = new Dictionary<string, HeavyValidatorStats>();
        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();
'@ @'
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyWorkGivers = new Dictionary<string, HeavyValidatorStats>();
        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, bool> TargetedPrefilterAuthorityCache = new Dictionary<Type, bool>();
'@ 'S4 targeted authority cache'

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
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
'@ @'
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

            TargetedPrefilterKind targetedKind = ResolveTargetedPrefilter(resolvedScanner);
            if (targetedKind != TargetedPrefilterKind.None && kept > 0)
            {
                if (!IsTargetedPrefilterAuthoritySafe(resolvedScanner))
                {
                    targetedPrefilterAuthorityBypass++;
                }
                else
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
                            if (targetedKind == TargetedPrefilterKind.HaulCorpses) targetedHaulCorpsesRejected++;
                            else targetedHoldingPlatformRejected++;
                            continue;
                        }
                        candidates[write++] = candidate;
                    }
                    kept = write;
                }
            }

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
'@ 'S4 targeted candidate compaction'

$s4 = Replace-OrThrow $s4 @'
        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ @'
        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return TargetedPrefilterKind.None;
            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_HaulCorpses)) return TargetedPrefilterKind.HaulCorpses;
            if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform)) return TargetedPrefilterKind.TakeEntityToHoldingPlatform;
            return TargetedPrefilterKind.None;
        }

        private static bool IsTargetedPrefilterAuthoritySafe(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return false;
            Type type = scanner.GetType();
            bool cached;
            if (TargetedPrefilterAuthorityCache.TryGetValue(type, out cached)) return cached;

            bool safe = true;
            try
            {
                Type[] args = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
                string[] methodNames = new string[] { "HasJobOnThing", "JobOnThing" };
                for (int ni = 0; ni < methodNames.Length && safe; ni++)
                {
                    Type current = type;
                    while (current != null && typeof(WorkGiver).IsAssignableFrom(current))
                    {
                        MethodInfo method = current.GetMethod(methodNames[ni],
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
            }
            catch
            {
                safe = false;
            }

            TargetedPrefilterAuthorityCache[type] = safe;
            return safe;
        }

        private static bool PassTargetedCheapNegative(TargetedPrefilterKind kind, Pawn worker, Thing thing)
        {
            try
            {
                if (kind == TargetedPrefilterKind.HaulCorpses)
                {
                    // Vanilla WorkGiver_HaulCorpses.JobOnThing: non-corpses are rejected before
                    // any general hauling logic. Its global candidate source is the haulables lister,
                    // so this avoids entering PawnCanAutomaticallyHaulFast/HaulToStorageJob for them.
                    if (!(thing is Corpse)) return false;
                    if (worker == null || worker.Map == null) return true;

                    Pawn reserver = worker.Map.physicalInteractionReservationManager.FirstReserverOf(new LocalTargetInfo(thing));
                    if (reserver != null && reserver.RaceProps != null && reserver.RaceProps.Animal &&
                        reserver.Faction != Faction.OfPlayer)
                        return false;
                    return true;
                }

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
            }
            catch
            {
                // Fail open: original validator/Reservation/Reachability/JobOnThing remain authoritative.
                return true;
            }
        }

        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ 'S4 targeted helpers'

$s4 = Replace-OrThrow $s4 @'
                   ", penPrefilterRejected=" + penPrefilterRejected +
                   " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "]" +
                   ", failures=" + failures +
'@ @'
                   ", penPrefilterRejected=" + penPrefilterRejected +
                   " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "]" +
                   ", targetedPrefilterCalls=" + targetedPrefilterCalls +
                   ", targetedPrefilterRejected=" + targetedPrefilterRejected +
                   " [haulCorpses=" + targetedHaulCorpsesRejected + ", holdingPlatform=" + targetedHoldingPlatformRejected + "]" +
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", failures=" + failures +
'@ 'S4 targeted summary'

$s4 = Replace-OrThrow $s4 @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
'@ @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform }
'@ 'S4 targeted enum'

Set-Content $s4Path $s4 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'S4 tail=32ms + heavy WorkGiver attribution;' 'S4 tail=32ms + heavy attribution + authority-safe corpse/holding-platform pruners;' 'V23 diagnostics S4 policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V23 S4 transforms: authority-safe HaulCorpses and TakeEntityToHoldingPlatform cheap-negative pruning; ReachProfile remains V0.4.18.'
