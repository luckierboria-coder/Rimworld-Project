$ErrorActionPreference = 'Stop'

$path = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$text = Get-Content $path -Raw
$old = @'
            if (now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
            {
                staleInvalidations++;
                return false;
            }
'@
$new = @'
            if (now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
            {
                staleInvalidations++;
                stateExpired++;
                return false;
            }
'@
if (-not $text.Contains($old)) { throw 'RimMT V0.9.10 telemetry-fix anchor not found: ValidateState expiry block' }
$text = $text.Replace($old, $new)
Set-Content $path $text -Encoding UTF8
Write-Host 'Applied RimMT V0.9.10 telemetry fix: stateExpired is incremented independently from sourceInvalidations.'