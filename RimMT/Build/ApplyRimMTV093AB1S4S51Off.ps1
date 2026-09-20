$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "A/B1 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

# --- Disable S5.1 installation entirely ---
$s51Path=Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverHybridTailS51.cs'
$s51=Get-Content $s51Path -Raw
$s51=Replace-OrThrow $s51 @'
        static JobGiverHybridTailS51()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }
'@ @'
        static JobGiverHybridTailS51()
        {
            Log.Message("[RimMT] A/B1: S5.1 tail rescue authority disabled; ClosestThingReachable remains Vanilla-authoritative.");
        }
'@ 'S5.1 static installer'
Set-Content $s51Path $s51 -Encoding UTF8

# --- Disable S4 installation entirely ---
$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '                JobGiverSlowSearch0419S.Apply(harmony);' '                // A/B1: S4 slow-search rescue intentionally not installed.' 'S4 bootstrap install'

$boot=[regex]::Replace(
  $boot,
  'internal const string Version = "[^"]+";',
  'internal const string Version = "0.9.3-t32c1-ab1-s4-s51-off";',
  1)

$boot=$boot.Replace(
  '[RimMT] V0.9.3-T32C.1 Positive CanReserve Replay initialized.',
  '[RimMT] V0.9.3-T32C.1 A/B1 initialized: S5.1 and S4 ClosestThingReachable authority disabled; all other T32-C.1 production paths retained.')

Set-Content $bootPath $boot -Encoding UTF8

# --- Make report self-identifying ---
$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace(
  'V0.9.3-T32C.1 Positive CanReserve Replay on-demand report',
  'V0.9.3-T32C.1 A/B1 S4+S5.1 OFF on-demand report')
$report=$report.Replace(
  'S5.1 admission=16ms;',
  'S5.1=OFF(A/B1);')
$report=$report.Replace(
  'S4 tail=32ms +',
  'S4=OFF(A/B1); former S4 tail=32ms +')
Set-Content $reportPath $report -Encoding UTF8

# --- About metadata ---
$aboutPath=Join-Path $root 'RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=[regex]::Replace(
    $about,
    '<name>.*?</name>',
    '<name>RimMT V0.9.3 T32-C.1 A/B1 - S4+S5.1 OFF</name>')
  $about=[regex]::Replace(
    $about,
    '(?s)<description>.*?</description>',
    '<description>RimMT T32-C.1 correctness-isolation A/B1 for RimWorld 1.5. Production behavior is unchanged except that S5.1 Hybrid Tail Rescue and S4 Slow Search Rescue are not installed, so those paths cannot replace GenClosest.ClosestThingReachable results. T21, T22, T32-A/C.1, DoBill, haul and other production systems remain unchanged. This build is for isolating the mass-idle/false-NoJob regression.</description>')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied A/B1: S5.1 installer disabled; S4 bootstrap install removed; T32-C.1 and all other production paths retained.'
