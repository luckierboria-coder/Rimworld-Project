$ErrorActionPreference = 'Stop'
$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$text = Get-Content $path -Raw
$oldAvg = '", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +'
$oldMax = '", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +'
# PowerShell literals above intentionally use C# quote characters only after normalization below.
$oldAvg = $oldAvg.Replace('\"','"')
$oldMax = $oldMax.Replace('\"','"')
$newAvg = '", avgProfileCaptureWorkUs=" + avgCaptureUs.ToString("F2") +'.Replace('\"','"')
$newMax = '", maxProfileCaptureSliceUs=" + maxCaptureUs.ToString("F2") +'.Replace('\"','"')
if (-not $text.Contains($oldAvg)) { throw 'T23 finalize: avgProfileCaptureUs anchor missing.' }
if (-not $text.Contains($oldMax)) { throw 'T23 finalize: maxProfileCaptureUs anchor missing.' }
$text = $text.Replace($oldAvg, $newAvg).Replace($oldMax, $newMax)
Set-Content $path $text -Encoding UTF8
Write-Host 'Finalized T23 ReachProfile capture metric labels.'
