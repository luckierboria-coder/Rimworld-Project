$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.8 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.8 Logical Scanner Identity
# - continuation identity is WorkGiverDef + exact runtime scanner type, never object-reference identity
# - each JobGiver_Work package rebinds a pending state to the current work-list scanner instance
# - ambiguous logical matches fail open and invalidate; unavailable work lists defer without destroying state
# - ClosestPrefix, PawnCanUse barrier, completion and pending-drop all use the same logical identity rule
# - no expansion of exact-closure coverage; slice budgets and ReachProfile V0.4.18 remain unchanged

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        private static long resumeSuccesses;
        private static long stateExpired;
        private static long authorityBypass;
'@ @'
        private static long resumeSuccesses;
        private static long stateExpired;
        private static long logicalIdentityMatches;
        private static long logicalIdentityMisses;
        private static long logicalIdentityAmbiguous;
        private static long scannerRebinds;
        private static long workListUnavailableDeferrals;
        private static long authorityBypass;
'@ 'logical identity counters'

$resume = Replace-OrThrow $resume @'
        public static void PackagePrefix(JobGiver_Work __instance, Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            currentWorkList = null;
            pendingListIndex = -1;

            if (States.Count > MaxStates)
                PurgeInvalidStates();

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
        }
'@ @'
        public static void PackagePrefix(JobGiver_Work __instance, Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            currentWorkList = null;
            pendingListIndex = -1;

            if (States.Count > MaxStates)
                PurgeInvalidStates();

            if (__0 == null) return;

            ResumeState pending;
            if (!States.TryGetValue(__0, out pending) || pending == null) return;

            if (pending.ScannerDef == null || pending.ScannerType == null)
            {
                States.Remove(__0);
                pendingEligibilityInvalidations++;
                logicalIdentityMisses++;
                return;
            }

            try
            {
                currentWorkList = __instance != null && __instance.emergency
                    ? __0.workSettings.WorkGiversInOrderEmergency
                    : __0.workSettings.WorkGiversInOrderNormal;
            }
            catch
            {
                currentWorkList = null;
            }

            // A temporarily unavailable work list is not evidence that the logical scanner disappeared.
            // Preserve the continuation and let this package run Vanilla without a barrier.
            if (currentWorkList == null)
            {
                workListUnavailableDeferrals++;
                return;
            }

            WorkGiver_Scanner rebound;
            int logicalIndex = FindLogicalScanner(currentWorkList, pending, out rebound);
            if (logicalIndex >= 0 && rebound != null)
            {
                pendingListIndex = logicalIndex;
                pendingScannerForPackage = rebound;
                logicalIdentityMatches++;
                if (!ReferenceEquals(pending.Scanner, rebound))
                {
                    pending.Scanner = rebound;
                    scannerRebinds++;
                }
                return;
            }

            // No match means the WorkGiver is no longer in the active list; multiple matches are unsafe.
            States.Remove(__0);
            pendingScannerForPackage = null;
            pendingListIndex = -1;
            pendingEligibilityInvalidations++;
            if (logicalIndex == -2) logicalIdentityAmbiguous++;
            else logicalIdentityMisses++;
        }
'@ 'package rebinds pending scanner by logical identity'

$resume = Replace-OrThrow $resume @'
            if (ReferenceEquals(__1, pendingScannerForPackage))
            {
                if (!__result)
                {
                    // PawnCanUseWorkGiver can be transiently false (cooldown/current state/etc.).
                    // Do not destroy the continuation. Release only this package's barrier so Vanilla
                    // may choose other work, then retry the same pending scanner on a later package.
                    eligibilityDeferrals++;
                    pendingScannerForPackage = null;
                    pendingListIndex = -1;
                }
                else
                {
                    resumeEligibleAttempts++;
                }
                return;
            }
'@ @'
            if (SameScannerIdentity(__1, pendingScannerForPackage))
            {
                if (!__result)
                {
                    // PawnCanUseWorkGiver can be transiently false (cooldown/current state/etc.).
                    // Keep the logical continuation, release only this package's barrier, and retry later.
                    eligibilityDeferrals++;
                    pendingScannerForPackage = null;
                    pendingListIndex = -1;
                }
                else
                {
                    resumeEligibleAttempts++;
                }
                return;
            }
