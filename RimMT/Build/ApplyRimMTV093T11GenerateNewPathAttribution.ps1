$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T11 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T11 GenerateNewPath attribution
# Diagnostic-only child of T10. T8 production behavior remains unchanged.
# Correlates each bounded GenerateNewPath sample with nested FindPath timing and directly timed
# VFE GenerateNewPath prefix / Pathfinding Framework GenerateNewPath postfix methods.
# Separate >=10ms and >=20ms recent rings preserve severe events from being overwritten by microtails.

$patherPatchPath = 'RimMT/Source/RimMT/Patches/PatherInternalPatches093T9.cs'
$patherPatch = Get-Content $patherPatchPath -Raw
$patherPatch = Replace-OrThrow $patherPatch @'
        public static void GeneratePrefix(ref long __state) { Begin(ref __state); }
        public static void GeneratePostfix(long __state) { End(__state, PatherInternalPhase093T9.GenerateNewPath); }
'@ @'
        public static void GeneratePrefix(Pawn ___pawn, LocalTargetInfo ___destination, PathEndMode ___peMode, ref long __state)
        {
            Begin(ref __state);
            GenerateNewPathAttribution093T11.BeginGenerate(___pawn, ___destination, ___peMode);
        }
        public static void GeneratePostfix(long __state)
        {
            GenerateNewPathAttribution093T11.EndGenerate(__state);
            End(__state, PatherInternalPhase093T9.GenerateNewPath);
        }
'@ 'wire T11 GenerateNewPath begin/end into T9 inclusive probe'
Set-Content $patherPatchPath $patherPatch -Encoding UTF8

$pathfinderDiagPath = 'RimMT/Source/RimMT/Diagnostics/TailPathfinderAttribution093T3.cs'
$pathfinderDiag = Get-Content $pathfinderDiagPath -Raw
$pathfinderDiag = Replace-OrThrow $pathfinderDiag @'
            currentCalls++;
            if (us > currentMaxCallUs) currentMaxCallUs = us;
'@ @'
            currentCalls++;
            GenerateNewPathAttribution093T11.NoteFindPath(us);
            if (us > currentMaxCallUs) currentMaxCallUs = us;
'@ 'bridge T3 nested FindPath duration into active T11 GenerateNewPath sample'
Set-Content $pathfinderDiagPath $pathfinderDiag -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TryEnterForeignPatches093T10.Apply(harmony);
'@ @'
            TryEnterForeignPatches093T10.Apply(harmony);
            GenerateNewPathForeignPatches093T11.Apply(harmony);
'@ 'install T11 direct foreign GenerateNewPath probes after T10'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t10-tryenter-foreign-attribution";' 'internal const string Version = "0.9.3-t11-generatenewpath-attribution";' 'T11 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T10 TryEnter Foreign Attribution initialized. T8 production behavior retained; T9 Pather probes plus bounded PF/VFE TryEnter attribution are diagnostic-only.' '[RimMT] V0.9.3-T11 GenerateNewPath Attribution initialized. T8 production behavior retained; T9/T10 probes plus bounded GenerateNewPath/FindPath/foreign attribution are diagnostic-only.' 'T11 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(TryEnterForeignAttribution093T10.RecentSummary());
'@ @'
            sb.AppendLine(TryEnterForeignAttribution093T10.RecentSummary());
            sb.AppendLine(GenerateNewPathAttribution093T11.Summary());
            sb.AppendLine(GenerateNewPathAttribution093T11.Recent10Summary());
            sb.AppendLine(GenerateNewPathAttribution093T11.Recent20Summary());
'@ 'T11 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T10 TryEnter Foreign Attribution' 'V0.9.3-T11 GenerateNewPath Attribution' 'T11 report title'
$report = Replace-OrThrow $report 'T8 clean T5 carrier/mech pruners retained; T9 bounded Pather-internal attribution retained; T10 PF/VFE TryEnter foreign-postfix attribution=diagnostic-only; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T8 clean T5 carrier/mech pruners retained; T9 bounded Pather attribution + T10 TryEnter foreign attribution retained; T11 GenerateNewPath/nested-FindPath/VFE/PF attribution=diagnostic-only; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T11 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T10 TryEnter Foreign Attribution', 'V0.9.3-T11 GenerateNewPath Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T11: bounded GenerateNewPath attribution with nested T3 FindPath correlation, direct VFE/PF timing, and separate >=10/>=20ms rings; diagnostic-only.'
