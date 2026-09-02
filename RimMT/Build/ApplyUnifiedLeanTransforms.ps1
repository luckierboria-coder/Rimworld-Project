$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# Legacy source remains in-tree but is not installed by the V0.9.2 bootstrap. Keep its production
# transform deterministic because the project still compiles the file and this verifies the old
# source does not silently drift away from the Unified Lean safety policy.
$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfiles.cs'
$text = Get-Content $path -Raw

$sampleOld = 'private const int SampleMask = 15; // 1/16 after warmup.'
$sampleNew = 'private const int SampleMask = 127; // Unified Lean: 1/128 after warmup; fuse probation still forces live validation.'
$fieldOld = '[ThreadStatic] private static int bypassDepth;'
$guardOld = 'if (bypassDepth != 0 || !compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||'
$guardNew = 'if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||'

$text = Replace-OrThrow $text $sampleOld $sampleNew 'legacy ReachProfile sample cadence'
$text = Replace-OrThrow $text $fieldOld '' 'legacy bypassDepth field'
$text = Replace-OrThrow $text $guardOld $guardNew 'legacy bypassDepth guard'
Set-Content $path $text -Encoding UTF8

# V0.4.18 production transform: retain the V0.4.17 sliced/local-first implementation but add a
# conservative profile lease. An expired profile never regains authority from age alone: it must
# first survive four forced-live Vanilla comparisons. Any mismatch quarantines the slot and clears
# the lease. A hard 720-frame age always forces a full recapture. Also tighten topology slice checks
# so the adaptive microsecond budget has finer preemption granularity.
$v17Path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$v17 = Get-Content $v17Path -Raw

$v17 = Replace-OrThrow $v17 @'
        private const long MaxProfileAgeFrames = 180;
        private const long BuildCooldownFrames = 12;
'@ @'
        private const long MaxProfileAgeFrames = 180;
        private const long LeaseExtensionFrames = 180;
        private const long HardMaxProfileAgeFrames = 720;
        private const int LeaseProbeSamplesRequired = 4;
        private const long BuildCooldownFrames = 12;
'@ 'V0.4.17 lease constants'

$v17 = Replace-OrThrow $v17 '        private const int SliceCheckMask = 63;' '        private const int SliceCheckMask = 15;' 'V0.4.17 topology slice check mask'
$v17 = Replace-OrThrow $v17 '                            if ((build.RegionCursor & 15) == 0 && BudgetSpent(sliceStart, budgetTicks))' '                            if ((build.RegionCursor & 3) == 0 && BudgetSpent(sliceStart, budgetTicks))' 'V0.4.17 adjacency slice granularity'

$v17 = Replace-OrThrow $v17 @'
        private static long localSlotQuarantines;
        private static long localOnlyFuseDeferrals;
'@ @'
        private static long localSlotQuarantines;
        private static long localOnlyFuseDeferrals;
        private static long leaseProbeCalls;
        private static long leaseProbeMatches;
        private static long leaseRenewals;
        private static long leaseFailures;
        private static long forcedRefreshes;
'@ 'V0.4.17 lease counters'

$v17 = Replace-OrThrow $v17 @'
                if (now - profile.CaptureFrame > MaxProfileAgeFrames)
                {
                    Interlocked.Increment(ref profileExpired);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }

                Interlocked.Increment(ref profileHits);
'@ @'
                long profileAge = now - profile.CaptureFrame;
                long leaseUntil = Interlocked.Read(ref slot.LeaseUntilFrame);
                if (profileAge > HardMaxProfileAgeFrames)
                {
                    Interlocked.Increment(ref profileExpired);
                    Interlocked.Increment(ref forcedRefreshes);
                    Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                    Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
                    EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                    return true;
                }

                if (profileAge > MaxProfileAgeFrames && leaseUntil <= now)
                {
                    Interlocked.Increment(ref profileExpired);
                    Prediction leasePrediction = profile.Classify(start, dest, peMode, map, traverseParams);
                    if (leasePrediction == Prediction.Unknown)
                    {
                        Interlocked.Increment(ref queriesUnknown);
                        Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                        EnsureProfileScheduled(map, mapState, pawn, traverseParams, key, slot);
                        return true;
                    }

                    bool leasePredicted = leasePrediction == Prediction.Reachable;
                    if (leasePredicted) Interlocked.Increment(ref predictedReachable);
                    else Interlocked.Increment(ref predictedUnreachable);
                    __state = new ReachSampleState(true, leasePredicted, slot, profile.RegionGeneration, true);
                    Interlocked.Increment(ref shadowSamples);
                    Interlocked.Increment(ref leaseProbeCalls);
                    return true;
                }

                Interlocked.Increment(ref profileHits);
'@ 'V0.4.17 profile lease expiry branch'

$v17 = Replace-OrThrow $v17 @'
            bool mismatch = __result != __state.Predicted;
            if (!mismatch)
            {
                Interlocked.Increment(ref shadowMatches);
                Interlocked.Increment(ref __state.Slot.ValidatedMatches);
                ObserveRollingSample(false, __state.Slot);
                return;
            }
