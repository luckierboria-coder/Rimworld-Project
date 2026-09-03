$ErrorActionPreference = 'Stop'

$path = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$text = Get-Content $path -Raw
$old = '        internal sealed class ProfileCaptureWorkItem'
$new = '        private sealed class ProfileCaptureWorkItem'
if (-not $text.Contains($old)) { throw 'V21 ProfileCaptureWorkItem accessibility anchor not found.' }
$text = $text.Replace($old, $new)
Set-Content $path $text -Encoding UTF8
Write-Host 'Applied Unified Lean V21 accessibility repair: ProfileCaptureWorkItem is private to the ReachProfile implementation.'