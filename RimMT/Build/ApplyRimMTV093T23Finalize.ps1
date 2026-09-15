$ErrorActionPreference = 'Stop'

$reachPath = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach = Get-Content $reachPath -Raw

# Cosmetic metric-label normalization only. This is idempotent.
$reach = $reach.Replace('", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +'.Replace('\"','"'),
                       '", avgProfileCaptureWorkUs=" + avgCaptureUs.ToString("F2") +'.Replace('\"','"'))
$reach = $reach.Replace('", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +'.Replace('\"','"'),
                       '", maxProfileCaptureSliceUs=" + maxCaptureUs.ToString("F2") +'.Replace('\"','"'))
Set-Content $reachPath $reach -Encoding UTF8

# Foundation III generates GenClosestTransactionIndex093T22.cs at build time. JobIssueParams
# is Verse.AI.JobIssueParams in RimWorld 1.5; the original generator imported Verse but not
# Verse.AI, which breaks a clean compile even though the runtime design itself is valid.
$genPath = 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
if (!(Test-Path $genPath)) { throw "T23 finalize: missing generated GenClosest source: $genPath" }
$gen = Get-Content $genPath -Raw
if ($gen -notmatch '(?m)^using Verse\.AI;\s*$')
{
    if ($gen -notmatch '(?m)^using Verse;\s*$') { throw 'T23 finalize: GenClosest using Verse anchor missing.' }
    $gen = [regex]::Replace($gen, '(?m)^using Verse;\s*$', "using Verse;`r`nusing Verse.AI;", 1)
    Set-Content $genPath $gen -Encoding UTF8
}

Write-Host 'Finalized T23 generated sources: ReachProfile labels normalized; Verse.AI imported for JobIssueParams.'
