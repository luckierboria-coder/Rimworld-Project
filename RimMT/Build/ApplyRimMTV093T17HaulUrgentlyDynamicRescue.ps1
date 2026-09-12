$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T17 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T17 HaulUrgently Dynamic Rescue
# Production child of T16. T17 targets the measured 30-60ms
# AllowTool.WorkGiver_HaulUrgently.PotentialWorkThingsGlobal iterator only.
# The first Vanilla enumeration is recorded in source order; subsequent uses are package-local.
# No validator/Reachability/job result is cached. A 1/64 exact reference/order parity sample
# quarantines the optimization on any mismatch. Foreign source/MoveNext Harmony patches fail open.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t16-genclosest-deep-attribution";' 'internal const string Version = "0.9.3-t17-haulurgently-dynamic-rescue";' 'T17 bootstrap version'
$boot = Replace-OrThrow $boot @'
                JobGiverGlobalNearest04181.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                JobGiverGlobalNearest04181.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);
                HaulUrgentlyDynamicMemo093T17.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'T17 production module install'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T16 GenClosest Deep Attribution initialized. T15 bounded lifecycle retained; temporary infrastructure hooks now attribute GenClosest/Reachability/RegionTraverser calls to WorkGiver caller and non-enumerated source shape; SMF dispatcher census is read-only.' '[RimMT] V0.9.3-T17 HaulUrgently Dynamic Rescue initialized. T16 bounded diagnostics retained; measured AllowTool HaulUrgently dynamic sources now use package-local source-order memoization with sampled parity quarantine; Vanilla validator/Reachability/JobOnThing remain authoritative.' 'T17 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
            sb.AppendLine(GenClosestDeepAttribution093T16.Summary(24));
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
            sb.AppendLine(GenClosestDeepAttribution093T16.Summary(24));
            sb.AppendLine(HaulUrgentlyDynamicMemo093T17.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T17 report production counter line'
$report = $report.Replace('V0.9.3-T16 GenClosest Deep Attribution', 'V0.9.3-T17 HaulUrgently Dynamic Rescue')
$report = Replace-OrThrow $report 'T16 caller/source-shape search attribution; T16 reuses the same temporary 64-package detours and adds no resident profiler; dynamic source enumerables are never consumed and validators are never wrapped; SMF dispatcher coexistence is census-only;' 'T16 caller/source-shape search attribution + T17 package-local HaulUrgently dynamic-source memo; T16 temporary diagnostics remain bounded; T17 records the first Vanilla enumeration in source order, reuses it only inside the same synchronous JobGiver_Work package, samples exact reference/order parity every 64 reuses, and quarantines on mismatch/foreign source patches; no validator/Reachability/job result is cached; SMF dispatcher coexistence remains census-only;' 'T17 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T16 GenClosest Deep Attribution', 'V0.9.3-T17 HaulUrgently Dynamic Rescue')
    $about = $about.Replace('T16 caller/source-shape search attribution.', 'T16 caller/source-shape search attribution plus T17 package-local HaulUrgently dynamic-source rescue.')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T17: package-local source-order memo for AllowTool HaulUrgently dynamic candidates; 1/64 exact parity; foreign patch/mismatch quarantine; Vanilla final authority retained.'
