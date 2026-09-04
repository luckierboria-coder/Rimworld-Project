$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.6 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.6 Continuation Fix
# - one pending state per pawn remains authoritative until completion/invalidation
# - higher-priority WorkGivers may still preempt; lower-priority WorkGivers cannot pass the pending scanner
# - unrelated hot scanners are never allowed to replace an existing pending state
# - budget is checked before every candidate validator call
# - atomic validator tails are measured separately from whole-search tails
# - shape/bypass reasons are split for the next generic-coverage step
# - ReachProfile V0.4.18 and all other mature production paths remain unchanged

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static bool suspendedThisPackage;
'@ @'
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static bool suspendedThisPackage;
        [ThreadStatic] private static WorkGiver_Scanner pendingScannerForPackage;
        [ThreadStatic] private static List<WorkGiver> currentWorkList;
        [ThreadStatic] private static int pendingListIndex;
'@ 'thread-local continuation barrier state'

$resume = Replace-OrThrow $resume @'
        private static long customEnumerableBypass;
        private static long shapeBypass;
        private static long authorityBypass;
        private static long priorityBlocks;
'@ @'
        private static long customEnumerableBypass;
        private static long shapeBypass;
        private static long baseShapeBypass;
        private static long exactClosureBypass;
        private static long prioritizedBypass;
        private static long scanCellsBypass;
        private static long allowUnreachableBypass;
        private static long unstableSourceBypass;
        private static long pendingBarrierBypass;
        private static long pendingOtherScannerBypass;
        private static long pendingEligibilityInvalidations;
        private static long authorityBypass;
        private static long priorityBlocks;
        private static long atomicValidatorCalls;
        private static long atomicValidatorOver5;
        private static long atomicValidatorOver10;
        private static long atomicValidatorOver20;
        private static long maxAtomicValidatorTicks;
'@ 'detailed bypass and atomic validator counters'

$resume = Replace-OrThrow $resume @'
        public static void PackagePrefix(Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            if (States.Count > MaxStates)
                PurgeInvalidStates();
        }
'@ @'
        public static void PackagePrefix(JobGiver_Work __instance, Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            currentWorkList = null;
            pendingListIndex = -1;

            if (__0 != null)
            {
                ResumeState pending;
                if (States.TryGetValue(__0, out pending) && pending != null && pending.Scanner != null)
                {
                    pendingScannerForPackage = pending.Scanner;
                    try
                    {
                        currentWorkList = __instance != null && __instance.emergency
                            ? __0.workSettings.WorkGiversInOrderEmergency
                            : __0.workSettings.WorkGiversInOrderNormal;
                        if (currentWorkList != null)
                            pendingListIndex = currentWorkList.IndexOf(pending.Scanner);
                    }
                    catch
                    {
                        currentWorkList = null;
                        pendingListIndex = -1;
                    }

                    if (pendingListIndex < 0)
                    {
                        States.Remove(__0);
                        pendingScannerForPackage = null;
                        pendingEligibilityInvalidations++;
                    }
                }
            }

            if (States.Count > MaxStates)
                PurgeInvalidStates();
        }
'@ 'package restores exact pending scanner and list position'

$resume = Replace-OrThrow $resume @'
        public static Exception PackageFinalizer(Exception __exception)
        {
            currentPawn = null;
            suspendedThisPackage = false;
            return __exception;
        }
'@ @'
        public static Exception PackageFinalizer(Exception __exception)
        {
            currentPawn = null;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            currentWorkList = null;
            pendingListIndex = -1;
            return __exception;
        }
'@ 'package clears continuation TLS'

$resume = Replace-OrThrow $resume @'
        public static void PawnCanUsePostfix(Pawn __0, ref bool __result)
        {
            if (!suspendedThisPackage || currentPawn == null || !ReferenceEquals(__0, currentPawn)) return;
            __result = false;
            priorityBlocks++;
        }
'@ @'
        public static void PawnCanUsePostfix(Pawn __0, WorkGiver __1, ref bool __result)
        {
            if (currentPawn == null || !ReferenceEquals(__0, currentPawn)) return;

            if (suspendedThisPackage)
            {
                __result = false;
                priorityBlocks++;
                return;
            }

            if (pendingScannerForPackage == null) return;

            if (ReferenceEquals(__1, pendingScannerForPackage))
            {
                // If the pending scanner itself is no longer usable, the continuation is obsolete.
                // Drop it immediately and let lower-priority work proceed normally in this package.
                if (!__result)
                {
                    States.Remove(__0);
                    pendingScannerForPackage = null;
                    pendingListIndex = -1;
                    pendingEligibilityInvalidations++;
                }
                return;
            }

            int currentIndex = -1;
            try
            {
                if (currentWorkList != null) currentIndex = currentWorkList.IndexOf(__1);
            }
            catch { currentIndex = -1; }

            // Preserve Vanilla priority: items before the pending scanner are higher priority and may
            // still preempt. Items after it are lower priority and may not pass the continuation barrier.
            if (pendingListIndex >= 0 && currentIndex > pendingListIndex)
            {
                __result = false;
                priorityBlocks++;
                pendingBarrierBypass++;
            }
        }
