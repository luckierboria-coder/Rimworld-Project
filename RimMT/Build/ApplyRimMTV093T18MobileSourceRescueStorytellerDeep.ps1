$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T18 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T18 Mobile Source Rescue + Storyteller Deep Attribution
# Production: promotes the existing validated S4 nearest-first/live-CanReach semantics only for
# measured large mobile custom IList sources (>=96) at the beginning of JobGiver_Work.
# T17 HaulUrgently package-local memo is disabled because T17 runtime evidence showed zero reuse hits.
# Diagnostics: one bounded 64-storyteller-interval burst times queue, TryFire and the two
# MakeIncidentsForInterval state-machine layers, then auto-unpatches.

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t17-haulurgently-dynamic-rescue";' 'internal const string Version = "0.9.3-t18-mobile-source-rescue-storyteller-deep";' 'T18 bootstrap version'
$boot = Replace-OrThrow $boot @'
                JobGiverSlowSearch0419S.Apply(harmony);
                HaulUrgentlyDynamicMemo093T17.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                JobGiverSlowSearch0419S.Apply(harmony);
                MobileSourceRescue093T18.Apply(harmony);
                StorytellerDeepAttribution093T18.Initialize();
                DoBillTailFabric092.Apply(harmony);
'@ 'T18 production/diagnostic module install and T17 disable'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T17 HaulUrgently Dynamic Rescue initialized. T16 bounded diagnostics retained; measured AllowTool HaulUrgently dynamic sources now use package-local source-order memoization with sampled parity quarantine; Vanilla validator/Reachability/JobOnThing remain authoritative.' '[RimMT] V0.9.3-T18 Mobile Source Rescue + Storyteller Deep initialized. T17 memo disabled after zero-hit runtime validation; measured large mobile custom sources receive early validated S4 rescue, and one bounded 64-interval Storyteller attribution burst auto-unpatches.' 'T18 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath = 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime = Get-Content $runtimePath -Raw
$runtime = Replace-OrThrow $runtime @'
            WorkGiverDeepAttribution093T15.OnMainThreadFrame();

            bool logicalTickBoundary = true;
'@ @'
            WorkGiverDeepAttribution093T15.OnMainThreadFrame();
            StorytellerDeepAttribution093T18.OnMainThreadFrame();

            bool logicalTickBoundary = true;
'@ 'T18 deferred Storyteller burst lifecycle'
Set-Content $runtimePath $runtime -Encoding UTF8

# Feed T1's already-recorded catastrophic Storyteller timing into the bounded T18 burst.
$storyPath = 'RimMT/Source/RimMT/Diagnostics/StorytellerCatastrophic093T15.cs'
$story = Get-Content $storyPath -Raw
$story = Replace-OrThrow $story @'
            events++;
            if (us > maxUs) maxUs = us;
            int tick = -1;
'@ @'
            events++;
            if (us > maxUs) maxUs = us;
            StorytellerDeepAttribution093T18.ObserveCatastrophic(us);
            int tick = -1;
'@ 'T18 catastrophic event bridge'
$story = Replace-OrThrow $story 'bool ours = string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal);' 'bool ours = patch.owner != null && patch.owner.StartsWith("allen.rimmt", StringComparison.Ordinal);' 'T18 diagnostic Harmony owner census'
Set-Content $storyPath $story -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(HaulUrgentlyDynamicMemo093T17.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(MobileSourceRescue093T18.Summary());
            sb.AppendLine(StorytellerDeepAttribution093T18.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T18 report counters'
$report = $report.Replace('V0.9.3-T17 HaulUrgently Dynamic Rescue', 'V0.9.3-T18 Mobile Source Rescue + Storyteller Deep')
$report = Replace-OrThrow $report 'T16 caller/source-shape search attribution + T17 package-local HaulUrgently dynamic-source memo; T16 temporary diagnostics remain bounded; T17 records the first Vanilla enumeration in source order, reuses it only inside the same synchronous JobGiver_Work package, samples exact reference/order parity every 64 reuses, and quarantines on mismatch/foreign source patches; no validator/Reachability/job result is cached; SMF dispatcher coexistence remains census-only;' 'T16 caller/source-shape attribution retained; T17 HaulUrgently package-local memo disabled after runtime evidence showed cacheHits=0; T18 promotes the already-validated S4 nearest-first/validator-first/live-CanReach path only for JobGiver custom IList sources >=96 containing Pawn candidates; T18 Storyteller deep attribution is a one-shot 64-interval temporary burst that auto-unpatches; no job/validator/Reachability result is cached; SMF dispatcher coexistence remains census-only;' 'T18 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T17 HaulUrgently Dynamic Rescue', 'V0.9.3-T18 Mobile Source Rescue + Storyteller Deep')
    $about = $about.Replace('T16 caller/source-shape search attribution plus T17 package-local HaulUrgently dynamic-source rescue.', 'T16 attribution retained; T18 targets measured large mobile GenClosest sources and adds one bounded Storyteller deep-attribution burst. T17 zero-hit HaulUrgently memo is disabled.')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T18: T17 zero-hit memo disabled; early mobile-source S4 rescue for measured large Pawn-containing custom lists; bounded 64-interval Storyteller deep burst; SMF dispatcher policy unchanged.'
