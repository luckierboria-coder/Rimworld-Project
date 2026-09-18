$ErrorActionPreference='Stop'

# Rebuild the verified T28 baseline, then apply the T29 Source Metadata delta.
& (Join-Path $PSScriptRoot 'BuildRimMTT28.ps1')
if(-not $?){ throw 'T28 prerequisite build failed' }

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T29SourceMetadataFoundation.ps1')
if(-not $?){ throw 'T29 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T29 build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.7 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t29-source-metadata-foundation'){ throw 'T29 version marker missing' }
if($boot -notmatch 'JobSearchPackageContext093T28\.Apply\(harmony\)'){ throw 'T28 shared package boundary was lost' }

$metadataPath=Join-Path $root 'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs'
if(-not (Test-Path $metadataPath)){ throw 'T29 SourceMetadata source missing' }
$metadata=Get-Content $metadataPath -Raw
foreach($required in @(
  'TryGetValid',
  'PublishCaptured',
  'IsCurrent',
  'CurrentGeneration',
  'GetOrCreateModuleState',
  'ThingListMetadata',
  'SourceItem',
  'fingerprintSamples=',
  'No ListerThings lookup'
)){
  if(-not $metadata.Contains($required)){ throw "T29 metadata marker missing: $required" }
}
foreach($forbidden in @(
  'map.listerThings',
  'ThingsMatching(',
  '.CanReach(',
  'ReservationManager',
  'StartJob(',
  'EndCurrentJob(',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For(',
  'ManualResetEvent'
)){
  if($metadata -match [regex]::Escape($forbidden)){ throw "T29 metadata forbidden scan/gameplay/wait primitive: $forbidden" }
}

$t22Path=Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
$t22=Get-Content $t22Path -Raw
foreach($required in @(
  'SourceMetadata093T29.TryGetValid',
  'SourceMetadata093T29.PublishCaptured',
  'SourceMetadata093T29.SourceItem[]',
  'SourceMetadata093T29.IsCurrent',
  'IntVec3 position = thing.PositionHeld',
  'BuildLegacySamples'
)){
  if(-not $t22.Contains($required)){ throw "T29 T22 consumer marker missing: $required" }
}
if($t22 -notmatch 'original live validator is called exactly once per visited candidate'){
  throw 'T22 live validator authority statement was lost'
}

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'V0\.9\.3-T29 Source Metadata Foundation'){ throw 'T29 production report label missing' }
if($report -notmatch 'SourceMetadata093T29\.Summary\(\)'){ throw 'T29 source metadata summary missing from production report' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.7\.0"'){ throw 'Diagnostics v0.7 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -notmatch 'RimMT\.SourceMetadata093T29'){ throw 'Diagnostics v0.7 does not surface T29 source metadata' }

# Inherited T28/T26 safety invariants remain mandatory.
$shared=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs') -Raw
foreach($forbidden in @('StartJob(','EndCurrentJob(','ReservationManager.','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(','ManualResetEvent')){
  if($shared -match [regex]::Escape($forbidden)){ throw "T28 shared context safety regression in T29: $forbidden" }
}

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T29 inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t29-main'
$diagRoot=Join-Path $build 'stage-t29-diag'
$bundle=Join-Path $build 'stage-t29-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T29_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.7.zip'
$bundleZip=Join-Path $build 'RimMT_T29_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
