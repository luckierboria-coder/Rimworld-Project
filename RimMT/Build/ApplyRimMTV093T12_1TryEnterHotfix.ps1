$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T12.1 hotfix anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

$diagPath = 'RimMT/Source/RimMT/Diagnostics/TryEnterCleanAttribution093T12.cs'
$diag = Get-Content $diagPath -Raw

$diag = Replace-OrThrow $diag @'
        private static readonly StageStat[] StageStats = new StageStat[(int)TryEnterStage093T12.Count];
'@ @'
        private static readonly StageStat[] StageStats = CreateStageStats();
'@ 'initialize all StageStat slots'

$diag = Replace-OrThrow $diag @'
        internal static string Summary()
'@ @'
        private static StageStat[] CreateStageStats()
        {
            StageStat[] stats = new StageStat[(int)TryEnterStage093T12.Count];
            for (int i = 0; i < stats.Length; i++) stats[i] = new StageStat();
            return stats;
        }

        internal static string Summary()
'@ 'StageStat factory'

$diag = Replace-OrThrow $diag @'
            StageStat s = StageStats[i];
            s.Calls++;
'@ @'
            StageStat s = StageStats[i];
            if (s == null) return;
            s.Calls++;
'@ 'EndStage fail-closed null guard'

$diag = Replace-OrThrow $diag @'
                StageStat s = StageStats[i];
                double a = s.Calls == 0L ? 0.0 : s.TotalUs / (double)s.Calls;
'@ @'
                StageStat s = StageStats[i];
                if (s == null)
                {
                    sb.Append(((TryEnterStage093T12)i).ToString()).Append("(unavailable)");
                    continue;
                }
                double a = s.Calls == 0L ? 0.0 : s.TotalUs / (double)s.Calls;
'@ 'Summary fail-closed null guard'

Set-Content $diagPath $diag -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t12-clean-tryenter-attribution";' 'internal const string Version = "0.9.3-t12.1-clean-tryenter-hotfix";' 'T12.1 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T12 Clean TryEnter Attribution initialized.' '[RimMT] V0.9.3-T12.1 Clean TryEnter Attribution Hotfix initialized.' 'T12.1 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report 'V0.9.3-T12 Clean TryEnter Attribution' 'V0.9.3-T12.1 Clean TryEnter Attribution Hotfix' 'T12.1 report title'
$report = $report.Replace('T12 narrow TryEnter stage attribution=diagnostic-only;', 'T12.1 narrow TryEnter stage attribution=diagnostic-only, StageStat slots initialized + null fail-closed guards;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T12 Clean TryEnter Attribution', 'V0.9.3-T12.1 Clean TryEnter Attribution Hotfix')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T12.1 hotfix: StageStats slots initialized; EndStage/Summary null access fail closed. T12 measurement scope/behavior otherwise unchanged.'
