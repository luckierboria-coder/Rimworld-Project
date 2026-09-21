$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT34A.ps1')
if(-not $?){ throw 'T34-A prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T34BScannerParallelFabric.ps1')
if(-not $?){ throw 'T34-B transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T34-B build failed' }

dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.18 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @(
  '0.9.3-t34b-scanner-parallel-fabric',
  'CandidateFabric093T34A.Apply(harmony);',
  'ScannerParallelFabric093T34B.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "T34-B bootstrap invariant missing: $required" }
}
foreach($forbidden in @(
  'DoBillTailFabric092.Apply(harmony);',
  'AggressiveReachabilityProfilesV17.Apply(harmony);'
)){
  if($boot.Contains($forbidden)){ throw "T34-B retired bootstrap path still active: $forbidden" }
}

$runtime=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs') -Raw
foreach($required in @(
  'FeatureGate.Register(ScannerParallelFabric093T34B.FeatureId',
  'FeatureGate.SetEnabled(ScannerParallelFabric093T34B.FeatureId, work);'
)){
  if(-not $runtime.Contains($required)){ throw "T34-B runtime invariant missing: $required" }
}

$scannerPath=Join-Path $root 'RimMT/Source/RimMT/AI/ScannerParallelFabric093T34B.cs'
if(-not (Test-Path $scannerPath)){ throw 'T34-B scanner source was not materialized' }
$scanner=Get-Content $scannerPath -Raw

foreach($required in @(
  'internal const string FeatureId = "parallel.scannerFabric"',
  'scheduler.ParallelFor(',
  'JobPriority.High',
  'BuildDistancePlan(',
  'TryEnsureSourceSnapshotT34B',
  'TryGetKnownSourceSnapshotFastT34B',
  'TryValidateSourceSnapshotT34B',
  'map.reachability.CanReach(',
  'PersistentMapSearchFabric.FlushPendingT34B();',
  'no main-thread worker wait'
)){
  if(-not $scanner.Contains($required)){ throw "T34-B scanner invariant missing: $required" }
}

foreach($forbidden in @(
  'SpinWait',
  '.Wait(',
  '.Join(',
  'Thread.Sleep(',
  'ManualResetEvent',
  'AutoResetEvent',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'ReservationManager',
  'JobMaker.',
  'StartJob(',
  'EndCurrentJob(',
  'JobFailReason',
  'UnityEngine.'
)){
  if($scanner.Contains($forbidden)){ throw "T34-B worker/no-wait authority violation token: $forbidden" }
}

# Ensure BuildPlan worker body contains no obvious mutable-world dereference or gameplay authority.
$buildStart=$scanner.IndexOf('        private static void BuildPlan(')
$nextMethod=$scanner.IndexOf('        private static long MakePlanKey',$buildStart)
if($buildStart -lt 0 -or $nextMethod -lt 0){ throw 'Cannot isolate T34-B BuildPlan worker body' }
$buildBody=$scanner.Substring($buildStart,$nextMethod-$buildStart)
foreach($forbidden in @(
  '.Position',
  '.Map',
  '.MapHeld',
  '.Spawned',
  'CanReach',
  'CanReserve',
  'validator(',
  'WorkGiver',
  'JobMaker',
  'Reservation'
)){
  if($buildBody.Contains($forbidden)){ throw "T34-B worker body mutable-world violation: $forbidden" }
}
if(-not $buildBody.Contains('spec.Snapshot.BuildDistancePlan')){
  throw 'T34-B worker body is not snapshot-only'
}

$candidate=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/CandidateFabric093T34A.cs') -Raw
foreach($required in @(
  'TryEnsureSourceSnapshotT34B',
  'TryGetKnownSourceSnapshotFastT34B',
  'TryValidateSourceSnapshotT34B',
  'Publication is asynchronous. T34-B never waits'
)){
  if(-not $candidate.Contains($required)){ throw "T34-B candidate bridge missing: $required" }
}

$fabric=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabric.cs') -Raw
foreach($required in @(
  'PendingStatesT34B',
  'FlushPendingT34B',
  'FlushQueuedT34B',
  'dirtySources',
  'sourceIdsByThing',
  'publishedSources',
  'new Dictionary<int, SourceSnapshot>(publishedSources)',
  'scheduler.TryEnqueue(FeatureId, JobPriority.High'
)){
  if(-not $fabric.Contains($required)){ throw "T34-B persistent fabric invariant missing: $required" }
}
if($fabric.Contains('scheduler.TryEnqueue(FeatureId, JobPriority.Normal')){
  throw 'T34-B persistent fabric still schedules foreground drains at Normal priority'
}
if($fabric.Contains('Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) != 0)' -and
   $fabric.Contains('ScheduleDrain(state);')){
  # This token pair can appear in FlushPending; verify QueueEvent itself no longer schedules.
  $qStart=$fabric.IndexOf('        private static void QueueEvent(')
  $qEnd=$fabric.IndexOf('        private static void MarkPendingT34B',$qStart)
  if($qStart -lt 0 -or $qEnd -lt 0){ throw 'Cannot isolate T34-B QueueEvent' }
  $qBody=$fabric.Substring($qStart,$qEnd-$qStart)
  if($qBody.Contains('ScheduleDrain(state)')){
    throw 'T34-B QueueEvent still immediately schedules a worker drain'
  }
}

# T27 distance-plan extension must remain available and snapshot-only.
$t27Path=Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabricT27.cs'
if(-not (Test-Path $t27Path)){ throw 'T34-B distance plan extension missing' }
$t27=Get-Content $t27Path -Raw
foreach($required in @(
  'BuildDistancePlan',
  'DistancePlanEntry',
  'entry.X',
  'entry.Z',
  'entry.SourceIndex'
)){
  if(-not $t27.Contains($required)){ throw "T34-B distance-plan invariant missing: $required" }
}
foreach($forbidden in @(
  '.Position',
  '.MapHeld',
  '.Spawned',
  'CanReach',
  'CanReserve'
)){
  if($t27.Contains($forbidden)){ throw "T34-B distance-plan worker extension dereferences live world: $forbidden" }
}

$safetyPath=Join-Path $root 'RimMT/Source/RimMT/AI/WorkGiverParallelSafety093T27_2.cs'
if(Test-Path $safetyPath){
  $safety=Get-Content $safetyPath -Raw
  if(-not $safety.Contains('FullParallel=HARD_OFF')){
    throw 'T34-B could not prove FullParallel remains HARD_OFF'
  }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.18\.0"'){ throw 'Diagnostics v0.18 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
foreach($required in @(
  '"RimMT.ScannerParallelFabric093T34B"',
  '"RimMT.CandidateFabric093T34A"',
  '"RimMT.PersistentMapSearchFabric"'
)){
  if(-not $diagReport.Contains($required)){ throw "Diagnostics T34-B reflection bridge missing: $required" }
}
if($diagReport.Contains('CandidateRejectionCensusT33A') -or $diagReport.Contains('RejectionFamilyCensusT33B')){
  throw 'T33 census profiler leaked into the T34-B diagnostics package'
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
$mainRoot=Join-Path $build 'stage-t34b-main'
$diagRoot=Join-Path $build 'stage-t34b-diag'
$bundle=Join-Path $build 'stage-t34b-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T34B_ScannerParallelFabric.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.18_T34B.zip'
$bundleZip=Join-Path $build 'RimMT_T34B_ScannerParallelFabric_Bundle.zip'
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
