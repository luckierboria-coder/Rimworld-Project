$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.2 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T27.2 — Safety Layer Reset
# Base: T27.1 Work Plan Window.
# Correctness-first reset after runtime evidence of many Wait pawns with custom-plan consumption:
# - retire the entire T27/T27.1 speculative distance-plan behavior from production;
# - no static/custom source reordering and no hot-path T27 prefixes are installed;
# - retain T26.1 zero-wait + mature T18/T20/T21/T22/T24 paths;
# - add a one-time WorkGiver safety registry/API matrix only;
# - FullParallel stays hard-OFF until explicit thread-safety foundations exist.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.1-work-plan-window";' 'internal const string Version = "0.9.3-t27.2-safety-layer-reset";' 'version'
$boot=Replace-OrThrow $boot @'
                SimulationEpochCoordinator093T26.Apply(harmony);
                ParallelWorkKernel093T27.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                SimulationEpochCoordinator093T26.Apply(harmony);
                // T27/T27.1 speculative source reordering is intentionally retired in T27.2.
                // Do not install ParallelWorkKernel093T27 on any production hot path.
                WorkGiverParallelSafety093T27_2.Initialize();
                WorldTailBoundary093T22.Apply(harmony);
'@ 'retire work kernel and initialize safety registry'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T27.1 Work Plan Window initialized. Source-centric hot-use scheduling replaces package-wide WorkGiver scanning; stable custom IList sources are learned from live calls; plans remain package-local; FullParallel OFF.',
    '[RimMT] V0.9.3-T27.2 Safety Layer Reset initialized. T27/T27.1 speculative source reordering retired; T26.1 zero-wait retained; WorkGiver parallel safety registry is audit-only; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath='RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$runtime=Replace-OrThrow $runtime @'
            FeatureGate.Register(ParallelWorkKernel093T27.FeatureId, true, "T27 speculative persistent-snapshot WorkGiver search plans; FullParallel behavior execution OFF");
'@ @'
            FeatureGate.Register(ParallelWorkKernel093T27.FeatureId, false, "T27/T27.1 speculative source reordering retired in T27.2 after behavior-risk evidence");
            FeatureGate.Register(WorkGiverParallelSafety093T27_2.FeatureId, true, "T27.2 one-time WorkGiver safety/API audit; no worker behavior execution");
'@ 'feature registrations'
$runtime=Replace-OrThrow $runtime @'
            FeatureGate.SetEnabled(ParallelWorkKernel093T27.FeatureId, work);
'@ @'
            FeatureGate.SetEnabled(ParallelWorkKernel093T27.FeatureId, false);
            FeatureGate.SetEnabled(WorkGiverParallelSafety093T27_2.FeatureId, work);
'@ 'feature settings'
Set-Content $runtimePath $runtime -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27.1 Work Plan Window','V0.9.3-T27.2 Safety Layer Reset')
$report=Replace-OrThrow $report @'
            sb.AppendLine(ParallelWorkKernel093T27.Summary());
'@ @'
            sb.AppendLine("T27/T27.1 speculative work-plan kernel: RETIRED/OFF in T27.2; no source reordering is installed on production JobGiver/GenClosest paths.");
            sb.AppendLine(WorkGiverParallelSafety093T27_2.Summary());
            sb.AppendLine(WorkGiverParallelSafety093T27_2.ApiMatrixSummary());
'@ 'replace kernel report with safety report'
$report=$report.Replace(
    'T27.1 replaces package-wide WorkGiver classification with source-centric hot-use scheduling, learns only stable live IList sources (including custom lists), never materializes unknown IEnumerable, and rebuilds every root-specific distance plan inside the current synchronous package. Only source-use history crosses packages; FullParallel remains OFF;',
    'T27.2 retires T27/T27.1 speculative distance/source-order replacement entirely after behavior-risk evidence. No static/custom source is reordered by the T27 kernel. A one-time WorkGiver safety registry inventories core/mod types, foreign Harmony patches and direct-IL API risks; FullParallel is hard-OFF until reservation/reachability/job-pool/Rand/JobFailReason/mod-callback foundations are implemented;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
    $about=Get-Content $aboutPath -Raw
    $about=$about.Replace('V0.9.3-T27.1 Work Plan Window','V0.9.3-T27.2 Safety Layer Reset')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.2 Safety Layer Reset: T27/T27.1 production source reordering retired; one-time WorkGiver safety/API audit installed; FullParallel hard-OFF.'
