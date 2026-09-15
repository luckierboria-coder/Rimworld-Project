$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T22 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# T22 closes the root attribution blind spot around World.WorldTick and adds a guarded
# WorldTechLevel TraitDef narrow-rebuild bridge. No WorkGiver-specific optimizer is added.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t21-foundation-reach-chain";' 'internal const string Version = "0.9.3-t22-worldtick-root-attribution";' 'T22 bootstrap version'
$boot = Replace-OrThrow $boot @'
                QuestDeepAttribution093T19.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                QuestDeepAttribution093T19.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);
                WorldRootAttribution093T22.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'T22 root attribution install'
$boot = $boot.Replace('V0.9.3-T21 Foundation II Reach Chain initialized.', 'V0.9.3-T22 World Root Attribution initialized.')
$boot = $boot.Replace('T20 generic validator transaction retained; package-local Reachability now memoizes only the pre-postfix base result while every foreign prefix/postfix remains live. T18/T19 diagnostics retained; SMF dispatcher policy unchanged.', 'T21 foundation retained. T22 directly times World.WorldTick and concrete WorldComponent ticks, aggregates warning storms and PawnGenerator cost, and narrows WorldTechLevel TraitDef mismatch rebuilds to TraitDef-only when exact runtime authority is safe; any failure falls back to the original global rebuild. SMF dispatcher policy unchanged.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T21 Foundation II Reach Chain', 'V0.9.3-T22 World Root Attribution')
$report = Replace-OrThrow $report @'
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(WorldRootAttribution093T22.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T22 report counters'
$report = $report.Replace('T21 makes package-local CanReach chain-aware by replaying only the pre-postfix base result after all live prefixes, while all foreign postfixes still execute live and remain final authority;', 'T21 makes package-local CanReach chain-aware by replaying only the pre-postfix base result after all live prefixes, while all foreign postfixes still execute live and remain final authority; T22 adds direct WorldTick/world-component/PawnGenerator/warning-storm attribution and guarded WorldTechLevel TraitDef-only mismatch rebuild;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T21 Foundation II Reach Chain', 'V0.9.3-T22 World Root Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T22: direct WorldTick/world-component root attribution plus guarded WorldTechLevel TraitDef narrow rebuild; T21 foundation retained.'