'@ 'priority-preserving pending barrier'

$resume = Replace-OrThrow $resume @'
            if (pawn == null || currentPawn == null || !ReferenceEquals(pawn, currentPawn) ||
                __1 == null || __1.Disposed || !pawn.Spawned || pawn.Map != __1 ||
                !__0.IsValid || !__0.InBounds(__1) || __5 <= 0f || __6 == null)
            {
                shapeBypass++;
                return true;
            }
'@ @'
            if (pawn == null || currentPawn == null || !ReferenceEquals(pawn, currentPawn) ||
                __1 == null || __1.Disposed || !pawn.Spawned || pawn.Map != __1 ||
                !__0.IsValid || !__0.InBounds(__1) || __5 <= 0f || __6 == null)
            {
                shapeBypass++;
                baseShapeBypass++;
                return true;
            }
'@ 'base shape bypass attribution'

$resume = Replace-OrThrow $resume @'
            WorkGiver_Scanner scanner = ResolveExactJobGiverScanner(__6);
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                return true;
            }

            if (!JobGiverTailTelemetry094.IsRecurringHot(scanner))
                return true;
'@ @'
            WorkGiver_Scanner scanner = ResolveExactJobGiverScanner(__6);
            if (scanner == null)
            {
                shapeBypass++;
                exactClosureBypass++;
                return true;
            }
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                return true;
            }

            ResumeState existingAny;
            States.TryGetValue(pawn, out existingAny);
            bool hasPendingForScanner = existingAny != null && ReferenceEquals(existingAny.Scanner, scanner);

            // Higher-priority scanners are allowed to preempt, but they are not allowed to steal the
            // single per-pawn continuation slot. They run Vanilla while another scanner is pending.
            if (existingAny != null && !hasPendingForScanner)
            {
                pendingOtherScannerBypass++;
                return true;
            }

            if (!hasPendingForScanner && !JobGiverTailTelemetry094.IsRecurringHot(scanner))
                return true;
'@ 'exact closure and continuation ownership admission'

$resume = Replace-OrThrow $resume @'
            if (!TryGetStableSource(__1, __2, __7, out source))
            {
                if (__7 != null) customEnumerableBypass++;
                else shapeBypass++;
                return true;
            }
'@ @'
            if (!TryGetStableSource(__1, __2, __7, out source))
            {
                if (__7 != null) customEnumerableBypass++;
                else unstableSourceBypass++;
                shapeBypass++;
                return true;
            }
'@ 'stable source bypass attribution'

