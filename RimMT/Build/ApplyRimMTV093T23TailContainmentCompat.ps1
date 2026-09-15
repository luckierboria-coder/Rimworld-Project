$ErrorActionPreference = 'Stop'

$reachPath = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$text = Get-Content $reachPath -Raw
$match = [regex]::Match($text, 'private const int SliceCheckMask = (?<n>\d+);')
if (-not $match.Success) { throw 'T23 compat: SliceCheckMask declaration not found.' }
$original = $match.Groups['n'].Value

# The T23 transform was authored against the pre-Unified-Lean value (63). Normalize only
# the textual anchor while it runs, then restore the already-proven current mask value.
if ($original -ne '63')
{
    $text = [regex]::Replace($text,
        'private const int SliceCheckMask = \d+;',
        'private const int SliceCheckMask = 63;', 1)
    Set-Content $reachPath $text -Encoding UTF8
}

try
{
    & ./RimMT/Build/ApplyRimMTV093T23TailContainment.ps1
}
finally
{
    if ($original -ne '63' -and (Test-Path $reachPath))
    {
        $after = Get-Content $reachPath -Raw
        $after = $after.Replace('private const int SliceCheckMask = 63;',
            'private const int SliceCheckMask = ' + $original + ';')
        Set-Content $reachPath $after -Encoding UTF8
    }
}

Write-Host "Applied T23 compatibility wrapper; restored SliceCheckMask=$original."
