$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.9 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

function Regex-Replace-OrThrow {
    param([string]$Text,[string]$Pattern,[string]$Replacement,[string]$Label)
    $rx = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $rx.IsMatch($Text)) { throw "RimMT V0.9.9 regex anchor not found: $Label" }
    return $rx.Replace($Text, $Replacement, 1)
}

# RimMT V0.9.9 Package Context Continuation
# - pending continuation is owned by the exact JobGiver_Work package instance + emergency lane
# - only the owner package may rebind/resume the WorkGiverDef+runtime-type scanner identity
# - Emergency may preempt a pending Normal continuation
# - Normal cannot pass an unfinished Emergency continuation
# - unrelated same-lane JobGiver_Work packages do not destroy or steal pending state
# - exact-closure coverage, slice budgets and ReachProfile V0.4.18 remain unchanged

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        [ThreadStatic] private static int pendingListIndex;
'@ @'
        [ThreadStatic] private static int pendingListIndex;
        [ThreadStatic] private static JobGiver_Work currentPackageOwner;
        [ThreadStatic] private static bool currentPackageEmergency;
        [ThreadStatic] private static bool blockCurrentPackageForPendingEmergency;
'@ 'package context thread locals'

$resume = Replace-OrThrow $resume @'
        private static long workListUnavailableDeferrals;
        private static long authorityBypass;
'@ @'
        private static long workListUnavailableDeferrals;
        private static long packageContextMatches;
        private static long packageContextOther;
        private static long emergencyLanePreemptions;
        private static long normalLaneBlocks;
        private static long sameLanePackageDeferrals;
        private static long pendingOtherPackageBypass;
        private static long packageOwnerInvalidations;
        private static long authorityBypass;
'@ 'package context counters'

$packagePrefixPattern = '(?s)        public static void PackagePrefix\(JobGiver_Work __instance, Pawn __0\)\r?\n        \{.*?\r?\n        \}\r?\n\r?\n        public static Exception PackageFinalizer'
$packagePrefixReplacement = @'
        public static void PackagePrefix(JobGiver_Work __instance, Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            currentWorkList = null;
            pendingListIndex = -1;
            currentPackageOwner = __instance;
            currentPackageEmergency = false;
            blockCurrentPackageForPendingEmergency = false;

            try { currentPackageEmergency = __instance != null && __instance.emergency; }
            catch { currentPackageEmergency = false; }

            if (States.Count > MaxStates)
                PurgeInvalidStates();

            if (__0 == null) return;

            ResumeState pending;
            if (!States.TryGetValue(__0, out pending) || pending == null) return;

            if (pending.ScannerDef == null || pending.ScannerType == null || pending.PackageOwner == null)
            {
                States.Remove(__0);
                pendingEligibilityInvalidations++;
                packageOwnerInvalidations++;
                return;
            }

            bool samePackage = ReferenceEquals(pending.PackageOwner, __instance) &&
                               pending.PackageEmergency == currentPackageEmergency;
            if (!samePackage)
            {
                packageContextOther++;

                // Preserve Vanilla package priority across continuation boundaries.
                // Emergency work may always preempt a pending Normal continuation.
                if (!pending.PackageEmergency && currentPackageEmergency)
                {
                    emergencyLanePreemptions++;
                    return;
                }

                // An unfinished Emergency continuation remains above Normal work.
                // Block this Normal JobGiver_Work package without touching the pending state.
                if (pending.PackageEmergency && !currentPackageEmergency)
                {
                    blockCurrentPackageForPendingEmergency = true;
                    normalLaneBlocks++;
                    return;
                }

                // Multiple JobGiver_Work nodes can theoretically share a lane. Their relative ThinkTree
                // priority is not inferred here; leave the unrelated package Vanilla and retain state.
                sameLanePackageDeferrals++;
                return;
            }

            packageContextMatches++;

            try
            {
                currentWorkList = currentPackageEmergency
                    ? __0.workSettings.WorkGiversInOrderEmergency
                    : __0.workSettings.WorkGiversInOrderNormal;
            }
            catch
            {
                currentWorkList = null;
            }

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

            // Only the owner package is allowed to conclude that its logical scanner disappeared.
            States.Remove(__0);
            pendingScannerForPackage = null;
            pendingListIndex = -1;
            pendingEligibilityInvalidations++;
            if (logicalIndex == -2) logicalIdentityAmbiguous++;
            else logicalIdentityMisses++;
        }

        public static Exception PackageFinalizer
