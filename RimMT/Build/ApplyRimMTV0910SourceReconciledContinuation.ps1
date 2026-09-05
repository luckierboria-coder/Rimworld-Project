$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.10 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

function Regex-Replace-OrThrow {
    param([string]$Text,[string]$Pattern,[string]$Replacement,[string]$Label)
    $rx = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $rx.IsMatch($Text)) { throw "RimMT V0.9.10 regex anchor not found: $Label" }
    return $rx.Replace($Text, $Replacement, 1)
}

# RimMT V0.9.10 Source-Reconciled Continuation
# - proven map.listerThings.ThingsMatching(request) sources may change membership/order across slices
# - original snapshot is preserved; newly appearing live members are incrementally queued and sliced
# - removed/unspawned members are naturally skipped; final validator/reachability authority remains live
# - custom enumerable/IList sources remain exact and fail-closed
# - source-change telemetry is separated from stale/state invalidation telemetry
# - package context, logical scanner identity, slice budgets and ReachProfile V0.4.18 remain unchanged

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        private static long sourceInvalidations;
        private static long staleInvalidations;
'@ @'
        private static long sourceInvalidations;
        private static long staleInvalidations;
        private static long stateStructuralInvalidations;
        private static long sourceCountChanged;
        private static long sourceOrderChanged;
        private static long sourceMembershipAdded;
        private static long sourceMembershipRemoved;
        private static long sourceReconciliations;
        private static long reconcileCandidatesChecked;
        private static long reconcileValidatorRejected;
'@ 'source reconciliation counters'

$resume = Replace-OrThrow $resume @'
            if (count < MinSourceCount)
            {
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
            }
'@ @'
            if (count < MinSourceCount)
            {
                // A proven ListerThings continuation may safely finish an older snapshot after the
                // live source shrinks below admission size. New states still require MinSourceCount.
                if (!(hasPendingForScanner && existingAny != null && existingAny.ReconciliableListerSource))
                {
                    if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                    return true;
                }
            }
'@ 'pending lister continuation survives source shrink'

$resume = Replace-OrThrow $resume @'
            if (existing != null && SamePackageContext(existing) && SameScannerIdentity(existing, scanner))
            {
                if (!ReferenceEquals(existing.Scanner, scanner))
                {
                    existing.Scanner = scanner;
                    scannerRebinds++;
                }
                if (!ValidateState(existing, source, pawn, __1, __0))
                {
                    States.Remove(pawn);
                    sourceInvalidations++;
                }
                else
                {
                    state = existing;
                    resumes++;
                    resumeSuccesses++;
                }
            }
'@ @'
            if (existing != null && SamePackageContext(existing) && SameScannerIdentity(existing, scanner))
            {
                if (!ReferenceEquals(existing.Scanner, scanner))
                {
                    existing.Scanner = scanner;
                    scannerRebinds++;
                }
                if (!ValidateState(existing, source, pawn, __1, __0))
                {
                    States.Remove(pawn);
                }
                else
                {
                    state = existing;
                    resumes++;
                    resumeSuccesses++;
                }
            }
'@ 'ValidateState owns truthful invalidation attribution'

$resume = Replace-OrThrow $resume @'
                state = CreateState(pawn, scanner, source, __1, __0, __5);
'@ @'
                state = CreateState(pawn, scanner, source, __1, __0, __5, __7 == null && !__2.IsUndefined);
'@ 'state creation captures proven ListerThings provenance'

# Insert reconciliation phase after the original snapshot scan and before completion accounting.
$resume = Replace-OrThrow $resume @'
                state.Slices++;
                totalSlices++;
                long completedSliceTicks = Stopwatch.GetTimestamp() - sliceStart;
                if (completedSliceTicks > maxSliceTicks) maxSliceTicks = completedSliceTicks;

                States.Remove(pawn);
