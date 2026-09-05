$ErrorActionPreference = 'Stop'
$path = 'RimMT/Source/RimMT/Diagnostics/JobGiverTailTelemetry094.cs'
$text = Get-Content $path -Raw
$old = '        private struct TailCallState'
$new = '        public struct TailCallState'
if (-not $text.Contains($old)) { throw 'RimMT V0.9.11 compile-fix anchor not found: TailCallState accessibility' }
$text = $text.Replace($old, $new)
Set-Content $path $text -Encoding UTF8
Write-Host 'Applied RimMT V0.9.11 compile fix: Harmony public patch methods now expose an equally accessible TailCallState.'