'@ @'
            bool mismatch = __result != __state.Predicted;
            if (__state.LeaseProbe && !mismatch)
            {
                Interlocked.Increment(ref shadowMatches);
                Interlocked.Increment(ref leaseProbeMatches);
                ObserveRollingSample(false, __state.Slot);
                int clean = Interlocked.Increment(ref __state.Slot.LeaseProbeMatches);
                if (clean >= LeaseProbeSamplesRequired)
                {
                    Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
                    Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, RimMTRuntime.MainThreadFrames + LeaseExtensionFrames);
                    Interlocked.Exchange(ref __state.Slot.ValidatedMatches, LeaseProbeSamplesRequired);
                    Interlocked.Increment(ref leaseRenewals);
                }
                return;
            }
            if (__state.LeaseProbe)
            {
                Interlocked.Increment(ref leaseFailures);
                Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
                Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, 0L);
            }
            if (!mismatch)
            {
                Interlocked.Increment(ref shadowMatches);
                Interlocked.Increment(ref __state.Slot.ValidatedMatches);
                ObserveRollingSample(false, __state.Slot);
                return;
            }
'@ 'V0.4.17 lease probe postfix'

$v17 = Replace-OrThrow $v17 @'
            Interlocked.Exchange(ref __state.Slot.ValidatedMatches, 0);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
            Volatile.Write(ref __state.Slot.Published, null);
'@ @'
            Interlocked.Exchange(ref __state.Slot.ValidatedMatches, 0);
            Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
            Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, 0L);
            Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
            Volatile.Write(ref __state.Slot.Published, null);
'@ 'V0.4.17 mismatch lease reset'

$v17 = Replace-OrThrow $v17 @'
                Interlocked.Exchange(ref slot.ValidatedMatches, 0);
                Interlocked.Exchange(ref slot.PredictionSerial, 0);
                Volatile.Write(ref slot.Published, profile);
'@ @'
                Interlocked.Exchange(ref slot.ValidatedMatches, 0);
                Interlocked.Exchange(ref slot.PredictionSerial, 0);
                Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
                Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
                Volatile.Write(ref slot.Published, profile);
'@ 'V0.4.17 publish lease reset'

$v17 = Replace-OrThrow $v17 @'
                ", localSlotQuarantines=" + Interlocked.Read(ref localSlotQuarantines) +
                ", rollingMode=" + reachFuseMode +
'@ @'
                ", localSlotQuarantines=" + Interlocked.Read(ref localSlotQuarantines) +
                ", leaseProbeCalls=" + Interlocked.Read(ref leaseProbeCalls) +
                ", leaseProbeMatches=" + Interlocked.Read(ref leaseProbeMatches) +
                ", leaseRenewals=" + Interlocked.Read(ref leaseRenewals) +
                ", leaseFailures=" + Interlocked.Read(ref leaseFailures) +
                ", forcedRefreshes=" + Interlocked.Read(ref forcedRefreshes) +
                ", rollingMode=" + reachFuseMode +
'@ 'V0.4.17 lease summary counters'

$v17 = Replace-OrThrow $v17 @'
                ", maxProfileAgeFrames=" + MaxProfileAgeFrames +
                ", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +
'@ @'
                ", maxProfileAgeFrames=" + MaxProfileAgeFrames +
                ", leaseExtensionFrames=" + LeaseExtensionFrames +
                ", hardMaxProfileAgeFrames=" + HardMaxProfileAgeFrames +
                ", leaseProbeRequired=" + LeaseProbeSamplesRequired +
                ", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +
'@ 'V0.4.17 lease summary policy'

$v17 = Replace-OrThrow $v17 '            return "Aggressive reachability profile V0.4.17 sliced/local-first: compatibilityReady=" + compatibilityReady +' '            return "Aggressive reachability profile V0.4.18 sliced/local-first/lease: compatibilityReady=" + compatibilityReady +' 'V0.4.18 summary label'
$v17 = Replace-OrThrow $v17 '[RimMT] parallel.reachProfile V0.4.17 installed: topology capture is frame-sliced with adaptive budgets; mismatch handling is local-slot-first with multi-slot global soft fuse; emergency hard fuse remains 16/256.' '[RimMT] parallel.reachProfile V0.4.18 installed: topology capture is frame-sliced with finer budget checks; expired profiles require four clean forced-live lease probes; mismatch handling remains local-slot-first and emergency hard fuse remains 16/256.' 'V0.4.18 install log'

$v17 = Replace-OrThrow $v17 @'
            internal readonly ProfileSlot Slot;
            internal readonly long RegionGeneration;

            internal ReachSampleState(bool active, bool predicted, ProfileSlot slot, long generation)
            {
                Active = active;
                Predicted = predicted;
                Slot = slot;
                RegionGeneration = generation;
            }
