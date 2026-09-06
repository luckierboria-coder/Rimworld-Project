$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T5 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T5 HaulMerge Patch Census
# Measurement-only follow-up to T4. No optimizer/authority/admission behavior changes.
# T4 remains fully fail-open on foreign Harmony authority. T5 only reports the exact
# owner/priority/patch method on each method that can make T4 authoritySafe=False.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            WorkGiverMergePartnerIndex093T4.Apply(harmony);
'@ @'
            WorkGiverMergePartnerIndex093T4.Apply(harmony);
            HaulMergePatchCensus093T5.Apply();
'@ 'schedule T5 post-load Harmony census'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t4-haulmerge-partner-index";' 'internal const string Version = "0.9.3-t5-haulmerge-patch-census";' 'T5 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T4 HaulMerge Partner Index initialized.' '[RimMT] V0.9.3-T5 HaulMerge Patch Census initialized. T4 behavior unchanged; census is measurement-only.' 'T5 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
'@ @'
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
'@ 'T5 census report line'
$report = Replace-OrThrow $report 'V0.9.3-T4 HaulMerge Partner Index' 'V0.9.3-T5 HaulMerge Patch Census' 'T5 report title'
$report = Replace-OrThrow $report 'T4 package-local HaulMerge partner index; S4 early rescue=OFF;' 'T4 package-local HaulMerge partner index; T5 Harmony authority census=measurement-only; S4 early rescue=OFF;' 'T5 policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T4 HaulMerge Partner Index', 'V0.9.3-T5 HaulMerge Patch Census')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T5: measurement-only post-load/on-demand Harmony census for T4 HaulMerge authority blockers; T4 optimizer behavior unchanged.'
