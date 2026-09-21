$ErrorActionPreference='Stop'

function Disable-StaticInstallers {
    param([string]$Directory)
    $files = @(Get-ChildItem $Directory -Filter '*.cs' -Recurse -File)
    $changed = 0
    foreach($file in $files){
        $text = Get-Content $file.FullName -Raw
        $before = $text
        $text = [regex]::Replace(
            $text,
            '(?m)^(\s*)LongEventHandler\.ExecuteWhenFinished\(Install\);\s*$',
            '$1/* JOB AUTHORITY SAFE: self-installer intentionally disabled. */')
        if($text -ne $before){
            Set-Content $file.FullName $text -Encoding UTF8
            $changed++
        }
    }
    return $changed
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

# ---------------------------------------------------------------------------
# P0 correctness isolation:
# Production RimMT must not participate in the decision "does this pawn have work?"
# Keep core/UI/measurement infrastructure, but remove every bootstrap AI Apply(harmony)
# entry and every self-installing AI/compatibility Install() scheduled through LongEventHandler.
# Implementations stay in-tree for later binary-search restoration.
# ---------------------------------------------------------------------------

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw

# Keep only RimMTPatches.Apply(harmony): its retained production paths are UI text caching,
# an observational tick-pressure sampler, and PathGrid generation telemetry. All other
# *.Apply(harmony) bootstrap entries are disabled in this correctness build.
$boot=[regex]::Replace(
    $boot,
    '(?m)^(\s*)(?!RimMTPatches\.Apply\(harmony\);\s*$)([A-Za-z_][A-Za-z0-9_\.]*\.Apply\(harmony\);)\s*$',
    '$1// JOB AUTHORITY SAFE disabled: $2')

$boot=[regex]::Replace(
    $boot,
    'internal const string Version = "[^"]+";',
    'internal const string Version = "0.9.3-job-authority-safe";',
    1)

$oldInit='[RimMT] V0.9.3-T32C.1 A/B2 initialized:'
if($boot.Contains($oldInit)){
    $boot=$boot.Replace(
        $oldInit,
        '[RimMT] V0.9.3 JOB AUTHORITY SAFE initialized: all RimMT JobGiver/GenClosest/Reachability/Reservation/DoBill/Haul authority paths are disabled. Previous A/B2 marker:')
}
else {
    $boot=$boot.Replace(
        'Log.Message("[RimMT] V0.9.3',
        'Log.Message("[RimMT] V0.9.3 JOB AUTHORITY SAFE; AI authority disabled. Previous marker: V0.9.3')
}
Set-Content $bootPath $boot -Encoding UTF8

# Neutralize self-installing production layers. This deliberately catches more than the
# currently suspected Harvest/RC2/DoBill modules: the SAFE baseline must fail open to Vanilla,
# not depend on our current suspect list being complete.
$aiDir=Join-Path $root 'RimMT/Source/RimMT/AI'
$compatDir=Join-Path $root 'RimMT/Source/RimMT/Compatibility'
$disabledAI=Disable-StaticInstallers $aiDir
$disabledCompat=Disable-StaticInstallers $compatDir

# Make the production report self-identifying.
$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
if(Test-Path $reportPath){
    $report=Get-Content $reportPath -Raw
    $report=[regex]::Replace(
        $report,
        'V0\.9\.3-T32C\.1 A/B2 GenClosest OFF on-demand report',
        'V0.9.3 JOB AUTHORITY SAFE on-demand report')
    $report=$report.Replace(
        'S5.1=OFF(A/B1); T22=OFF(A/B2); GlobalNearest rewrite=OFF(A/B2, scope-only guard retained);',
        'JOB AUTHORITY SAFE: all RimMT JobGiver/GenClosest/Reachability/Reservation/DoBill/Haul authority paths OFF;')
    Set-Content $reportPath $report -Encoding UTF8
}

# About metadata.
$aboutPath=Join-Path $root 'RimMT/About/About.xml'
if(Test-Path $aboutPath){
    $about=Get-Content $aboutPath -Raw
    $about=[regex]::Replace($about,'<name>.*?</name>',
        '<name>RimMT V0.9.3 - Job Authority SAFE</name>',1)
    $about=[regex]::Replace(
        $about,
        '(?s)<description>.*?</description>',
        '<description>P0 correctness-isolation build for RimWorld 1.5. All RimMT production paths that can participate in JobGiver candidate selection, GenClosest authority, Reachability replay, CanReserve replay, DoBill source filtering, haul selection, WorkGiver pruning, or JobOnThing/JobOnCell negative authority are disabled. Implementations remain in the source tree but are not installed. Vanilla and other mods own work selection. Use together with RimMT Diagnostics v0.16 SAFE Idle Authority Trace to diagnose the mass-Wander regression.</description>',
        1)
    Set-Content $aboutPath $about -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Diagnostics v0.16 SAFE: add read-only Idle Authority Trace to the report.
# ---------------------------------------------------------------------------

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diagPatch=Get-Content $diagPatchPath -Raw
$diagPatch=[regex]::Replace(
    $diagPatch,
    'internal const string Version = "[^"]+";',
    'internal const string Version = "0.16.0-safe";',
    1)
Set-Content $diagPatchPath $diagPatch -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
if(-not $diagReport.Contains('IdleAuthorityTraceSafe.BuildSummary()')){
    $anchor='            sb.Append(DiagnosticsV03.BuildSummary());'
    if(-not $diagReport.Contains($anchor)){ throw 'SAFE diagnostics report anchor missing' }
    $diagReport=$diagReport.Replace(
        $anchor,
        $anchor + [Environment]::NewLine + '            sb.Append(IdleAuthorityTraceSafe.BuildSummary());')
}
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagModPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnosticsMod.cs'
$diagMod=Get-Content $diagModPath -Raw
$diagMod=$diagMod.Replace(
    'Standalone diagnostics companion v0.3. Disable the entire mod for normal gameplay when profiling is not needed.',
    'Diagnostics v0.16 SAFE. Idle Authority Trace is read-only and keeps only sampled/previously-idle DetermineNextJob calls that end in Wander/Wait.')
if(-not $diagMod.Contains('IdleAuthorityTraceSafe.Reset();')){
    $resetAnchor='                DiagnosticsV03.Reset();'
    if(-not $diagMod.Contains($resetAnchor)){ throw 'SAFE diagnostics reset anchor missing' }
    $diagMod=$diagMod.Replace(
        $resetAnchor,
        $resetAnchor + [Environment]::NewLine + '                IdleAuthorityTraceSafe.Reset();')
}
Set-Content $diagModPath $diagMod -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAboutPath){
    $diagAbout=Get-Content $diagAboutPath -Raw
    $diagAbout=[regex]::Replace(
        $diagAbout,
        '<name>.*?</name>',
        '<name>RimMT Diagnostics v0.16 SAFE - Idle Authority Trace</name>',
        1)
    $diagAbout=[regex]::Replace(
        $diagAbout,
        '(?s)<description>.*?</description>',
        '<description>Read-only P0 correctness diagnostics for RimMT Job Authority SAFE. Samples player-humanlike DetermineNextJob decisions and records only WorkGiver return values already produced by the live call. It never invokes extra validators, CanReach, CanReserve, or candidate enumeration. When a decision falls through to Wander/Wait, the report shows ShouldSkip, HasJobOnThing/Cell, JobOnThing/Cell, NonScanJob and non-enumerating PotentialWork source observations.</description>',
        1)
    Set-Content $diagAboutPath $diagAbout -Encoding UTF8
}

Write-Host "Applied JOB AUTHORITY SAFE. Neutralized self-installing files: AI=$disabledAI Compatibility=$disabledCompat"