'@ @'
            internal readonly ProfileSlot Slot;
            internal readonly long RegionGeneration;
            internal readonly bool LeaseProbe;

            internal ReachSampleState(bool active, bool predicted, ProfileSlot slot, long generation, bool leaseProbe = false)
            {
                Active = active;
                Predicted = predicted;
                Slot = slot;
                RegionGeneration = generation;
                LeaseProbe = leaseProbe;
            }
'@ 'V0.4.17 ReachSampleState lease flag'

$v17 = Replace-OrThrow $v17 @'
            internal int ValidatedMatches;
            internal int PredictionSerial;
            internal ProfileSnapshot Published;
'@ @'
            internal int ValidatedMatches;
            internal int PredictionSerial;
            internal int LeaseProbeMatches;
            internal long LeaseUntilFrame;
            internal ProfileSnapshot Published;
'@ 'V0.4.17 ProfileSlot lease state'

Set-Content $v17Path $v17 -Encoding UTF8

# S4 Pen cheap-negative proof. Resolve the JobGiver_Work closure once per accelerated call, then
# compact obvious impossible candidates before sorting/validator work. Only Vanilla's leading,
# side-effect-free negative conditions are duplicated; survivors still execute the original
# validator, live Reachability and final JobOnThing.
$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long heavyWorkGiverResolved;
        private static long heavyWorkGiverUnresolved;
        private static long failures;
'@ @'
        private static long heavyWorkGiverResolved;
        private static long heavyWorkGiverUnresolved;
        private static long penPrefilterCalls;
        private static long penPrefilterRejected;
        private static long penPrefilterTakeToPenRejected;
        private static long penPrefilterRoamingRejected;
        private static long failures;
'@ 'S4 Pen prefilter counters'

$s4 = Replace-OrThrow $s4 @'
            int localValidatorRejected = 0;
            int localReachRejected = 0;
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ @'
            int localValidatorRejected = 0;
            int localReachRejected = 0;
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
            for (int i = 0; i < kept; i++)
'@ 'S4 Pen candidate compaction'

$s4 = Replace-OrThrow $s4 @'
        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ @'
        private static WorkGiver_Scanner TryResolveScanner(Predicate<Thing> validator)
        {
            if (validator == null) return null;
            try
            {
                object target = validator.Target;
                if (target == null) return null;
                Type targetType = target.GetType();
                FieldInfo scannerField;
                if (!ScannerFieldCache.TryGetValue(targetType, out scannerField))
                {
                    scannerField = ResolveScannerField(targetType);
                    ScannerFieldCache[targetType] = scannerField;
                }
                return scannerField == null ? null : scannerField.GetValue(target) as WorkGiver_Scanner;
            }
            catch
            {
                return null;
            }
        }

        private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return PenPrefilterKind.None;
            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_TakeRoamingAnimalsToPen)) return PenPrefilterKind.TakeRoamingAnimalsToPen;
            if (type == typeof(WorkGiver_TakeToPen)) return PenPrefilterKind.TakeToPen;
            if (scanner is WorkGiver_TakeToPen) return PenPrefilterKind.DerivedTakeToPen;
            return PenPrefilterKind.None;
        }

        private static bool PassPenCheapNegative(PenPrefilterKind kind, Pawn worker, Thing thing)
        {
            try
            {
                Pawn animal = thing as Pawn;
                if (animal == null || !animal.IsAnimal) return false;
                if (worker == null) return true;
                if (animal.Position.IsForbidden(worker)) return false;
                Map map = animal.Map;
                if (map != null && map.designationManager.DesignationOn(animal, DesignationDefOf.ReleaseAnimalToWild) != null)
                    return false;

                bool roaming = animal.MentalStateDef == MentalStateDefOf.Roaming;
                if (kind == PenPrefilterKind.TakeRoamingAnimalsToPen && !roaming) return false;
                if (kind == PenPrefilterKind.TakeToPen && !roaming && animal.MentalStateDef != null) return false;
                return true;
            }
            catch
            {
                // Fail open: if any live property behaves unexpectedly, let the original validator decide.
                return true;
            }
        }

        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ 'S4 Pen prefilter helpers'

$s4 = Replace-OrThrow $s4 @'
                   ", heavyWorkGiverResolved=" + heavyWorkGiverResolved +
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
                   ", failures=" + failures +
'@ @'
                   ", heavyWorkGiverResolved=" + heavyWorkGiverResolved +
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
                   ", penPrefilterCalls=" + penPrefilterCalls +
                   ", penPrefilterRejected=" + penPrefilterRejected +
                   " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "]" +
                   ", failures=" + failures +
'@ 'S4 Pen prefilter summary'

$s4 = Replace-OrThrow $s4 '        private enum RescueRoute { StaticLarge, TailList, CustomTail }' @'
        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
'@ 'S4 Pen prefilter enum'

Set-Content $s4Path $s4 -Encoding UTF8

Write-Host 'Applied Unified Lean transforms: legacy ReachProfile cadence guard; V0.4.18 four-probe profile lease + hard age + finer sliced topology; S4 Pen cheap-negative candidate compaction.'
