$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T13 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T13 PawnTick Aggregate Attribution
# Clean child of T8. Reuses T2 Stopwatch values rather than adding new per-Pawn/subtracker timers.
# Adds category/residual aggregation and an on-demand Pawn.Tick Harmony census only.
# No Pawn behavior, Job decision, path result, tracker cadence, Harmony ordering, worker scheduling or state commit is changed.

$t2DiagPath = 'RimMT/Source/RimMT/Diagnostics/TailPawnAttribution093T2.cs'
$t2Diag = Get-Content $t2DiagPath -Raw
$t2Diag = Replace-OrThrow $t2Diag @'
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;

            Calls[p]++;
'@ @'
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;

            PawnTickAggregateAttribution093T13.RecordPhase(phase, us);
            Calls[p]++;
'@ 'T13 reuse T2 phase elapsed values'

$t2Diag = Replace-OrThrow $t2Diag @'
            long us = TicksToUs(elapsed);
            int p = (int)PawnTailPhase093T2.PawnTick;
'@ @'
            long us = TicksToUs(elapsed);
            PawnTickAggregateAttribution093T13.RecordPawn(pawn, us);
            int p = (int)PawnTailPhase093T2.PawnTick;
'@ 'T13 reuse T2 Pawn.Tick elapsed value'
Set-Content $t2DiagPath $t2Diag -Encoding UTF8

$t2PatchPath = 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs'
$t2Patch = Get-Content $t2PatchPath -Raw
$t2Patch = Replace-OrThrow $t2Patch @'
        public static void PawnPrefix(ref long __state)
        {
            __state = TailPawnAttribution093T2.DeepActive ? TailPawnAttribution093T2.BeginPhase() : 0L;
        }
'@ @'
        public static void PawnPrefix(Pawn __instance, ref long __state)
        {
            bool deep = TailPawnAttribution093T2.DeepActive;
            PawnTickAggregateAttribution093T13.BeginPawn(__instance, deep);
            __state = deep ? TailPawnAttribution093T2.BeginPhase() : 0L;
        }
'@ 'T13 bind existing T2 Pawn timing to current Pawn without another Stopwatch'
Set-Content $t2PatchPath $t2Patch -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t8-t5-carrier-pruners";' 'internal const string Version = "0.9.3-t13-pawntick-aggregate-attribution";' 'T13 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T8 T5 Carrier Pruners initialized. T5 baseline retained; T6 HaulMerge CommonSense coexistence is absent; S4-only authority-safe carrier/mech cheap negatives enabled.' '[RimMT] V0.9.3-T13 PawnTick Aggregate Attribution initialized. T8 production behavior retained; T13 reuses T2 timestamps for category/residual attribution and adds no subtracker Harmony timers.' 'T13 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
'@ @'
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.Summary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.CategorySummary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.RecentSummary());
            sb.AppendLine(PawnTickAggregateAttribution093T13.PawnTickHarmonyCensus());
'@ 'T13 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T8 T5 Carrier Pruners' 'V0.9.3-T13 PawnTick Aggregate Attribution' 'T13 report title'
$report = Replace-OrThrow $report 'T8 clean T5 rebase with S4-only carrier/mech cheap negatives + bounded slow-Determine WorkGiver evidence; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T8 production behavior retained + T13 low-overhead PawnTick aggregate attribution reusing T2 timestamps; no T9/T10/T11/T12 probe chain; no subtracker Harmony timers; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T13 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T8 T5 Carrier Pruners', 'V0.9.3-T13 PawnTick Aggregate Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T13: clean T8 child; T2 elapsed values reused for PawnTick category/job/pather/residual attribution; on-demand Pawn.Tick Harmony census; no additional subtracker Harmony timers.'