'@
$resume = Regex-Replace-OrThrow $resume $packagePrefixPattern $packagePrefixReplacement 'package-context PackagePrefix'

$finalizerPattern = '(?s)        public static Exception PackageFinalizer\(Exception __exception\)\r?\n        \{.*?\r?\n        \}\r?\n\r?\n        public static void PawnCanUsePostfix'
$finalizerReplacement = @'
        public static Exception PackageFinalizer(Exception __exception)
        {
            currentPawn = null;
            suspendedThisPackage = false;
            pendingScannerForPackage = null;
            currentWorkList = null;
            pendingListIndex = -1;
            currentPackageOwner = null;
            currentPackageEmergency = false;
            blockCurrentPackageForPendingEmergency = false;
            return __exception;
        }

        public static void PawnCanUsePostfix
'@
$resume = Regex-Replace-OrThrow $resume $finalizerPattern $finalizerReplacement 'package finalizer clears context'

$resume = Replace-OrThrow $resume @'
            if (currentPawn == null || !ReferenceEquals(__0, currentPawn)) return;

            if (suspendedThisPackage)
'@ @'
            if (currentPawn == null || !ReferenceEquals(__0, currentPawn)) return;

            if (blockCurrentPackageForPendingEmergency)
            {
                __result = false;
                priorityBlocks++;
                return;
            }

            if (suspendedThisPackage)
'@ 'Normal package blocked behind pending Emergency continuation'

$resume = Replace-OrThrow $resume @'
            ResumeState existingAny;
            States.TryGetValue(pawn, out existingAny);
            bool hasPendingForScanner = existingAny != null && SameScannerIdentity(existingAny, scanner);
            if (hasPendingForScanner && !ReferenceEquals(existingAny.Scanner, scanner))
'@ @'
            ResumeState existingAny;
            States.TryGetValue(pawn, out existingAny);
            bool samePackageContext = existingAny != null && SamePackageContext(existingAny);
            if (existingAny != null && !samePackageContext)
            {
                // Another JobGiver_Work package may preempt according to PackagePrefix, but it must
                // never consume, replace or invalidate this package's continuation slot.
                pendingOtherPackageBypass++;
                return true;
            }

            bool hasPendingForScanner = samePackageContext && SameScannerIdentity(existingAny, scanner);
            if (hasPendingForScanner && !ReferenceEquals(existingAny.Scanner, scanner))
'@ 'ClosestPrefix requires package-context ownership'

