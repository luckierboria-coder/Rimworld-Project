$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.1 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T27.1 — Source-Centric Work Plan Window
# Base: T27 Parallel Work Kernel.
# Historical guardrails:
# - no cross-package DistancePlan/result cache (T25 failure mode stays banned);
# - no same-call worker waits/spins/joins (T26 failure mode stays banned);
# - no WorkGiver-list-wide speculative classification tax;
# - no dynamic IEnumerable materialization;
# - only actual-use history crosses package boundaries; each root-specific plan is rebuilt and package-local.

$kernelPath='RimMT/Source/RimMT/AI/ParallelWorkKernel093T27.cs'
$kernel=Get-Content $kernelPath -Raw
foreach($marker in @(
    'T27.1 source-centric parallel Work Search Kernel',
    'crossPackagePlanCache=OFF',
    'PreheatRecentlyUsedSources',
    'customGlobalSearchSet as IList',
    'HotWindowPackages = 48',
    'fullParallel=OFF, waits=0')){
    if(-not $kernel.Contains($marker)){ throw "T27.1 kernel marker missing: $marker" }
}
if($kernel.Contains('WorkGiversInOrderNormal') -or $kernel.Contains('WorkGiversInOrderEmergency') -or $kernel.Contains('ClassifySnapshotSource')){
    throw 'T27.1 regression: package-start WorkGiver list scan/classifier returned'
}

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27-parallel-work-kernel";' 'internal const string Version = "0.9.3-t27.1-work-plan-window";' 'version'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T27 Parallel Work Kernel initialized. T26.1 zero-wait retained; persistent snapshots now feed speculative worker-built WorkGiver search plans; FullParallel WorkGiver behavior execution remains OFF.',
    '[RimMT] V0.9.3-T27.1 Work Plan Window initialized. Source-centric hot-use scheduling replaces package-wide WorkGiver scanning; stable custom IList sources are learned from live calls; plans remain package-local; FullParallel OFF.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27 Parallel Work Kernel','V0.9.3-T27.1 Work Plan Window')
$report=$report.Replace(
    'T27 adds bottom-level WorkGiver parallel classification plus speculative persistent-snapshot distance/source-order plans. Workers never execute WorkGiver/validator/Reachability/reservation/Job code; FullParallel is reserved/OFF;',
    'T27.1 replaces package-wide WorkGiver classification with source-centric hot-use scheduling, learns only stable live IList sources (including custom lists), never materializes unknown IEnumerable, and rebuilds every root-specific distance plan inside the current synchronous package. Only source-use history crosses packages; FullParallel remains OFF;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
    $about=Get-Content $aboutPath -Raw
    $about=$about.Replace('V0.9.3-T27 Parallel Work Kernel','V0.9.3-T27.1 Work Plan Window')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.1 Work Plan Window: source-centric hot-use scheduling; stable custom IList learning; package-local plans; zero-wait and FullParallel OFF.'
