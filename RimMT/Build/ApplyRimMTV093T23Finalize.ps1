$ErrorActionPreference = 'Stop'
$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$text = Get-Content $path -Raw

# Cosmetic metric-label normalization only. This step must be idempotent and must never
# block a build when an earlier transform already changed the summary wording.
$text = $text.Replace('", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +'.Replace('\"','"'),
                     '", avgProfileCaptureWorkUs=" + avgCaptureUs.ToString("F2") +'.Replace('\"','"'))
$text = $text.Replace('", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +'.Replace('\"','"'),
                     '", maxProfileCaptureSliceUs=" + maxCaptureUs.ToString("F2") +'.Replace('\"','"'))
Set-Content $path $text -Encoding UTF8
Write-Host 'Finalized T23 ReachProfile capture metric labels (idempotent).'