'@ 'PawnCanUse matches rebound scanner by logical identity'

$resume = Replace-OrThrow $resume @'
            ResumeState existingAny;
            States.TryGetValue(pawn, out existingAny);
            bool hasPendingForScanner = existingAny != null && ReferenceEquals(existingAny.Scanner, scanner);

            // Higher-priority scanners may preempt, but cannot steal the one continuation slot.
'@ @'
            ResumeState existingAny;
            States.TryGetValue(pawn, out existingAny);
            bool hasPendingForScanner = existingAny != null && SameScannerIdentity(existingAny, scanner);
            if (hasPendingForScanner && !ReferenceEquals(existingAny.Scanner, scanner))
            {
                existingAny.Scanner = scanner;
                scannerRebinds++;
            }

            // Higher-priority scanners may preempt, but cannot steal the one continuation slot.
'@ 'ClosestPrefix continuation ownership uses logical identity'

$resume = Replace-OrThrow $resume @'
            if (existing != null && ReferenceEquals(existing.Scanner, scanner))
            {
                if (!ValidateState(existing, source, pawn, __1, __0))
'@ @'
            if (existing != null && SameScannerIdentity(existing, scanner))
            {
                if (!ReferenceEquals(existing.Scanner, scanner))
                {
                    existing.Scanner = scanner;
                    scannerRebinds++;
                }
                if (!ValidateState(existing, source, pawn, __1, __0))
'@ 'resume state match uses logical identity'

$resume = Replace-OrThrow $resume @'
                if (ReferenceEquals(pendingScannerForPackage, scanner))
                {
                    pendingScannerForPackage = null;
                    pendingListIndex = -1;
                }
'@ @'
                if (SameScannerIdentity(pendingScannerForPackage, scanner))
                {
                    pendingScannerForPackage = null;
                    pendingListIndex = -1;
                }
'@ 'completion clears logical pending scanner'

$resume = Replace-OrThrow $resume @'
        private static void DropPendingContinuation(Pawn pawn, WorkGiver_Scanner scanner, bool eligibility, bool source)
        {
            try
            {
                ResumeState pending;
                if (pawn != null && States.TryGetValue(pawn, out pending) && pending != null &&
                    (scanner == null || ReferenceEquals(pending.Scanner, scanner)))
                    States.Remove(pawn);
            }
            catch { }

            if (scanner == null || ReferenceEquals(pendingScannerForPackage, scanner))
            {
                pendingScannerForPackage = null;
                pendingListIndex = -1;
            }
            if (eligibility) pendingEligibilityInvalidations++;
            if (source) sourceInvalidations++;
        }
'@ @'
        private static bool SameScannerIdentity(ResumeState state, WorkGiver scanner)
        {
            if (state == null || scanner == null || state.ScannerDef == null || state.ScannerType == null)
                return false;
            try
            {
                return ReferenceEquals(state.ScannerDef, scanner.def) && state.ScannerType == scanner.GetType();
            }
            catch { return false; }
        }

        private static bool SameScannerIdentity(WorkGiver a, WorkGiver b)
        {
            if (a == null || b == null) return false;
            try
            {
                return a.GetType() == b.GetType() && a.def != null && ReferenceEquals(a.def, b.def);
            }
            catch { return false; }
        }

        // Returns -1 for no match and -2 for ambiguous duplicate logical identity.
        private static int FindLogicalScanner(List<WorkGiver> list, ResumeState state, out WorkGiver_Scanner scanner)
        {
            scanner = null;
            if (list == null || state == null) return -1;
            int found = -1;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    WorkGiver_Scanner candidate = list[i] as WorkGiver_Scanner;
                    if (candidate == null || !SameScannerIdentity(state, candidate)) continue;
                    if (found >= 0)
                    {
                        scanner = null;
                        return -2;
                    }
                    found = i;
                    scanner = candidate;
                }
            }
            catch
            {
                scanner = null;
                return -1;
            }
            return found;
        }

        private static void DropPendingContinuation(Pawn pawn, WorkGiver_Scanner scanner, bool eligibility, bool source)
        {
            try
            {
                ResumeState pending;
                if (pawn != null && States.TryGetValue(pawn, out pending) && pending != null &&
                    (scanner == null || SameScannerIdentity(pending, scanner)))
                    States.Remove(pawn);
            }
            catch { }

            if (scanner == null || SameScannerIdentity(pendingScannerForPackage, scanner))
            {
                pendingScannerForPackage = null;
                pendingListIndex = -1;
            }
            if (eligibility) pendingEligibilityInvalidations++;
            if (source) sourceInvalidations++;
        }
