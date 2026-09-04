$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.6 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# V0.9.6 Continuation Fix
# - keeps the V0.9.5 architecture and all stable V0.9.3/V0.9.4 production paths
# - fixes per-pawn continuation ownership so a suspended scanner cannot be silently replaced by unrelated scanners
# - checks the slice deadline before starting every validator call
# - attributes atomic validator tails separately from whole-search tails
# - splits shape bypass telemetry for future generic coverage expansion
# - retires no additional stable subsystem and leaves ReachProfile V0.4.18 unchanged

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static bool suspendedThisPackage;
'@ @'
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static bool suspendedThisPackage;
        [ThreadStatic] private static WorkGiver_Scanner pendingScannerForPackage;
'@ 'thread-local pending scanner barrier'

$resume = Replace-OrThrow $resume @'
        private static long customEnumerableBypass;
        private static long shapeBypass;
        private static long authorityBypass;
'@ @'
        private static long customEnumerableBypass;
        private static long shapeBypass;
        private static long exactClosureBypass;
        private static long prioritizedBypass;
        private static long scanCellsBypass;
        private static long allowUnreachableBypass;
        private static long unstableSourceBypass;
        private static long pendingBarrierBypass;
        private static long authorityBypass;
        private static long atomicValidatorCalls;
        private static long atomicValidatorOver5;
        private static long atomicValidatorOver10;
        private static long atomicValidatorOver20;
        private static long maxAtomicValidatorTicks;
'@ 'detailed bypass and atomic-validator counters'

$resume = Replace-OrThrow $resume @'
        public static void PackagePrefix(Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            if (States.Count > MaxStates)
                PurgeInvalidStates();
        }
'@ @'
        public static void PackagePrefix(Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            if (__0 != null)
            {
                ResumeState pending;
                if (States.TryGetValue(__0, out pending) && pending != null)
                    pendingScannerForPackage = pending.Scanner;
            }
            if (States.Count > MaxStates)
                PurgeInvalidStates();
        }
'@ 'package picks up existing pending scanner'

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
            return __exception;
        }
'@ 'package clears pending scanner TLS'

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

            // Once a slice suspends, lower-priority work must not pass the barrier in this package.
            if (suspendedThisPackage)
            {
                __result = false;
                priorityBlocks++;
                return;
            }

            // If this pawn entered the package with an unfinished scanner, only that exact scanner may
            // execute until it completes or invalidates. This turns suspension into a real continuation
            // rather than letting an unrelated hot scanner replace the pending state.
            if (pendingScannerForPackage != null && !ReferenceEquals(__1, pendingScannerForPackage))
            {
                __result = false;
                priorityBlocks++;
                pendingBarrierBypass++;
            }
        }
'@ 'priority-preserving pending barrier'

$resume = Replace-OrThrow $resume @'
            WorkGiver_Scanner scanner = ResolveExactJobGiverScanner(__6);
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                return true;
            }
'@ @'
            WorkGiver_Scanner scanner = ResolveExactJobGiverScanner(__6);
            if (scanner == null)
            {
                exactClosureBypass++;
                shapeBypass++;
                return true;
            }
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                return true;
            }

            if (pendingScannerForPackage != null && !ReferenceEquals(scanner, pendingScannerForPackage))
            {
                pendingBarrierBypass++;
                suspendedThisPackage = true;
                __result = null;
                return false;
            }
'@ 'exact closure and pending scanner enforcement'

$resume = Replace-OrThrow $resume @'
            if (!JobGiverTailTelemetry094.IsRecurringHot(scanner))
                return true;
'@ @'
            ResumeState pendingBeforeHot;
            bool hasPendingForScanner = States.TryGetValue(pawn, out pendingBeforeHot) && pendingBeforeHot != null &&
                ReferenceEquals(pendingBeforeHot.Scanner, scanner);
            if (!hasPendingForScanner && !JobGiverTailTelemetry094.IsRecurringHot(scanner))
                return true;
'@ 'pending continuation bypasses hot-admission recheck'

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
                int processedSinceCheck = 0;

                while (state.NextIndex < state.Members.Length)
                {
                    Thing thing = state.Members[state.NextIndex++];
'@ @'
                while (state.NextIndex < state.Members.Length)
                {
                    // Check the deadline before starting every validator call. We cannot preempt a
                    // validator once entered, but we must never knowingly start another one after budget.
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
'@ 'remove coarse post-validator budget check'

$resume = Replace-OrThrow $resume @'
                States.Remove(pawn);
                completed++;
'@ @'
                States.Remove(pawn);
                if (ReferenceEquals(pendingScannerForPackage, scanner)) pendingScannerForPackage = null;
                completed++;
'@ 'clear pending barrier only on exact completion'

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
                if (!scanner.def.scanThings) return false;
                if (scanner.def.scanCells) { scanCellsBypass++; return false; }
                if (scanner.Prioritized) { prioritizedBypass++; return false; }
                if (scanner.AllowUnreachable) { allowUnreachableBypass++; return false; }
                return true;
            }
            catch { return false; }
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
                   " [exactClosure=" + exactClosureBypass + ", prioritized=" + prioritizedBypass +
                   ", scanCells=" + scanCellsBypass + ", allowUnreachable=" + allowUnreachableBypass +
                   ", unstableSource=" + unstableSourceBypass + ", pendingBarrier=" + pendingBarrierBypass + "]" +
                   ", authorityBypass=" + authorityBypass +
                   ", priorityBlocks=" + priorityBlocks +
                   ", atomicValidatorCalls=" + atomicValidatorCalls +
                   " [>5ms=" + atomicValidatorOver5 + ", >10ms=" + atomicValidatorOver10 +
                   ", >20ms=" + atomicValidatorOver20 + "]" +
                   ", maxAtomicValidatorUs=" + (maxAtomicValidatorTicks * 1000000.0 / Stopwatch.Frequency).ToString("F1") +
'@ 'summary detailed bypass and atomic timing'

Set-Content $resumePath $resume -Encoding UTF8

# Rename bootstrap/settings/report surface to V0.9.6 while leaving internal 0.9.5 class filename stable.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.5-resumable-jobgiver','0.9.6-continuation-fix')
$boot = $boot.Replace('V0.9.5 Resumable JobGiver initialized','V0.9.6 Continuation Fix initialized')
$boot = $boot.Replace('V0.9.5 Resumable JobGiver install failed','V0.9.6 Continuation Fix install failed')
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
$about = $about.Replace('RimMT V0.9.5 Resumable JobGiver for RimWorld 1.5.', 'RimMT V0.9.6 Continuation Fix for RimWorld 1.5.')
$about = $about.Replace('adds recurring-hot main-thread validator slicing with priority-preserving suspension/resume and live final Vanilla authority.', 'fixes continuation ownership with a per-pawn pending scanner barrier, checks time budget before each validator, and attributes atomic validator tails while retaining live final Vanilla authority.')
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.6 Continuation Fix: real pending-state continuation barrier, pre-validator deadline checks, atomic validator tail telemetry, detailed bypass attribution; ReachProfile remains V0.4.18.'