$resume = Replace-OrThrow $resume @'
            if (existing != null && SameScannerIdentity(existing, scanner))
            {
'@ @'
            if (existing != null && SamePackageContext(existing) && SameScannerIdentity(existing, scanner))
            {
'@ 'resume requires same package context'

$resume = Replace-OrThrow $resume @'
        private static bool SameScannerIdentity(ResumeState state, WorkGiver scanner)
'@ @'
        private static bool SamePackageContext(ResumeState state)
        {
            if (state == null || state.PackageOwner == null || currentPackageOwner == null) return false;
            return ReferenceEquals(state.PackageOwner, currentPackageOwner) &&
                   state.PackageEmergency == currentPackageEmergency;
        }

        private static bool SameScannerIdentity(ResumeState state, WorkGiver scanner)
'@ 'package-context identity helper'

$resume = Replace-OrThrow $resume @'
                if (pawn != null && States.TryGetValue(pawn, out pending) && pending != null &&
                    (scanner == null || SameScannerIdentity(pending, scanner)))
                    States.Remove(pawn);
'@ @'
                if (pawn != null && States.TryGetValue(pawn, out pending) && pending != null &&
                    SamePackageContext(pending) && (scanner == null || SameScannerIdentity(pending, scanner)))
                    States.Remove(pawn);
'@ 'pending drop cannot destroy another package continuation'

$resume = Replace-OrThrow $resume @'
                    ScannerDef = scanner == null ? null : scanner.def,
                    ScannerType = scanner == null ? null : scanner.GetType(),
                    Map = map,
'@ @'
                    ScannerDef = scanner == null ? null : scanner.def,
                    ScannerType = scanner == null ? null : scanner.GetType(),
                    PackageOwner = currentPackageOwner,
                    PackageEmergency = currentPackageEmergency,
                    Map = map,
'@ 'state captures package owner and lane'

$resume = Replace-OrThrow $resume @'
            if (state == null || !ReferenceEquals(state.Pawn, pawn) || !ReferenceEquals(state.Map, map) ||
                state.Root != root || state.Members == null)
'@ @'
            if (state == null || !SamePackageContext(state) || !ReferenceEquals(state.Pawn, pawn) ||
                !ReferenceEquals(state.Map, map) || state.Root != root || state.Members == null)
'@ 'state validation requires package context'

$resume = Replace-OrThrow $resume @'
            internal WorkGiverDef ScannerDef;
            internal Type ScannerType;
            internal Map Map;
'@ @'
            internal WorkGiverDef ScannerDef;
            internal Type ScannerType;
            internal JobGiver_Work PackageOwner;
            internal bool PackageEmergency;
            internal Map Map;
'@ 'resume state stores package context'

$resume = Replace-OrThrow $resume @'
                   ", workListUnavailableDeferrals=" + workListUnavailableDeferrals +
                   ", authorityBypass=" + authorityBypass +
'@ @'
                   ", workListUnavailableDeferrals=" + workListUnavailableDeferrals +
                   ", packageContextMatches=" + packageContextMatches +
                   ", packageContextOther=" + packageContextOther +
                   ", emergencyLanePreemptions=" + emergencyLanePreemptions +
                   ", normalLaneBlocks=" + normalLaneBlocks +
                   ", sameLanePackageDeferrals=" + sameLanePackageDeferrals +
                   ", pendingOtherPackageBypass=" + pendingOtherPackageBypass +
                   ", packageOwnerInvalidations=" + packageOwnerInvalidations +
                   ", authorityBypass=" + authorityBypass +
'@ 'package context summary counters'

$resume = $resume.Replace('V0.9.8 recurring-hot JobGiver logical-identity continuation slicer.', 'V0.9.9 recurring-hot JobGiver package-context continuation slicer.')
$resume = $resume.Replace('[RimMT] V0.9.8 Logical Scanner Identity installed on ', '[RimMT] V0.9.9 Package Context Continuation installed on ')
$resume = $resume.Replace('[RimMT] V0.9.8 Logical Scanner Identity failed closed: ', '[RimMT] V0.9.9 Package Context Continuation failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.8 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.9 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.8:', 'Resumable JobGiver V0.9.9:')
Set-Content $resumePath $resume -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.8-logical-scanner-identity','0.9.9-package-context-continuation')
$boot = $boot.Replace('V0.9.8 Logical Scanner Identity initialized','V0.9.9 Package Context Continuation initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.8 Logical Scanner Identity on-demand report','V0.9.9 Package Context Continuation on-demand report')
$diag = $diag.Replace('V0.9.8 Logical Scanner Identity; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; logical scanner identity=WorkGiverDef+type; pending continuation barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;', 'V0.9.9 Package Context Continuation; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; package context=JobGiver_Work+lane; logical scanner identity=WorkGiverDef+type; cross-lane priority barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.8 Logical Scanner Identity','V0.9.9 Package Context Continuation')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.8 Logical Scanner Identity','RimMT V0.9.9 Package Context Continuation')
$about = [regex]::Replace($about, '<description>.*?</description>', '<description>RimMT V0.9.9 Package Context Continuation for RimWorld 1.5. Pending resumable JobGiver state is now owned by the exact JobGiver_Work package instance plus its emergency lane, while scanner identity remains stable WorkGiverDef plus exact runtime type. Only the owner package may rebind and resume its scanner. Emergency work may preempt a pending Normal continuation, while Normal work cannot pass an unfinished Emergency continuation. Unrelated same-lane packages never destroy or steal state. Exact-closure coverage, per-validator slice budgets, live final Vanilla authority, ReachProfile V0.4.18, validated S4 pruners and acceleration-only GenClosest cold sleep remain unchanged.</description>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.9 Package Context Continuation: state ownership now includes exact JobGiver_Work package + emergency lane; cross-lane priority is preserved; logical scanner rebind only occurs in the owner package; coverage and ReachProfile stay unchanged.'