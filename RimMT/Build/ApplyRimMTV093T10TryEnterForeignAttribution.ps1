$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T10 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T10 TryEnter foreign-postfix attribution
# Diagnostic-only child of T9. T8 production behavior remains unchanged.
# Directly times the two known foreign TryEnterNextPathCell postfix methods and records
# PF terrain/graphics helper activity plus VFE fog/phasing/flood activity.
# No owner ordering, path state, result, terrain, graphics, fog or phasing behavior is changed.

$patherPatchPath = 'RimMT/Source/RimMT/Patches/PatherInternalPatches093T9.cs'
$patherPatch = Get-Content $patherPatchPath -Raw
$patherPatch = Replace-OrThrow $patherPatch @'
        public static void EnterPrefix(ref long __state) { Begin(ref __state); }
        public static void EnterPostfix(long __state) { End(__state, PatherInternalPhase093T9.TryEnterNextPathCell); }
'@ @'
        public static void EnterPrefix(Pawn ___pawn, ref long __state)
        {
            Begin(ref __state);
            TryEnterForeignAttribution093T10.BeginTryEnter(___pawn, __state);
        }
        public static void EnterPostfix(Pawn ___pawn, long __state)
        {
            TryEnterForeignAttribution093T10.EndTryEnter(___pawn, __state);
            End(__state, PatherInternalPhase093T9.TryEnterNextPathCell);
        }
'@ 'wire T10 TryEnter begin/end into existing T9 inclusive probe'
Set-Content $patherPatchPath $patherPatch -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            PatherInternalPatches093T9.Apply(harmony);
'@ @'
            PatherInternalPatches093T9.Apply(harmony);
            TryEnterForeignPatches093T10.Apply(harmony);
'@ 'install T10 foreign postfix probes after T9'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t9-pather-internal-attribution";' 'internal const string Version = "0.9.3-t10-tryenter-foreign-attribution";' 'T10 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T9 Pather Internal Attribution initialized. T8 production behavior retained; bounded Pather internal probes are diagnostic-only.' '[RimMT] V0.9.3-T10 TryEnter Foreign Attribution initialized. T8 production behavior retained; T9 Pather probes plus bounded PF/VFE TryEnter attribution are diagnostic-only.' 'T10 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(PatherInternalAttribution093T9.HarmonyCensus());
'@ @'
            sb.AppendLine(PatherInternalAttribution093T9.HarmonyCensus());
            sb.AppendLine(TryEnterForeignAttribution093T10.Summary());
            sb.AppendLine(TryEnterForeignAttribution093T10.RecentSummary());
'@ 'T10 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T9 Pather Internal Attribution' 'V0.9.3-T10 TryEnter Foreign Attribution' 'T10 report title'
$report = Replace-OrThrow $report 'T8 clean T5 carrier/mech pruners retained; T9 bounded Pather-internal attribution=diagnostic-only; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T8 clean T5 carrier/mech pruners retained; T9 bounded Pather-internal attribution retained; T10 PF/VFE TryEnter foreign-postfix attribution=diagnostic-only; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T10 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T9 Pather Internal Attribution', 'V0.9.3-T10 TryEnter Foreign Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T10: direct PF/VFE TryEnter postfix timing + terrain/graphics/fog/phasing evidence; diagnostic-only, production behavior unchanged.'
