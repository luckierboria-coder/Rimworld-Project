$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'ApplyUnifiedLeanV19Transforms.ps1'
$tempPath = Join-Path $env:RUNNER_TEMP 'ApplyUnifiedLeanV19Transforms.fixed.ps1'
$text = Get-Content $sourcePath -Raw

$bad = @'
    $second = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline, $match.Index + $match.Length)
    if ($second.Success) {
        throw "Unified Lean V19 regex anchor matched more than once: $Label"
    }
'@

if (-not $text.Contains($bad)) {
    throw 'V19 transform runner repair anchor not found.'
}
$text = $text.Replace($bad, '')

# ProfileSlot is internal, so its generated capture-state field requires an internal state type.
$privateCapture = '        private sealed class ProfileCaptureState'
$internalCapture = '        internal sealed class ProfileCaptureState'
if (-not $text.Contains($privateCapture)) {
    throw 'V19 ProfileCaptureState accessibility repair anchor not found.'
}
$text = $text.Replace($privateCapture, $internalCapture)

# TopologySnapshot exists in the generated C# source, not in the V19 script replacement body.
# Align it with ProfileCaptureState after the V18 transform and before executing V19.
$reachPath = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach = Get-Content $reachPath -Raw
$privateTopology = '        private sealed class TopologySnapshot'
$internalTopology = '        internal sealed class TopologySnapshot'
if (-not $reach.Contains($privateTopology)) {
    throw 'V19 generated TopologySnapshot accessibility repair anchor not found.'
}
$reach = $reach.Replace($privateTopology, $internalTopology)
Set-Content $reachPath $reach -Encoding UTF8

Set-Content $tempPath $text -Encoding UTF8
& $tempPath
