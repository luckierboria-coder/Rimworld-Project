$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT28.ps1')
if(-not $?){ throw 'T28 prerequisite build failed' }

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T32AReservationTransaction.ps1')
if(-not $?){ throw 'T32-A transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T32-A build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.10 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t32a-reservation-transaction'){ throw 'T32-A version marker missing' }
if($boot -notmatch 'JobSearchPackageContext093T28\.Apply\(harmony\)'){ throw 'T28 package owner lost' }
if($boot -notmatch 'ReservationTransaction093T32A\.Apply\(harmony\)'){ throw 'T32-A apply missing' }

$t32Path=Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs'
if(-not (Test-Path $t32Path)){ throw 'T32-A source missing' }
$t32=Get-Content $t32Path -Raw

foreach($required in @(
  'typeof(ReservationManager)',
  'nameof(ReservationManager.CanReserve)',
  'Capacity = 8192',
  'WarmupMatches = 16',
  'VerifyMask = 63',
  'NegativeEntry',
  'MutationEpoch',
  'TargetFingerprint',
  'ReservationMutationPrefix',
  '"Reserve"',
  '"Release"',
  '"ReleaseClaimedBy"',
  '"ReleaseAllClaimedBy"',
  '"ReleaseAllForTarget"',
  'runtimeQuarantined = true',
  'foreignTranspilers == 0',
  'foreignFinalizers == 0',
  'bool __runOriginal',
  '__result = false',
  'False-only exact CanReserve memo'
)){
  if(-not $t32.Contains($required)){ throw "T32-A marker missing: $required" }
}

foreach($forbidden in @(
  '__result = true',
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
  if($t32 -match [regex]::Escape($forbidden)){ throw "T32-A forbidden mutation/special-case/worker primitive: $forbidden" }
}

# No direct reservation mutation calls are allowed; T32-A only observes mutation entrypoints.
foreach($forbidden in @(
  '__instance.Reserve(',
  '__instance.Release(',
  '.reservationManager.Reserve(',
  '.reservationManager.Release('
)){
  if($t32 -match [regex]::Escape($forbidden)){ throw "T32-A attempted reservation mutation: $forbidden" }
}

$ctx=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs') -Raw
foreach($required in @(
  'ReservationTransaction093T32A.BeginPackage',
  'ReservationTransaction093T32A.EndPackage'
)){
  if(-not $ctx.Contains($required)){ throw "T32-A package lifecycle hook missing: $required" }
}

# T29/T30/T31 experiments must not leak into the production optimization branch.
foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T32-A: $dead" }
}

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'V0\.9\.3-T32A Reservation Transaction'){ throw 'T32-A production report label missing' }
if($report -notmatch 'ReservationTransaction093T32A\.Summary\(\)'){ throw 'T32-A summary missing from production report' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.10\.0"'){ throw 'Diagnostics v0.10 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -notmatch 'RimMT\.ReservationTransaction093T32A'){ throw 'Diagnostics v0.10 does not surface T32-A' }
if($diagReport -match 'SharedEligibilityPrimitiveCensus093T31|JobSearchRedundancyCensus093T30'){
  throw 'T30/T31 profiler leaked into Diagnostics v0.10'
}

# Preserve proven T21/T22 safety boundaries.
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'validator caches false only',
  'reachRuntimeQuarantined',
  'JobSearchPackageContext093T28.Installed'
)){
  if(-not $t20.Contains($required)){ throw "T21 safety marker missing: $required" }
}
$t22=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs') -Raw
if($t22 -notmatch 'original live validator is called exactly once'){ throw 'T22 live-validator authority marker missing' }

# Preserve zero-wait.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T32-A inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t32a-main'
$diagRoot=Join-Path $build 'stage-t32a-diag'
$bundle=Join-Path $build 'stage-t32a-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T32A_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.10.zip'
$bundleZip=Join-Path $build 'RimMT_T32A_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