'@ @'
                // For proven ListerThings sources, tolerate live membership/order changes. Anything
                // that appeared after the original snapshot is queued exactly once and receives the
                // same main-thread validator slicing before final live authority is consulted.
                if (state.ReconciliableListerSource)
                {
                    bool reconciliationStable = false;
                    while (!reconciliationStable)
                    {
                        if (!ObserveAndQueueListerChanges(state, source))
                        {
                            sourceInvalidations++;
                            DropPendingContinuation(pawn, scanner, false, false);
                            return true;
                        }

                        while (state.ReconcileIndex < state.ReconcileQueue.Count)
                        {
                            if (Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
                            {
                                state.Slices++;
                                totalSlices++;
                                long sliceTicks = Stopwatch.GetTimestamp() - sliceStart;
                                if (sliceTicks > maxSliceTicks) maxSliceTicks = sliceTicks;
                                StoreState(pawn, state, existing);
                                pendingScannerForPackage = scanner;
                                suspendedThisPackage = true;
                                suspensions++;
                                __result = null;
                                return false;
                            }

                            Thing thing = state.ReconcileQueue[state.ReconcileIndex++];
                            if (thing != null && thing.Spawned && thing.Map == __1)
                            {
                                IntVec3 pos = thing.Position;
                                if (pos.IsValid)
                                {
                                    long dx = (long)pos.x - __0.x;
                                    long dz = (long)pos.z - __0.z;
                                    double maxSq = (double)__5 * __5;
                                    if ((double)(dx * dx + dz * dz) <= maxSq)
                                    {
                                        candidatesChecked++;
                                        reconcileCandidatesChecked++;
                                        long validatorStart = Stopwatch.GetTimestamp();
                                        bool valid = __6(thing);
                                        long validatorTicks = Stopwatch.GetTimestamp() - validatorStart;
                                        atomicValidatorCalls++;
                                        if (validatorTicks > maxAtomicValidatorTicks) maxAtomicValidatorTicks = validatorTicks;
                                        if (validatorTicks >= Stopwatch.Frequency * 5L / 1000L) atomicValidatorOver5++;
                                        if (validatorTicks >= Stopwatch.Frequency * 10L / 1000L) atomicValidatorOver10++;
                                        if (validatorTicks >= Stopwatch.Frequency * 20L / 1000L) atomicValidatorOver20++;
                                        if (valid) state.Passed.Add(thing);
                                        else
                                        {
                                            validatorRejected++;
                                            reconcileValidatorRejected++;
                                        }
                                    }
                                }
                            }
                        }

                        int before = state.ReconcileQueue.Count;
                        if (!ObserveAndQueueListerChanges(state, source))
                        {
                            sourceInvalidations++;
                            DropPendingContinuation(pawn, scanner, false, false);
                            return true;
                        }
                        reconciliationStable = state.ReconcileQueue.Count == before;
                    }
                }

                state.Slices++;
                totalSlices++;
                long completedSliceTicks = Stopwatch.GetTimestamp() - sliceStart;
                if (completedSliceTicks > maxSliceTicks) maxSliceTicks = completedSliceTicks;

                States.Remove(pawn);
'@ 'time-sliced live source reconciliation before completion'

$resume = Replace-OrThrow $resume @'
        private static ResumeState CreateState(Pawn pawn, WorkGiver_Scanner scanner, IList<Thing> source,
            Map map, IntVec3 root, float maxDistance)
'@ @'
        private static ResumeState CreateState(Pawn pawn, WorkGiver_Scanner scanner, IList<Thing> source,
            Map map, IntVec3 root, float maxDistance, bool reconciliableListerSource)
'@ 'CreateState provenance parameter'

$resume = Replace-OrThrow $resume @'
                    MaxDistance = maxDistance,
                    CreatedTick = CurrentGameTick(),
                    Members = members,
                    Passed = new List<Thing>(Math.Min(count, 256)),
                    NextIndex = 0,
                    Slices = 0
'@ @'
                    MaxDistance = maxDistance,
                    CreatedTick = CurrentGameTick(),
                    Members = members,
                    Passed = new List<Thing>(Math.Min(count, 256)),
                    ReconciliableListerSource = reconciliableListerSource,
                    KnownMembers = reconciliableListerSource ? new HashSet<Thing>(members, ThingReferenceComparer.Instance) : null,
                    LastLiveMembers = reconciliableListerSource ? new HashSet<Thing>(members, ThingReferenceComparer.Instance) : null,
                    ReconcileQueue = reconciliableListerSource ? new List<Thing>() : null,
                    ReconcileIndex = 0,
                    NextIndex = 0,
                    Slices = 0
'@ 'state initializes reconciliation membership'

$validatePattern = '(?s)        private static bool ValidateState\(ResumeState state, IList<Thing> source, Pawn pawn, Map map, IntVec3 root\)\r?\n        \{.*?\r?\n        \}\r?\n\r?\n        private static void StoreState'
$validateReplacement = @'
        private static bool ValidateState(ResumeState state, IList<Thing> source, Pawn pawn, Map map, IntVec3 root)
        {
            if (state == null || !SamePackageContext(state) || !ReferenceEquals(state.Pawn, pawn) ||
                !ReferenceEquals(state.Map, map) || state.Root != root || state.Members == null)
            {
                stateStructuralInvalidations++;
                return false;
            }

            int now = CurrentGameTick();
            if (now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
            {
                staleInvalidations++;
                return false;
            }

            int count;
            try { count = source.Count; }
            catch
            {
                sourceInvalidations++;
                return false;
            }

            if (state.ReconciliableListerSource)
            {
                if (!ObserveAndQueueListerChanges(state, source))
                {
                    sourceInvalidations++;
                    return false;
                }
                return true;
            }

            // Third-party/custom IList semantics are unknown. Preserve the V0.9.9 exact-source rule.
            if (count != state.Members.Length)
            {
                sourceCountChanged++;
                sourceInvalidations++;
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(source[i], state.Members[i]))
                {
                    sourceOrderChanged++;
                    sourceInvalidations++;
                    return false;
                }
            }
            return true;
        }

        private static bool ObserveAndQueueListerChanges(ResumeState state, IList<Thing> source)
        {
            if (state == null || source == null || !state.ReconciliableListerSource ||
                state.KnownMembers == null || state.LastLiveMembers == null || state.ReconcileQueue == null)
                return false;

            try
            {
                int count = source.Count;
                HashSet<Thing> current = new HashSet<Thing>(ThingReferenceComparer.Instance);
                bool positionalMismatch = count != state.Members.Length;
                if (count != state.LastLiveMembers.Count) sourceCountChanged++;

                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing != null) current.Add(thing);
                    if (!positionalMismatch && i < state.Members.Length && !ReferenceEquals(thing, state.Members[i]))
                        positionalMismatch = true;
                }

                long addedDelta = 0;
                foreach (Thing thing in current)
                    if (!state.LastLiveMembers.Contains(thing)) addedDelta++;

                long removedDelta = 0;
                foreach (Thing thing in state.LastLiveMembers)
                    if (!current.Contains(thing)) removedDelta++;

                if (addedDelta != 0) sourceMembershipAdded += addedDelta;
                if (removedDelta != 0) sourceMembershipRemoved += removedDelta;
                if (positionalMismatch && addedDelta == 0 && removedDelta == 0) sourceOrderChanged++;

                int queued = 0;
                foreach (Thing thing in current)
                {
                    if (state.KnownMembers.Add(thing))
                    {
                        state.ReconcileQueue.Add(thing);
                        queued++;
                    }
                }
                if (queued > 0) sourceReconciliations++;

                state.LastLiveMembers = current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void StoreState
'@
$resume = Regex-Replace-OrThrow $resume $validatePattern $validateReplacement 'truthful validation + lister source reconciliation helper'

$resume = Replace-OrThrow $resume @'
            internal Thing[] Members;
            internal List<Thing> Passed;
            internal int NextIndex;
'@ @'
            internal Thing[] Members;
            internal List<Thing> Passed;
            internal bool ReconciliableListerSource;
            internal HashSet<Thing> KnownMembers;
            internal HashSet<Thing> LastLiveMembers;
            internal List<Thing> ReconcileQueue;
            internal int ReconcileIndex;
            internal int NextIndex;
'@ 'resume state reconciliation fields'

# Add a strict reference comparer so Thing identity never depends on modded Equals/GetHashCode behavior.
$resume = Replace-OrThrow $resume @'
        private sealed class ResumeState
'@ @'
        private sealed class ThingReferenceComparer : IEqualityComparer<Thing>
        {
            internal static readonly ThingReferenceComparer Instance = new ThingReferenceComparer();
            public bool Equals(Thing x, Thing y) { return ReferenceEquals(x, y); }
            public int GetHashCode(Thing obj)
            {
                return obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private sealed class ResumeState
'@ 'reference-identity comparer'

$resume = Replace-OrThrow $resume @'
                   ", sourceInvalidations=" + sourceInvalidations +
                   ", staleInvalidations=" + staleInvalidations +
'@ @'
                   ", sourceInvalidations=" + sourceInvalidations +
                   ", staleInvalidations=" + staleInvalidations +
                   ", stateStructuralInvalidations=" + stateStructuralInvalidations +
                   ", sourceCountChanged=" + sourceCountChanged +
                   ", sourceOrderChanged=" + sourceOrderChanged +
                   ", sourceMembershipAdded=" + sourceMembershipAdded +
                   ", sourceMembershipRemoved=" + sourceMembershipRemoved +
                   ", sourceReconciliations=" + sourceReconciliations +
                   ", reconcileCandidatesChecked=" + reconcileCandidatesChecked +
                   ", reconcileValidatorRejected=" + reconcileValidatorRejected +
'@ 'source reconciliation telemetry summary'

$resume = $resume.Replace('V0.9.9 recurring-hot JobGiver package-context continuation slicer.', 'V0.9.10 recurring-hot JobGiver source-reconciled continuation slicer.')
$resume = $resume.Replace('[RimMT] V0.9.9 Package Context Continuation installed on ', '[RimMT] V0.9.10 Source-Reconciled Continuation installed on ')
$resume = $resume.Replace('[RimMT] V0.9.9 Package Context Continuation failed closed: ', '[RimMT] V0.9.10 Source-Reconciled Continuation failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.9 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.10 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.9:', 'Resumable JobGiver V0.9.10:')
Set-Content $resumePath $resume -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.9-package-context-continuation','0.9.10-source-reconciled-continuation')
$boot = $boot.Replace('V0.9.9 Package Context Continuation initialized','V0.9.10 Source-Reconciled Continuation initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.9 Package Context Continuation on-demand report','V0.9.10 Source-Reconciled Continuation on-demand report')
$diag = $diag.Replace('V0.9.9 Package Context Continuation; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; package context=JobGiver_Work+lane; logical scanner identity=WorkGiverDef+type; cross-lane priority barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;', 'V0.9.10 Source-Reconciled Continuation; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; package context=JobGiver_Work+lane; logical scanner identity=WorkGiverDef+type; ListerThings source reconciliation=ON; custom source exactness=ON; cross-lane priority barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.9 Package Context Continuation','V0.9.10 Source-Reconciled Continuation')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.9 Package Context Continuation','RimMT V0.9.10 Source-Reconciled Continuation')
$about = [regex]::Replace($about, '<description>.*?</description>', '<description>RimMT V0.9.10 Source-Reconciled Continuation for RimWorld 1.5. Keeps package-context and WorkGiverDef+type continuation ownership from V0.9.9, while proven map ListerThings candidate sources may now change membership or ordering across slices without discarding progress. The original snapshot continues under the same main-thread time budget, newly appearing live candidates are reconciled and sliced before final selection, removed candidates are naturally skipped, and final validator/reachability authority remains live. Unknown custom enumerable sources remain exact and fail-closed. Source-change telemetry is separated from stale/state invalidation telemetry. Exact-closure coverage, ReachProfile V0.4.18 and other validated production paths remain unchanged.</description>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.10 Source-Reconciled Continuation: proven ListerThings sources reconcile live additions/removals/order changes across slices; custom sources remain exact; stale/source telemetry is separated; package context and ReachProfile stay unchanged.'