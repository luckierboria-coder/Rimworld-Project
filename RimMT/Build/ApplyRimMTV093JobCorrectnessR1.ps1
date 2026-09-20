$ErrorActionPreference='Stop'

# Job Correctness R1
# Purpose: restore Vanilla authority for work search while keeping the non-Job-search RimMT shell alive.
# This is an isolation/hotfix build, not a performance candidate.
#
# We remove bootstrap installation of every known RimMT module that can:
# - skip a JobGiver/GenClosest/CanReserve/CanReach original,
# - return an authoritative cached bool/Thing/null,
# - apply a work-candidate prefilter,
# - or change DoBill/haul work availability.
#
# Diagnostics remain optional and measurement-only.

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw

$modules=@(
  'AdaptiveGenClosestAssist',
  'BroadGenClosestOrder0418',
  'JobGiverGlobalNearest04181',
  'JobGiverSlowSearch0419S',
  'JobGiverHybridTailS51',
  'LargeSetTailRescue092',
  'HaulWorkAccelerator',
  'GlobalHaulAccelerator',
  'WorkGiverMergePartnerIndex093T4',
  'PersistentDoBillIndex092',
  'DoBillTailFabric092',
  'AggressiveReachabilityProfilesV17',
  'JobSearchPackageContext093T28',
  'JobSearchTransaction093T20',
  'GenClosestTransactionIndex093T22',
  'ReservationTransaction093T32A'
)

$removed=@()
foreach($m in $modules){
  $pattern='(?m)^[ \t]*' + [regex]::Escape($m) + '\.Apply\(harmony\);[ \t]*\r?\n'
  $before=$boot
  $boot=[regex]::Replace($boot,$pattern,'')
  if($boot -ne $before){ $removed += $m }
}

# Version/log label.
$boot=[regex]::Replace(
  $boot,
  'internal const string Version = "[^"]+";',
  'internal const string Version = "0.9.3-job-correctness-r1";',
  1)

$logMarker='Log.Message("[RimMT] V0.9.3'
$idx=$boot.IndexOf($logMarker)
if($idx -ge 0){
  $end=$boot.IndexOf(');',$idx)
  if($end -ge 0){
    $old=$boot.Substring($idx,$end+2-$idx)
    $new=@'
Log.Message("[RimMT] V0.9.3 Job Correctness R1 initialized. " +
                    "Vanilla Job/GenClosest/CanReserve/CanReach/DoBill/haul authority restored for regression isolation. " +
                    "Result-authoritative RimMT work-search accelerators are not installed in this build.");
'@
    $boot=$boot.Replace($old,$new.TrimEnd())
  }
}

Set-Content $bootPath $boot -Encoding UTF8

# Visible report policy marker. Do not delete implementation files; they are simply not installed.
$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
if(Test-Path $reportPath){
  $r=Get-Content $reportPath -Raw
  $r=[regex]::Replace($r,'V0\.9\.3-T32C\.1 Positive CanReserve Replay on-demand report','V0.9.3 Job Correctness R1 on-demand report')
  $r=[regex]::Replace($r,'V0\.9\.3-T32C\.1 Positive CanReserve Replay','V0.9.3 Job Correctness R1')
  $r=$r.Replace(
    'Production policy:',
    'Production policy: JOB-CORRECTNESS-R1; Vanilla work-search authority restored; result-authoritative Job/GenClosest/Reservation/Reach/DoBill/haul acceleration disabled; ')
  Set-Content $reportPath $r -Encoding UTF8
}

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=[regex]::Replace($a,'<name>.*?</name>','<name>RimMT V0.9.3 Job Correctness R1</name>')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Regression-isolation build. Restores Vanilla authority for JobGiver work search, GenClosest, CanReserve, CanReach, DoBill readiness and haul candidate selection by not installing RimMT result-authoritative work-search accelerators. Non-work-search runtime shell remains. Use this build to determine whether RimMT work-search authority layers are responsible for pawns idling instead of taking available work.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

Write-Host ('Job Correctness R1 removed bootstrap installs: ' + ($removed -join ', '))
