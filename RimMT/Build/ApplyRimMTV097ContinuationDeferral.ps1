$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.7 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.7 Continuation Deferral
# - fixes V0.9.6 transient eligibility false from destroying pending continuation state
# - a temporarily ineligible pending scanner releases only the current package barrier; state remains bounded
# - the next eligible package resumes the exact scanner/state
# - durable invalidations (work-list removal, source/state mismatch, age, authority loss, exception) still fail open
# - no new WorkGiver-specific optimization; ReachProfile V0.4.18 remains unchanged

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        private static long pendingEligibilityInvalidations;
        private static long authorityBypass;
'@ @'
        private static long pendingEligibilityInvalidations;
        private static long eligibilityDeferrals;
        private static long resumeEligibleAttempts;
        private static long resumeSuccesses;
        private static long stateExpired;
        private static long authorityBypass;
'@ 'deferral lifecycle counters'

$resume = Replace-OrThrow $resume @'
            if (ReferenceEquals(__1, pendingScannerForPackage))
            {
                if (!__result)
                    DropPendingContinuation(__0, pendingScannerForPackage, true, false);
                return;
            }
'@ @'
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
'@ 'transient eligibility becomes deferral instead of invalidation'

$resume = Replace-OrThrow $resume @'
                else
                {
                    state = existing;
                    resumes++;
                }
'@ @'
                else
                {
                    state = existing;
                    resumes++;
                    resumeSuccesses++;
                }
'@ 'successful continuation resume attribution'

$resume = Replace-OrThrow $resume @'
            if (now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
            {
                staleInvalidations++;
                return false;
            }
'@ @'
            if (now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
            {
                staleInvalidations++;
                stateExpired++;
                return false;
            }
'@ 'state expiration attribution'

$resume = Replace-OrThrow $resume @'
                   ", pendingEligibilityInvalidations=" + pendingEligibilityInvalidations +
                   ", authorityBypass=" + authorityBypass +
'@ @'
                   ", pendingEligibilityInvalidations=" + pendingEligibilityInvalidations +
                   ", eligibilityDeferrals=" + eligibilityDeferrals +
                   ", resumeEligibleAttempts=" + resumeEligibleAttempts +
                   ", resumeSuccesses=" + resumeSuccesses +
                   ", stateExpired=" + stateExpired +
                   ", authorityBypass=" + authorityBypass +
'@ 'summary continuation lifecycle counters'

$resume = $resume.Replace('V0.9.6 recurring-hot JobGiver continuation slicer.', 'V0.9.7 recurring-hot JobGiver continuation deferral slicer.')
$resume = $resume.Replace('[RimMT] V0.9.6 Continuation Fix installed on ', '[RimMT] V0.9.7 Continuation Deferral installed on ')
$resume = $resume.Replace('[RimMT] V0.9.6 Continuation Fix failed closed: ', '[RimMT] V0.9.7 Continuation Deferral failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.6 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.7 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.6:', 'Resumable JobGiver V0.9.7:')
Set-Content $resumePath $resume -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.6-continuation-fix','0.9.7-continuation-deferral')
$boot = $boot.Replace('V0.9.6 Continuation Fix initialized','V0.9.7 Continuation Deferral initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.6 Continuation Fix on-demand report','V0.9.7 Continuation Deferral on-demand report')
$diag = $diag.Replace('V0.9.6 Continuation Fix; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; pending continuation barrier=ON; atomic validator tails=ON;', 'V0.9.7 Continuation Deferral; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; pending continuation barrier=ON; transient eligibility deferral=ON; atomic validator tails=ON;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.6 Continuation Fix','V0.9.7 Continuation Deferral')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.6 Continuation Fix','RimMT V0.9.7 Continuation Deferral')
$about = $about.Replace('Fixes per-pawn continuation ownership with a priority-preserving pending scanner barrier, checks the time budget before every candidate validator, records atomic validator tails, and releases stale/unsupported continuations before Vanilla fallback.', 'Defers transiently ineligible pending scanners without discarding their continuation state; the per-pawn priority barrier is released only for that package and restored on a later eligible package. Time budgets are checked before every candidate validator, atomic validator tails are recorded, and durable invalidations still fail open to Vanilla.')
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.7 Continuation Deferral: transient PawnCanUse=false now defers without deleting state; exact pending continuation resumes on later eligible packages; durable invalidation remains fail-open; ReachProfile stays V0.4.18.'
