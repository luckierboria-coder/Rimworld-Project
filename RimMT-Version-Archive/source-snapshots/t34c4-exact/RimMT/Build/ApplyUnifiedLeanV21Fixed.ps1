$ErrorActionPreference = 'Stop'

$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$text = Get-Content $path -Raw

$old = '        internal sealed class ProfileCaptureWorkItem'
$new = '        private sealed class ProfileCaptureWorkItem'
if (-not $text.Contains($old)) { throw 'V21 ProfileCaptureWorkItem accessibility anchor not found.' }
$text = $text.Replace($old, $new)

$deadFields = @(
    'profileCaptureBudgetFrame',
    'profileCaptureBudgetSpentTicks',
    'hardRefreshGraceQueries',
    'hardRefreshGraceShadowSamples',
    'hardRefreshGraceAuthoritative',
    'hardRefreshGraceExpired'
)
foreach ($field in $deadFields) {
    $pattern = '(?m)^\s*private static long ' + [regex]::Escape($field) + '(?:\s*=\s*[^;]+)?;\r?\n'
    if (-not [regex]::IsMatch($text, $pattern)) {
        throw "V21 retired field anchor not found: $field"
    }
    $text = [regex]::Replace($text, $pattern, '', 1)
}

Set-Content $path $text -Encoding UTF8
Write-Host 'Applied Unified Lean V21 repair: private capture work item; removed retired V20 grace and V19 budget fields.'