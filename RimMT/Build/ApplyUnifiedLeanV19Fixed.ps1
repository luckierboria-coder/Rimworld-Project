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

$privateCapture = '        private sealed class ProfileCaptureState'
$internalCapture = '        internal sealed class ProfileCaptureState'
if (-not $text.Contains($privateCapture)) {
    throw 'V19 ProfileCaptureState accessibility repair anchor not found.'
}
$text = $text.Replace($privateCapture, $internalCapture)

Set-Content $tempPath $text -Encoding UTF8
& $tempPath