'@ 'logical identity helpers and pending-drop semantics'

$resume = Replace-OrThrow $resume @'
                ResumeState state = new ResumeState
                {
                    Pawn = pawn,
                    Scanner = scanner,
                    Map = map,
'@ @'
                ResumeState state = new ResumeState
                {
                    Pawn = pawn,
                    Scanner = scanner,
                    ScannerDef = scanner == null ? null : scanner.def,
                    ScannerType = scanner == null ? null : scanner.GetType(),
                    Map = map,
'@ 'state captures stable scanner identity'

$resume = Replace-OrThrow $resume @'
        private sealed class ResumeState
        {
            internal Pawn Pawn;
            internal WorkGiver_Scanner Scanner;
            internal Map Map;
'@ @'
        private sealed class ResumeState
        {
            internal Pawn Pawn;
            internal WorkGiver_Scanner Scanner;
            internal WorkGiverDef ScannerDef;
            internal Type ScannerType;
            internal Map Map;
'@ 'resume state stores WorkGiverDef and runtime type'

$resume = Replace-OrThrow $resume @'
                   ", resumeSuccesses=" + resumeSuccesses +
                   ", stateExpired=" + stateExpired +
                   ", authorityBypass=" + authorityBypass +
'@ @'
                   ", resumeSuccesses=" + resumeSuccesses +
                   ", stateExpired=" + stateExpired +
                   ", logicalIdentityMatches=" + logicalIdentityMatches +
                   ", logicalIdentityMisses=" + logicalIdentityMisses +
                   ", logicalIdentityAmbiguous=" + logicalIdentityAmbiguous +
                   ", scannerRebinds=" + scannerRebinds +
                   ", workListUnavailableDeferrals=" + workListUnavailableDeferrals +
                   ", authorityBypass=" + authorityBypass +
'@ 'logical identity telemetry summary'

$resume = $resume.Replace('V0.9.7 recurring-hot JobGiver continuation deferral slicer.', 'V0.9.8 recurring-hot JobGiver logical-identity continuation slicer.')
$resume = $resume.Replace('[RimMT] V0.9.7 Continuation Deferral installed on ', '[RimMT] V0.9.8 Logical Scanner Identity installed on ')
$resume = $resume.Replace('[RimMT] V0.9.7 Continuation Deferral failed closed: ', '[RimMT] V0.9.8 Logical Scanner Identity failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.7 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.8 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.7:', 'Resumable JobGiver V0.9.8:')
Set-Content $resumePath $resume -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.7-continuation-deferral','0.9.8-logical-scanner-identity')
$boot = $boot.Replace('V0.9.7 Continuation Deferral initialized','V0.9.8 Logical Scanner Identity initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.7 Continuation Deferral on-demand report','V0.9.8 Logical Scanner Identity on-demand report')
$diag = $diag.Replace('V0.9.7 Continuation Deferral; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; pending continuation barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;', 'V0.9.8 Logical Scanner Identity; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; logical scanner identity=WorkGiverDef+type; pending continuation barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.7 Continuation Deferral','V0.9.8 Logical Scanner Identity')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.7 Continuation Deferral','RimMT V0.9.8 Logical Scanner Identity')
$about = [regex]::Replace($about, '<description>.*?</description>', '<description>RimMT V0.9.8 Logical Scanner Identity for RimWorld 1.5. Keeps the V0.9.7 resumable JobGiver architecture, but continuation ownership is now keyed by stable WorkGiverDef plus exact scanner runtime type and rebound to the current work-list instance each package. This removes object-reference identity as a continuation requirement while preserving Vanilla priority, transient eligibility deferral, per-validator time budgets, live final authority, ReachProfile V0.4.18, validated S4 pruners and acceleration-only GenClosest cold sleep. Exact-closure coverage is intentionally unchanged.</description>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.8 Logical Scanner Identity: pending continuations now use WorkGiverDef+runtime-type identity and rebind to current scanner instances; coverage and ReachProfile remain unchanged.'
