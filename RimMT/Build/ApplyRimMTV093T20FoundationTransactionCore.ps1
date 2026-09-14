$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T20 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T20 Foundation Transaction Core
# Generic bottom-layer optimization only:
#   JobGiver_Work package transaction -> generic local Validator negative memo -> package-local CanReach memo.
# All memoized query results die at the outer synchronous package boundary. Positive validators,
# Jobs, JobOnThing, reservations, priority and cross-tick state are never cached.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t19-quest-deep-mobile-global-rescue";' 'internal const string Version = "0.9.3-t20-foundation-transaction-core";' 'T20 bootstrap version'
$boot = Replace-OrThrow $boot @'
                MobileSourceRescue093T18.Apply(harmony);
                StorytellerDeepAttribution093T18.Initialize();
                QuestDeepAttribution093T19.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                MobileSourceRescue093T18.Apply(harmony);
                StorytellerDeepAttribution093T18.Initialize();
                QuestDeepAttribution093T19.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'T20 transaction install'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T19 Quest Deep + Mobile Global Rescue initialized. T18 reachable mobile rescue retained; T19 adds measured large mobile ClosestThing_Global rescue plus long-lived low-duty-cycle natural quest CanRun attribution. T18 bounded Storyteller burst retained; SMF dispatcher policy unchanged.' '[RimMT] V0.9.3-T20 Foundation Transaction Core initialized. Generic JobGiver package transaction, validator-negative memo and bounded package-local Reachability memo added below WorkGiver-specific logic. T18/T19 diagnostics retained; SMF dispatcher policy unchanged.' 'T20 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(QuestDeepAttribution093T19.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(QuestDeepAttribution093T19.Summary());
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T20 report counters'
$report = $report.Replace('V0.9.3-T19 Quest Deep + Mobile Global Rescue', 'V0.9.3-T20 Foundation Transaction Core')
$report = Replace-OrThrow $report 'T18 reachable mobile rescue retained; T19 adds exact 5-arg ClosestThing_Global rescue only for JobGiver IList sources >=96 containing Pawn candidates, priorityGetter=null and all members spawned; T19 natural-random-quest attribution stays resident but activates timers only inside ChooseNaturalRandomQuest and records per-QuestScriptDef CanRun cost/mod source; T18 Storyteller 64-interval burst retained; no job/validator/Reachability result is cached; SMF dispatcher coexistence remains census-only;' 'T18 reachable mobile rescue and T19 diagnostics retained; T20 adds one-package JobGiver transaction state, generic JobGiver_Work local-validator false-only memo with parity quarantine, and package-local exact CanReach memo that becomes shadow-only when a foreign postfix can mutate the result; no Job/JobOnThing/reservation/priority/cross-package result is cached; SMF dispatcher coexistence remains census-only;' 'T20 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T19 Quest Deep + Mobile Global Rescue', 'V0.9.3-T20 Foundation Transaction Core')
    $about = $about.Replace('T18 reachable mobile rescue retained. T19 adds measured large mobile ClosestThing_Global rescue and long-lived low-duty-cycle natural random quest CanRun attribution; T18 bounded Storyteller burst remains available. T17 zero-hit HaulUrgently memo stays disabled.', 'T18/T19 measured rescues and attribution remain. T20 adds a generic one-package JobGiver transaction core with false-only validator memo and bounded Reachability reuse/shadow validation; no cross-tick query result cache. T17 zero-hit HaulUrgently memo stays disabled.')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T20 Foundation Transaction Core: generic package transaction, validator-negative memo, Reachability transaction memo/shadow guard; no WorkGiver-specific T20 fix and no SMF policy change.'
