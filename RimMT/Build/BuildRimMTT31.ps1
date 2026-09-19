$ErrorActionPreference='Stop'

# Rebuild the verified T28 baseline, then apply the T31 measurement-only census.
& (Join-Path $PSScriptRoot 'BuildRimMTT28.ps1')
if(-not $?){ throw 'T28 prerequisite build failed' }

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T31SharedEligibilityPrimitiveCensus.ps1')
if(-not $?){ throw 'T31 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T31 build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.9 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t31-shared-eligibility-census'){ throw 'T31 version marker missing' }
if($boot -notmatch 'JobSearchPackageContext093T28\.Apply\(harmony\)'){ throw 'T28 package owner was lost' }
if($boot -notmatch 'SharedEligibilityPrimitiveCensus093T31\.Apply\(harmony\)'){ throw 'T31 apply missing' }

$t31Path=Join-Path $root 'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
if(-not (Test-Path $t31Path)){ throw 'T31 census source missing' }
$t31=Get-Content $t31Path -Raw

foreach($required in @(
  'SampleMask = 7L',
  'DiagnosticsPresent()',
  '"IsForbidden", PrimitiveKind.Forbidden',
  '"CanReserve", PrimitiveKind.CanReserve',
  '"CanReserveAndReach", PrimitiveKind.CanReserveAndReach',
  'CrossScannerRepeatCalls',
  'CrossScannerRepeatTicks',
  'ResultFlips',
  'CrossScannerResultFlips',
  'factMutations',
  'SLOW20{',
  'timings are inclusive',
  'No WorkGiver/mod special case'
)){
  if(-not $t31.Contains($required)){ throw "T31 marker missing: $required" }
}

# T31 is measurement-only. It may Harmony-patch generic primitives and WorkGiver scope,
# but must not mutate gameplay results/state or add worker/wait behavior.
foreach($forbidden in @(
  '__result =',
  'ReservationManager',
  '.Reserve(',
  '.Release(',
  'StartJob(',
  'EndCurrentJob(',
  'JobMaker.',
  'JobPool.',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For(',
  'ManualResetEvent',
  'Hospitality',
  'PickUpAndHaul',
  'HaulToInventory',
  'Warden_DeliverFood',
  'BrothelColony',
  'DubsBadHygiene',
  'MedievalOverhaul'
)){
  if($t31 -match [regex]::Escape($forbidden)){ throw "T31 forbidden mutation/special-case/worker primitive: $forbidden" }
}

# Candidate facts must be sampled from already-observed targets; never patch trivial property getters.
foreach($forbidden in @(
  'get_Spawned',
  'get_MapHeld',
  'get_PositionHeld',
  'AccessTools.PropertyGetter'
)){
  if($t31 -match [regex]::Escape($forbidden)){ throw "T31 forbidden getter detour: $forbidden" }
}

$ctx=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs') -Raw
foreach($required in @(
  'SharedEligibilityPrimitiveCensus093T31.BeginPackage',
  'SharedEligibilityPrimitiveCensus093T31.EndPackage'
)){
  if(-not $ctx.Contains($required)){ throw "T31 package lifecycle hook missing: $required" }
}

# T30 is deliberately not inherited. T31 asks the next question with a narrower profiler.
if(Test-Path (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs')){
  throw 'T30 heavy redundancy census unexpectedly present in T31 branch'
}
if(Test-Path (Join-Path $root 'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs')){
  throw 'T29 Source Metadata unexpectedly present in T31 branch'
}

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'V0\.9\.3-T31 Shared Eligibility Primitive Census'){ throw 'T31 production report label missing' }
if($report -notmatch 'SharedEligibilityPrimitiveCensus093T31\.Summary\(\)'){ throw 'T31 summary missing from production report' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.9\.0"'){ throw 'Diagnostics v0.9 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -notmatch 'RimMT\.SharedEligibilityPrimitiveCensus093T31'){ throw 'Diagnostics v0.9 does not surface T31 census' }

# Preserve proven T21/T22 and zero-wait invariants.
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
if($t20 -notmatch 'validator caches false only'){ throw 'T21 validator safety policy missing' }
$t22=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs') -Raw
if($t22 -notmatch 'original live validator is called exactly once'){ throw 'T22 live-validator authority marker missing' }

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T31 inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t31-main'
$diagRoot=Join-Path $build 'stage-t31-diag'
$bundle=Join-Path $build 'stage-t31-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T31_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.9.zip'
$bundleZip=Join-Path $build 'RimMT_T31_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
