$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "Diagnostics v0.2 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

$v02Path='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV02.cs'
$v02=Get-Content $v02Path -Raw
$v02=Replace-OrThrow $v02 @'
                WorkStat stat = Get(__state.Key);
                stat.Calls++;
'@ @'
                WorkStat stat = Get(__state.Key);
                stat.Ensure();
                stat.Calls++;
'@ 'initialize WorkStat method dictionary'
Set-Content $v02Path $v02 -Encoding UTF8

$hubPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsHub.cs'
$hub=Get-Content $hubPath -Raw
$hub=$hub.Replace('[RimMT Diagnostics v0.1]','[RimMT Diagnostics v0.2 core]')
Set-Content $hubPath $hub -Encoding UTF8

$reportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$report=Get-Content $reportPath -Raw
$report=Replace-OrThrow $report @'
                ", waitTrace=" + RimMTDiagnosticsSettings.EnableWaitTrace +
                ", searchTiming=" + RimMTDiagnosticsSettings.EnableSearchTiming);
'@ @'
                ", waitTrace=" + RimMTDiagnosticsSettings.EnableWaitTrace +
                ", patherBreakdown=" + RimMTDiagnosticsSettings.EnablePatherBreakdown +
                ", workGiverBurst=" + RimMTDiagnosticsSettings.EnableWorkGiverBurst +
                ", reachCaptureTrace=" + RimMTDiagnosticsSettings.EnableReachCaptureTrace +
                ", pauseGapTrace=" + RimMTDiagnosticsSettings.EnablePauseGapTrace +
                ", workGiverTriggerMs=" + RimMTDiagnosticsSettings.WorkGiverTriggerMs +
                ", workGiverBurstPackages=" + RimMTDiagnosticsSettings.WorkGiverBurstPackages +
                ", searchTiming=" + RimMTDiagnosticsSettings.EnableSearchTiming);
'@ 'report v0.2 settings'
$report=Replace-OrThrow $report @'
            sb.Append(DiagnosticsHub.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
'@ @'
            sb.Append(DiagnosticsHub.BuildSummary());
            sb.Append(DiagnosticsV02.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
'@ 'append targeted diagnostics summary'
$report=Replace-OrThrow $report @'
            "RimMT.JobGiverSlowSearch0419S",
            "RimMT.DoBillTailFabric092"
'@ @'
            "RimMT.JobGiverSlowSearch0419S",
            "RimMT.WorkGiverMergePartnerIndex093T4",
            "RimMT.PersistentDoBillIndex092",
            "RimMT.PersistentMapSearchFabric",
            "RimMT.DoBillTailFabric092"
'@ 'RimMT production summary types'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMTDiagnostics/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=$about.Replace('<name>RimMT Diagnostics v0.1</name>','<name>RimMT Diagnostics v0.2</name>')
$about=$about.Replace(
'Optional diagnostics companion for RimMT. Contains measurement-only tail, Pawn/Job/Wait, GenClosest/Reachability, Map/World/Storyteller timing, Harmony patch census and reflective RimMT status reporting. Disable this mod for normal gameplay when diagnostics are not needed.',
'Optional diagnostics companion for RimMT. v0.2 adds sampled current-job Wait dwell census, bounded Pather stage timing, a temporary WorkGiver burst profiler after slow DetermineNextJob, ReachProfile capture/Region.Allows attribution, pause/resume gap tagging, Harmony census and reflective RimMT production status. Observation-only; disable this mod for normal gameplay when diagnostics are not needed.')
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Finalized RimMT Diagnostics v0.2 targeted profiler/report.'