$resume = Replace-OrThrow $resume @'
                long sliceStart = Stopwatch.GetTimestamp();
                long budgetTicks = SliceBudgetTicks();
                int processedSinceCheck = 0;

                while (state.NextIndex < state.Members.Length)
                {
                    Thing thing = state.Members[state.NextIndex++];
'@ @'
                long sliceStart = Stopwatch.GetTimestamp();
                long budgetTicks = SliceBudgetTicks();

                while (state.NextIndex < state.Members.Length)
                {
                    // Check before entering every candidate. A single validator cannot be preempted once
                    // entered, but we never knowingly start another candidate after the slice deadline.
                    if (state.NextIndex > 0 && Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
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

                    Thing thing = state.Members[state.NextIndex++];
'@ 'pre-validator deadline check'

$resume = Replace-OrThrow $resume @'
                                candidatesChecked++;
                                if (__6(thing)) state.Passed.Add(thing);
                                else validatorRejected++;
'@ @'
                                candidatesChecked++;
                                long validatorStart = Stopwatch.GetTimestamp();
                                bool valid = __6(thing);
                                long validatorTicks = Stopwatch.GetTimestamp() - validatorStart;
                                atomicValidatorCalls++;
                                if (validatorTicks > maxAtomicValidatorTicks) maxAtomicValidatorTicks = validatorTicks;
                                if (validatorTicks >= Stopwatch.Frequency * 5L / 1000L) atomicValidatorOver5++;
                                if (validatorTicks >= Stopwatch.Frequency * 10L / 1000L) atomicValidatorOver10++;
                                if (validatorTicks >= Stopwatch.Frequency * 20L / 1000L) atomicValidatorOver20++;
                                if (valid) state.Passed.Add(thing);
                                else validatorRejected++;
'@ 'atomic validator timing'

$resume = Replace-OrThrow $resume @'

                    processedSinceCheck++;
                    if ((processedSinceCheck & BudgetCheckMask) == 0 && state.NextIndex < state.Members.Length &&
                        Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
                    {
                        state.Slices++;
                        totalSlices++;
                        long sliceTicks = Stopwatch.GetTimestamp() - sliceStart;
                        if (sliceTicks > maxSliceTicks) maxSliceTicks = sliceTicks;
                        StoreState(pawn, state, existing);
                        suspendedThisPackage = true;
                        suspensions++;
                        __result = null;
                        return false;
                    }
'@ @'
'@ 'remove coarse budget polling block'

$resume = Replace-OrThrow $resume @'
                States.Remove(pawn);
                completed++;
'@ @'
                States.Remove(pawn);
                if (ReferenceEquals(pendingScannerForPackage, scanner))
                {
                    pendingScannerForPackage = null;
                    pendingListIndex = -1;
                }
                completed++;
'@ 'clear exact continuation only on completion'

$resume = Replace-OrThrow $resume @'
        private static bool IsSupportedScanner(WorkGiver_Scanner scanner)
        {
            if (scanner == null || scanner.def == null) return false;
            try
            {
                if (!scanner.def.scanThings || scanner.def.scanCells) return false;
                if (scanner.Prioritized || scanner.AllowUnreachable) return false;
                return true;
            }
            catch { return false; }
        }
'@ @'
        private static bool IsSupportedScanner(WorkGiver_Scanner scanner)
        {
            if (scanner == null || scanner.def == null) return false;
            try
            {
                if (!scanner.def.scanThings) { baseShapeBypass++; return false; }
                if (scanner.def.scanCells) { scanCellsBypass++; return false; }
                if (scanner.Prioritized) { prioritizedBypass++; return false; }
                if (scanner.AllowUnreachable) { allowUnreachableBypass++; return false; }
                return true;
            }
            catch { baseShapeBypass++; return false; }
        }
'@ 'scanner shape bypass attribution'

$resume = Replace-OrThrow $resume @'
                   ", customEnumerableBypass=" + customEnumerableBypass +
                   ", shapeBypass=" + shapeBypass +
                   ", authorityBypass=" + authorityBypass +
                   ", priorityBlocks=" + priorityBlocks +
'@ @'
                   ", customEnumerableBypass=" + customEnumerableBypass +
                   ", shapeBypass=" + shapeBypass +
                   " [base=" + baseShapeBypass + ", exactClosure=" + exactClosureBypass +
                   ", prioritized=" + prioritizedBypass + ", scanCells=" + scanCellsBypass +
                   ", allowUnreachable=" + allowUnreachableBypass + ", unstableSource=" + unstableSourceBypass +
                   ", pendingBarrier=" + pendingBarrierBypass + ", pendingOtherScanner=" + pendingOtherScannerBypass + "]" +
                   ", pendingEligibilityInvalidations=" + pendingEligibilityInvalidations +
                   ", authorityBypass=" + authorityBypass +
                   ", priorityBlocks=" + priorityBlocks +
                   ", atomicValidatorCalls=" + atomicValidatorCalls +
                   " [>5ms=" + atomicValidatorOver5 + ", >10ms=" + atomicValidatorOver10 +
                   ", >20ms=" + atomicValidatorOver20 + "]" +
                   ", maxAtomicValidatorUs=" + (maxAtomicValidatorTicks * 1000000.0 / Stopwatch.Frequency).ToString("F1") +
'@ 'summary detailed bypass and atomic timing'

# Replace diagnostic/install labels in the generated V0.9.5 source.
$resume = $resume.Replace('V0.9.5 recurring-hot JobGiver tail slicer.', 'V0.9.6 recurring-hot JobGiver continuation slicer.')
$resume = $resume.Replace('[RimMT] V0.9.5 Resumable JobGiver installed on ', '[RimMT] V0.9.6 Continuation Fix installed on ')
$resume = $resume.Replace('[RimMT] V0.9.5 Resumable JobGiver failed closed: ', '[RimMT] V0.9.6 Continuation Fix failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.5 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.6 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.5:', 'Resumable JobGiver V0.9.6:')
Set-Content $resumePath $resume -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.5-resumable-jobgiver','0.9.6-continuation-fix')
$boot = $boot.Replace('V0.9.5 Resumable JobGiver initialized','V0.9.6 Continuation Fix initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.5 Resumable JobGiver on-demand report','V0.9.6 Continuation Fix on-demand report')
$diag = $diag.Replace('V0.9.5 Resumable JobGiver; JobGiver tail buckets=ON; recurring-hot validator slicing=ON;', 'V0.9.6 Continuation Fix; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; pending continuation barrier=ON; atomic validator tails=ON;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.5 Resumable JobGiver','V0.9.6 Continuation Fix')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.5 Resumable JobGiver','RimMT V0.9.6 Continuation Fix')
$about = $about.Replace('adds recurring-hot main-thread validator slicing with priority-preserving suspension/resume and live final Vanilla authority.', 'fixes continuation ownership with a per-pawn pending scanner barrier, checks the budget before every candidate validator, and attributes atomic validator tails while retaining live final Vanilla authority.')
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.6 Continuation Fix: exact pending-state ownership, priority-preserving barrier, pre-validator budget checks, atomic validator tails and split bypass attribution; ReachProfile remains V0.4.18.'
