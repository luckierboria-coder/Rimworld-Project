$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V26 consolidated transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V26 Consolidated Stable intentionally changes no optimization semantics.
# Production composition is exactly:
#   V22 stable core / ReachProfile V0.4.18
#   + V23 authority-safe HaulCorpses / HoldingPlatform pruning
#   + V24 truthful S4 validator telemetry + FeedHemogen / VisitSick pruning
# V25 JobGiver validator memo and V19-V21 ReachProfile experiments are NOT in production.

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$oldPolicy = 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;'
$newPolicy = 'V26 Consolidated Stable; S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners; V25 validator memo=OFF;'
$diag = Replace-OrThrow $diag $oldPolicy $newPolicy 'V26 production policy marker'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V26 Consolidated Stable marker: V22 core + V23/V24 proven S4 pruners; V25 validator memo OFF; ReachProfile remains V0.4.18.'
