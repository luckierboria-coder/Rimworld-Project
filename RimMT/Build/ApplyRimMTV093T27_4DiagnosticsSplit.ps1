$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.4 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T27.4 — Diagnostics Split
# Production rule from this version onward:
# - no new diagnostic Harmony patch is added to RimMT.dll;
# - T27.3 WaitStallTrace/patch are removed from RimMT production assembly/call chain;
# - standalone allen.rimmt.diagnostics owns new profiling/telemetry;
# - legacy T0/T1/T2-era observability remains temporarily because several production experiments
#   still consume its counters; migrate those incrementally instead of destabilizing the core.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.3-wait-stall-trace";' 'internal const string Version = "0.9.3-t27.4-diagnostics-split";' 'version'
$boot=Replace-OrThrow $boot @'
                WorkGiverParallelSafety093T27_2.Initialize();
                WaitStallPatches093T27_3.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                WorkGiverParallelSafety093T27_2.Initialize();
                WorldTailBoundary093T22.Apply(harmony);
'@ 'remove production Wait-stall Harmony patch'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T27.3 Wait Stall Trace initialized. T27/T27.1 and T18/T19 source-reordering paths retired; T26.1 zero-wait retained; Wait-result/source telemetry is measurement-only; FullParallel hard-OFF.',
    '[RimMT] V0.9.3-T27.4 Diagnostics Split initialized. T27/T27.1 and T18/T19 source-reordering paths remain retired; T26.1 zero-wait retained; new diagnostics live only in optional allen.rimmt.diagnostics; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27.3 Wait Stall Trace','V0.9.3-T27.4 Diagnostics Split')
$report=$report.Replace('            sb.AppendLine(WaitStallPatches093T27_3.Summary());' + "`r`n",'')
$report=$report.Replace('            sb.AppendLine(WaitStallPatches093T27_3.Summary());' + "`n",'')
$report=$report.Replace('            sb.AppendLine(WaitStallTrace093T27_3.Summary());' + "`r`n",'')
$report=$report.Replace('            sb.AppendLine(WaitStallTrace093T27_3.Summary());' + "`n",'')
$report=$report.Replace(
    'T27.3 retains the T27.2 retirement and additionally retires T18/T19 mobile custom-source rescue after source review found non-Vanilla validator/reach visitation order. Wait-stall telemetry installs one measurement-only DetermineNextJob postfix and records only final ThinkResult/source for player humanlikes; no think tree is rerun and no gameplay state is mutated. The WorkGiver safety registry remains audit-only; FullParallel stays hard-OFF;',
    'T27.4 retains the T27.2/T27.3 behavior resets, but removes the T27.3 Wait tracer from RimMT.dll. New profiling belongs to optional allen.rimmt.diagnostics; legacy observability remains only where older production paths still consume it. The WorkGiver safety registry remains audit-only; FullParallel stays hard-OFF;')
Set-Content $reportPath $report -Encoding UTF8

$projPath='RimMT/Source/RimMT/RimMT.csproj'
$proj=Get-Content $projPath -Raw
$anchor='    <Compile Remove="Diagnostics\ReachabilityPatchCensus.cs" />'
$insert=$anchor
if(-not $proj.Contains('<Compile Remove="Diagnostics\WaitStallTrace093T27_3.cs" />')){ $insert += [Environment]::NewLine + '    <Compile Remove="Diagnostics\WaitStallTrace093T27_3.cs" />' }
if(-not $proj.Contains('<Compile Remove="Patches\WaitStallPatches093T27_3.cs" />')){ $insert += [Environment]::NewLine + '    <Compile Remove="Patches\WaitStallPatches093T27_3.cs" />' }
if($insert -ne $anchor){ $proj=Replace-OrThrow $proj $anchor $insert 'exclude T27.3 wait diagnostics from RimMT.dll' }
Set-Content $projPath $proj -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
    $about=Get-Content $aboutPath -Raw
    $about=$about.Replace('V0.9.3-T27.3 Wait Stall Trace','V0.9.3-T27.4 Diagnostics Split')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.4 Diagnostics Split: T27.3 Wait diagnostics removed from production DLL; new diagnostics are external-only.'
