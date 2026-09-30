$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32C1.ps1')
if(-not $?){ throw 'T32-C.1 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T34AAsyncCandidateFabric.ps1')
if(-not $?){ throw 'T34-A transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T34-A build failed' }

dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.17 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @(
  '0.9.3-t34a-async-candidate-fabric',
  'CandidateFabric093T34A.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "T34-A bootstrap invariant missing: $required" }
}
foreach($forbidden in @(
  'DoBillTailFabric092.Apply(harmony);',
  'AggressiveReachabilityProfilesV17.Apply(harmony);'
)){
  if($boot.Contains($forbidden)){ throw "T34-A retired bootstrap path still active: $forbidden" }
}

$runtime=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs') -Raw
foreach($required in @(
  'FeatureGate.Register(CandidateFabric093T34A.FeatureId',
  'FeatureGate.SetEnabled(CandidateFabric093T34A.FeatureId, work);',
  'CandidateFabric093T34A.MarkCompatibilityReady();'
)){
  if(-not $runtime.Contains($required)){ throw "T34-A runtime invariant missing: $required" }
}
if($runtime.Contains('FeatureGate.SetEnabled(AggressiveReachabilityProfiles.FeatureId, work);')){
  throw 'ReachProfile runtime gate unexpectedly active in T34-A'
}

$candidatePath=Join-Path $root 'RimMT/Source/RimMT/AI/CandidateFabric093T34A.cs'
$candidate=Get-Content $candidatePath -Raw
foreach($required in @(
  'internal const string FeatureId = "parallel.candidateFabric"',
  'PersistentMapSearchFabric.Apply(harmony);',
  'map.listerThings.ThingsMatching(thingReq)',
  'PersistentMapSearchFabric.RegisterOrUpdateSource',
  'PersistentMapSearchFabric.TryGetSourceSnapshot',
  'snapshot.TryFindClosest(',
  'count + 1',
  'ThingRequest-backed',
  'main thread never waits'
)){
  if(-not $candidate.Contains($required)){ throw "T34-A candidate invariant missing: $required" }
}

foreach($forbidden in @(
  'SpinWait',
  '.Wait(',
  '.Join(',
  'Thread.Sleep(',
  'ManualResetEvent',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'Parallel.For(',
  'ReservationManager',
  'JobMaker.',
  'StartJob(',
  'EndCurrentJob('
)){
  if($candidate.Contains($forbidden)){ throw "T34-A candidate no-wait/authority violation: $forbidden" }
}

$fabric=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabric.cs') -Raw
if(-not $fabric.Contains('private const string FeatureId = CandidateFabric093T34A.FeatureId;')){
  throw 'Persistent fabric is not routed through the T34-A feature gate'
}
foreach($required in @(
  'scheduler.TryEnqueue(FeatureId',
  'Worker snapshots contain only Thing references plus primitive positions/source order',
  'RuntimeHelpers.GetHashCode(obj)'
)){
  if(-not $fabric.Contains($required)){ throw "Persistent fabric safety invariant missing: $required" }
}

$stage=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/LargeSetTailRescue092.cs') -Raw
$stageCtorStart=$stage.IndexOf('static LargeSetTailRescue092()')
$stageInstall=$stage.IndexOf('private static void Install()',$stageCtorStart)
if($stageCtorStart -lt 0 -or $stageInstall -lt 0){ throw 'Cannot isolate Stage3 static constructor' }
$stageCtor=$stage.Substring($stageCtorStart,$stageInstall-$stageCtorStart)
if($stageCtor.Contains('ExecuteWhenFinished(Install)')){
  throw 'Stage3 static self-install is still active in T34-A'
}

$safetyPath=Join-Path $root 'RimMT/Source/RimMT/AI/WorkGiverParallelSafety093T27.cs'
if(Test-Path $safetyPath){
  $safety=Get-Content $safetyPath -Raw
  if(-not $safety.Contains('FullParallel=HARD_OFF') -and -not $safety.Contains('FullParallel = false')){
    throw 'T34-A could not prove FullParallel remains HARD_OFF'
  }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.17\.0"'){ throw 'Diagnostics v0.17 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
foreach($required in @(
  '"RimMT.CandidateFabric093T34A"',
  '"RimMT.PersistentMapSearchFabric"',
  '"RimMT.GlobalHaulAccelerator"'
)){
  if(-not $diagReport.Contains($required)){ throw "Diagnostics T34-A reflection bridge missing: $required" }
}
if($diagReport.Contains('CandidateRejectionCensusT33A') -or $diagReport.Contains('RejectionFamilyCensusT33B')){
  throw 'T33 census profiler leaked into the T34-A diagnostics package'
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){
  throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')"
}
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){
  throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')"
}

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t34a-main'
$diagRoot=Join-Path $build 'stage-t34a-diag'
$bundle=Join-Path $build 'stage-t34a-bundle'

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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T34A_AsyncCandidateFabric.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.17_T34A.zip'
$bundleZip=Join-Path $build 'RimMT_T34A_AsyncCandidateFabric_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){
  if(Test-Path $z){ Remove-Item $z -Force }
}

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
