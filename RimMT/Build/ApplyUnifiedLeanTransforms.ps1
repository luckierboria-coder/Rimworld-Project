$ErrorActionPreference = 'Stop'

$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfiles.cs'
$text = Get-Content $path -Raw

$sampleOld = 'private const int SampleMask = 15; // 1/16 after warmup.'
$sampleNew = 'private const int SampleMask = 127; // Unified Lean: 1/128 after warmup; fuse probation still forces live validation.'
$fieldOld = '[ThreadStatic] private static int bypassDepth;'
$guardOld = 'if (bypassDepth != 0 || !compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||'
$guardNew = 'if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||'

if (-not $text.Contains($sampleOld)) {
    throw "Unified Lean transform anchor not found for ReachProfile sample cadence in $path."
}
if (-not $text.Contains($fieldOld)) {
    throw "Unified Lean transform anchor not found for dead bypassDepth field in $path."
}
if (-not $text.Contains($guardOld)) {
    throw "Unified Lean transform anchor not found for bypassDepth guard in $path."
}

$text = $text.Replace($sampleOld, $sampleNew)
$text = $text.Replace($fieldOld, '')
$text = $text.Replace($guardOld, $guardNew)
Set-Content $path $text -Encoding UTF8

Write-Host 'Applied Unified Lean ReachProfile transforms: shadow cadence 1/16 -> 1/128; removed never-assigned bypassDepth dead guard.'
