$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T19 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T19 Quest Deep + Mobile Global Rescue
# Production: retains T18 ClosestThingReachable mobile rescue and adds an authority-safe
# ClosestThing_Global fast path only for measured large Pawn-containing IList sources with
# priorityGetter == null and all members spawned. Vanilla validator semantics remain final.
# Diagnostics: long-lived, low-duty-cycle NaturalRandomQuestChooser -> QuestScriptDef.CanRun
# attribution. Stopwatch work is active only while a natural random quest choice is executing.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t18-mobile-source-rescue-storyteller-deep";' 'internal const string Version = "0.9.3-t19-quest-deep-mobile-global-rescue";' 'T19 bootstrap version'
$boot = Replace-OrThrow $boot @'
                MobileSourceRescue093T18.Apply(harmony);
                StorytellerDeepAttribution093T18.Initialize();
                DoBillTailFabric092.Apply(harmony);
'@ @'
                MobileSourceRescue093T18.Apply(harmony);
                StorytellerDeepAttribution093T18.Initialize();
                QuestDeepAttribution093T19.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'T19 quest attribution install'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T18 Mobile Source Rescue + Storyteller Deep initialized. T17 memo disabled after zero-hit runtime validation; measured large mobile custom sources receive early validated S4 rescue, and one bounded 64-interval Storyteller attribution burst auto-unpatches.' '[RimMT] V0.9.3-T19 Quest Deep + Mobile Global Rescue initialized. T18 reachable mobile rescue retained; T19 adds measured large mobile ClosestThing_Global rescue plus long-lived low-duty-cycle natural quest CanRun attribution. T18 bounded Storyteller burst retained; SMF dispatcher policy unchanged.' 'T19 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(MobileSourceRescue093T18.Summary());
            sb.AppendLine(StorytellerDeepAttribution093T18.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(MobileSourceRescue093T18.Summary());
            sb.AppendLine(StorytellerDeepAttribution093T18.Summary());
            sb.AppendLine(QuestDeepAttribution093T19.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T19 report counters'
$report = $report.Replace('V0.9.3-T18 Mobile Source Rescue + Storyteller Deep', 'V0.9.3-T19 Quest Deep + Mobile Global Rescue')
$report = Replace-OrThrow $report 'T18 promotes the already-validated S4 nearest-first/validator-first/live-CanReach path only for JobGiver custom IList sources >=96 containing Pawn candidates; T18 Storyteller deep attribution is a one-shot 64-interval temporary burst that auto-unpatches; no job/validator/Reachability result is cached; SMF dispatcher coexistence remains census-only;' 'T18 reachable mobile rescue retained; T19 adds exact 5-arg ClosestThing_Global rescue only for JobGiver IList sources >=96 containing Pawn candidates, priorityGetter=null and all members spawned; T19 natural-random-quest attribution stays resident but activates timers only inside ChooseNaturalRandomQuest and records per-QuestScriptDef CanRun cost/mod source; T18 Storyteller 64-interval burst retained; no job/validator/Reachability result is cached; SMF dispatcher coexistence remains census-only;' 'T19 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T18 Mobile Source Rescue + Storyteller Deep', 'V0.9.3-T19 Quest Deep + Mobile Global Rescue')
    $about = $about.Replace('T16 attribution retained; T18 targets measured large mobile GenClosest sources and adds one bounded Storyteller deep-attribution burst. T17 zero-hit HaulUrgently memo is disabled.', 'T18 reachable mobile rescue retained. T19 adds measured large mobile ClosestThing_Global rescue and long-lived low-duty-cycle natural random quest CanRun attribution; T18 bounded Storyteller burst remains available. T17 zero-hit HaulUrgently memo stays disabled.')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T19: T18 reachable mobile rescue retained; large mobile ClosestThing_Global phase 2 added; natural quest chooser/CanRun attribution resident and measurement-only; SMF dispatcher policy unchanged.'
