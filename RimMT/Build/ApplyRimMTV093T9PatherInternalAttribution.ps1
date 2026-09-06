$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T9 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T9 Pather Internal Attribution
# Diagnostic-only child of T8. Production optimizers remain unchanged.
# Selected Pawn_PathFollower internals are timed only inside existing T2 deep PatherTick samples.
# The probes are inclusive and intentionally do not alter path decisions, collision, reservations,
# jobs, movement costs, FindPath, or T8 carrier/mech pruning.

$t2PatchPath = 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs'
$t2Patch = Get-Content $t2PatchPath -Raw
$t2Patch = Replace-OrThrow $t2Patch @'
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"), nameof(PhasePrefix), nameof(PatherPostfix), "Pawn_PathFollower.PatherTick");
'@ @'
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"), nameof(PatherPrefix), nameof(PatherPostfix), "Pawn_PathFollower.PatherTick");
'@ 'T9 use Pather-specific deep prefix'

$t2Patch = Replace-OrThrow $t2Patch @'
        public static void PawnPrefix(ref long __state)
'@ @'
        public static void PatherPrefix(ref long __state)
        {
            if (!TailPawnAttribution093T2.DeepActive)
            {
                __state = 0L;
                return;
            }
            __state = TailPawnAttribution093T2.BeginPhase();
            PatherInternalAttribution093T9.BeginPather();
        }

        public static void PawnPrefix(ref long __state)
'@ 'T9 bounded Pather internal attribution begin'

$t2Patch = Replace-OrThrow $t2Patch @'
        public static void PatherPostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.PatherTick);
        }
'@ @'
        public static void PatherPostfix(long __state)
        {
            if (__state == 0L) return;
            PatherInternalAttribution093T9.EndPather(__state);
            TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.PatherTick);
        }
'@ 'T9 bounded Pather internal attribution end'
Set-Content $t2PatchPath $t2Patch -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPathfinderPatches093T3.Apply(harmony);
'@ @'
            TailPathfinderPatches093T3.Apply(harmony);
            PatherInternalPatches093T9.Apply(harmony);
'@ 'install T9 Pather internal probes'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t8-t5-carrier-pruners";' 'internal const string Version = "0.9.3-t9-pather-internal-attribution";' 'T9 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T8 T5 Carrier Pruners initialized. T5 baseline retained; T6 HaulMerge CommonSense coexistence is absent; S4-only authority-safe carrier/mech cheap negatives enabled.' '[RimMT] V0.9.3-T9 Pather Internal Attribution initialized. T8 production behavior retained; bounded Pather internal probes are diagnostic-only.' 'T9 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
'@ @'
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
            sb.AppendLine(PatherInternalAttribution093T9.Summary());
            sb.AppendLine(PatherInternalAttribution093T9.RecentSummary());
            sb.AppendLine(PatherInternalAttribution093T9.HarmonyCensus());
'@ 'T9 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T8 T5 Carrier Pruners' 'V0.9.3-T9 Pather Internal Attribution' 'T9 report title'
$report = Replace-OrThrow $report 'T8 clean T5 rebase with S4-only carrier/mech cheap negatives + bounded slow-Determine WorkGiver evidence; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T8 clean T5 carrier/mech pruners retained; T9 bounded Pather-internal attribution=diagnostic-only; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T9 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T8 T5 Carrier Pruners', 'V0.9.3-T9 Pather Internal Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T9: T8 production behavior retained; bounded inclusive Pawn_PathFollower internal timing and Harmony census added only for diagnosis.'
