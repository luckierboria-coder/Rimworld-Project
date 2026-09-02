$ErrorActionPreference = 'Stop'

$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfiles.cs'
$text = Get-Content $path -Raw

$old = 'private const int SampleMask = 15; // 1/16 after warmup.'
$new = 'private const int SampleMask = 127; // Unified Lean: 1/128 after warmup; fuse probation still forces live validation.'

if (-not $text.Contains($old)) {
    throw "Unified Lean transform anchor not found in $path. Refusing to build an unverified ReachProfile variant."
}

$text = $text.Replace($old, $new)
Set-Content $path $text -Encoding UTF8
Write-Host 'Applied Unified Lean ReachProfile shadow cadence: 1/16 -> 1/128.'
