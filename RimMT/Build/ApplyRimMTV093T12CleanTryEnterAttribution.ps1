$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T12 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T12 Clean TryEnter Attribution
# Clean child of T8. T9/T10/T11 are intentionally not in the build chain.
# Adds only bounded helper timing around TryEnterNextPathCell in existing T2 deep windows.
# No path result, movement state, Harmony owner order, filth/snow/door state, or Job is changed.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPathfinderPatches093T3.Apply(harmony);
'@ @'
            TailPathfinderPatches093T3.Apply(harmony);
            TryEnterCleanPatches093T12.Apply(harmony);
'@ 'install T12 clean TryEnter probes after existing bounded T3 FindPath probe'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t8-t5-carrier-pruners";' 'internal const string Version = "0.9.3-t12-clean-tryenter-attribution";' 'T12 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T8 T5 Carrier Pruners initialized. T5 baseline retained; T6 HaulMerge CommonSense coexistence is absent; S4-only authority-safe carrier/mech cheap negatives enabled.' '[RimMT] V0.9.3-T12 Clean TryEnter Attribution initialized. T8 production behavior retained; T9/T10/T11 absent; bounded TryEnter stage probes active only inside existing T2 deep windows.' 'T12 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
'@ @'
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
            sb.AppendLine(TryEnterCleanPatches093T12.Summary());
            sb.AppendLine(TryEnterCleanAttribution093T12.Summary());
            sb.AppendLine(TryEnterCleanAttribution093T12.Recent10Summary());
            sb.AppendLine(TryEnterCleanAttribution093T12.Recent20Summary());
'@ 'T12 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T8 T5 Carrier Pruners' 'V0.9.3-T12 Clean TryEnter Attribution' 'T12 report title'
$report = Replace-OrThrow $report 'T8 clean T5 rebase with S4-only carrier/mech cheap negatives + bounded slow-Determine WorkGiver evidence; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T8 production behavior retained + T12 clean bounded TryEnter stage attribution; T9/T10/T11=ABSENT; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T12 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T8 T5 Carrier Pruners', 'V0.9.3-T12 Clean TryEnter Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T12: clean T8 child with bounded TryEnter Position/Clamor/Filth/Snow/Door/Need/Setup attribution; T9/T10/T11 absent.'
