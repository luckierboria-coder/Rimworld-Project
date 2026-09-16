$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.3 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T27.3 — Wait Stall Trace / Mobile Source Semantics Reset
# - retire T18/T19 MobileSourceRescue production prefixes after code review showed validator-before-reach
#   and nearest-first custom Pawn visitation changes versus Vanilla's reach-before-validator global path;
# - leave T20/T21 transaction foundation and other mature paths untouched for this A/B;
# - install one measurement-only DetermineNextJob postfix that reads final ThinkResult/source only;
# - tracer never reruns ThinkTrees and never mutates Pawn/Job/reservation state.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.2-safety-layer-reset";' 'internal const string Version = "0.9.3-t27.3-wait-stall-trace";' 'version'
$boot=Replace-OrThrow $boot @'
                MobileSourceRescue093T18.Apply(harmony);
                StorytellerDeepAttribution093T18.Initialize();
'@ @'
                // T27.3: retire T18/T19 mobile custom-source rescue. It changes validator/reach
                // visitation semantics for custom Pawn lists; Vanilla GenClosest stays authoritative.
                StorytellerDeepAttribution093T18.Initialize();
'@ 'retire mobile-source rescue'
$boot=Replace-OrThrow $boot @'
                WorkGiverParallelSafety093T27_2.Initialize();
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                WorkGiverParallelSafety093T27_2.Initialize();
                WaitStallPatches093T27_3.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ 'install measurement-only wait tracer'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T27.2 Safety Layer Reset initialized. T27/T27.1 speculative source reordering retired; T26.1 zero-wait retained; WorkGiver parallel safety registry is audit-only; FullParallel hard-OFF.',
    '[RimMT] V0.9.3-T27.3 Wait Stall Trace initialized. T27/T27.1 and T18/T19 source-reordering paths retired; T26.1 zero-wait retained; Wait-result/source telemetry is measurement-only; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27.2 Safety Layer Reset','V0.9.3-T27.3 Wait Stall Trace')
$report=Replace-OrThrow $report @'
            sb.AppendLine(WorkGiverParallelSafety093T27_2.ApiMatrixSummary());
'@ @'
            sb.AppendLine(WorkGiverParallelSafety093T27_2.ApiMatrixSummary());
            sb.AppendLine(WaitStallPatches093T27_3.Summary());
            sb.AppendLine(WaitStallTrace093T27_3.Summary());
'@ 'wait trace report'
$report=$report.Replace(
    'T27.2 retires T27/T27.1 speculative distance/source-order replacement entirely after behavior-risk evidence. No static/custom source is reordered by the T27 kernel. A one-time WorkGiver safety registry inventories core/mod types, foreign Harmony patches and direct-IL API risks; FullParallel is hard-OFF until reservation/reachability/job-pool/Rand/JobFailReason/mod-callback foundations are implemented;',
    'T27.3 retains the T27.2 retirement and additionally retires T18/T19 mobile custom-source rescue after source review found non-Vanilla validator/reach visitation order. Wait-stall telemetry installs one measurement-only DetermineNextJob postfix and records only final ThinkResult/source for player humanlikes; no think tree is rerun and no gameplay state is mutated. The WorkGiver safety registry remains audit-only; FullParallel stays hard-OFF;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
    $about=Get-Content $aboutPath -Raw
    $about=$about.Replace('V0.9.3-T27.2 Safety Layer Reset','V0.9.3-T27.3 Wait Stall Trace')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.3: T18/T19 mobile-source rescue retired; measurement-only Wait-stall DetermineNextJob postfix installed; no gameplay mutation.'
