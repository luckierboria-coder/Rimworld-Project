$ErrorActionPreference='Stop'

# Rebuild the verified T28 baseline, then apply the T30 measurement-only census.
& (Join-Path $PSScriptRoot 'BuildRimMTT28.ps1')
if(-not $?){ throw 'T28 prerequisite build failed' }

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T30JobSearchRedundancyCensus.ps1')
if(-not $?){ throw 'T30 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T30 build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.8 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t30-job-search-redundancy-census'){ throw 'T30 version marker missing' }
if($boot -notmatch 'JobSearchPackageContext093T28\.Apply\(harmony\)'){ throw 'T28 package owner was lost' }

$t30Path=Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs'
if(-not (Test-Path $t30Path)){ throw 'T30 census source missing' }
$t30=Get-Content $t30Path -Raw

foreach($required in @(
  'SampleMask = 7L',
  'DiagnosticsPresent()',
  'RecordValidator',
  'RecordReach',
  'RecordSource',
  'ValidatorMultiScannerThings',
  'ReachMultiShapeTargets',
  'SourceMultiCenter',
  'SLOW20{',
  'no WorkGiver whitelist/name attribution'
)){
  if(-not $t30.Contains($required)){ throw "T30 marker missing: $required" }
}

foreach($forbidden in @(
  'HarmonyLib',
  'AccessTools.',
  'harmony.Patch',
  'ThingsMatching(',
  '.CanReach(',
  'JobOnThing(',
  'StartJob(',
  'EndCurrentJob(',
  'ReservationManager',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For(',
  'ManualResetEvent',
  'Hospitality',
  'PickUpAndHaul',
  'HaulToInventory',
  'Warden_DeliverFood'
)){
  if($t30 -match [regex]::Escape($forbidden)){ throw "T30 forbidden special-case/gameplay/hook primitive: $forbidden" }
}

$ctx=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs') -Raw
foreach($required in @(
  'JobSearchRedundancyCensus093T30.BeginPackage',
  'JobSearchRedundancyCensus093T30.EndPackage'
)){
  if(-not $ctx.Contains($required)){ throw "T30 package lifecycle hook missing: $required" }
}

$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'JobSearchRedundancyCensus093T30.RecordValidator(key)',
  'JobSearchRedundancyCensus093T30.RecordReach(key)'
)){
  if(-not $t20.Contains($required)){ throw "T30 T20/T21 observer missing: $required" }
}

$t22=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs') -Raw
if($t22 -notmatch 'SourceRoute\.GlobalNewTemp'){ throw 'T30 T22 source census missing' }

$global=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs') -Raw
foreach($required in @('SourceRoute.Global','SourceRoute.GlobalReachable','RecordSource(source, center, censusRoute, count)')){
  if(-not $global.Contains($required)){ throw "T30 GlobalNearest source observer missing: $required" }
}

$s4=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs') -Raw
foreach($required in @('SourceRoute.ClosestReachableCustom','SourceRoute.ClosestReachableLister')){
  if(-not $s4.Contains($required)){ throw "T30 S4 source observer missing: $required" }
}

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'V0\.9\.3-T30 Job Search Redundancy Census'){ throw 'T30 production report label missing' }
if($report -notmatch 'JobSearchRedundancyCensus093T30\.Summary\(\)'){ throw 'T30 summary missing from production report' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.8\.0"'){ throw 'Diagnostics v0.8 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -notmatch 'RimMT\.JobSearchRedundancyCensus093T30'){ throw 'Diagnostics v0.8 does not surface T30 census' }

# T29 is intentionally not inherited: T30 starts from T28 after the zero-hit metadata experiment.
if(Test-Path (Join-Path $root 'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs')){
  throw 'T29 Source Metadata unexpectedly present in T30 branch'
}

# Inherited zero-wait and T28 gameplay-safety invariants.
foreach($forbidden in @('StartJob(','EndCurrentJob(','ReservationManager.','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(','ManualResetEvent')){
  if($ctx -match [regex]::Escape($forbidden)){ throw "T28 shared context safety regression in T30: $forbidden" }
}

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T30 inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t30-main'
$diagRoot=Join-Path $build 'stage-t30-diag'
$bundle=Join-Path $build 'stage-t30-bundle'
foreach($p in @($mainRoot,$diagRoot,$bundle)){
  if(Test-Path $p){ Remove-Item $p -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $p | Out-Null
}

$mainStage=Join-Path $mainRoot 'RimMT'
$diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage

$mainZip=Join-Path $build 'RimMT_V0.9.3_T30_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.8.zip'
$bundleZip=Join-Path $build 'RimMT_T30_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
