$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "RimMT V0.9.3 consolidated transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# Internal development codename: V26 Consolidated Stable.
# Public release version: RimMT V0.9.3.
# Production composition is exactly:
#   V22 stable core / ReachProfile V0.4.18
#   + V23 authority-safe HaulCorpses / HoldingPlatform pruning
#   + V24 truthful S4 validator telemetry + FeedHemogen / VisitSick pruning
# V25 JobGiver validator memo and V19-V21 ReachProfile experiments are NOT in production.

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag '[RimMT] V0.9.2 Unified Lean on-demand report' '[RimMT] V0.9.3 Consolidated Stable on-demand report' 'public report version'
$oldPolicy = 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;'
$newPolicy = 'V0.9.3 Consolidated Stable; S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners; V25 validator memo=OFF;'
$diag = Replace-OrThrow $diag $oldPolicy $newPolicy 'production policy marker'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied RimMT V0.9.3 Consolidated Stable marker: V22 core + V23/V24 proven S4 pruners; V25 validator memo OFF; ReachProfile remains V0.4.18.'
