param([switch]$ValidateCurrentTree)

$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT34B.ps1')
  if(-not $?){ throw 'T34-B prerequisite build failed' }

  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T34CParallelCandidateClassification.ps1')
  if(-not $?){ throw 'T34-C transform failed' }

  $mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
  $diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'
  dotnet build $mainProj --configuration Release --no-restore
  if($LASTEXITCODE -ne 0){ throw 'RimMT T34-C.2 build failed' }
  dotnet build $diagProj --configuration Release --no-restore
  if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.19.2 build failed' }
}

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @(
  '0.9.3-t34c2-source-indexed-classification-reuse',
  'CandidateFabric093T34A.Apply(harmony);',
  'ScannerParallelFabric093T34B.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "T34-C bootstrap invariant missing: $required" }
}

$runtime=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs') -Raw
foreach($required in @(
  'FeatureGate.Register(CandidateClassificationFabric093T34C.FeatureId',
  'FeatureGate.SetEnabled(CandidateClassificationFabric093T34C.FeatureId, work);'
)){
  if(-not $runtime.Contains($required)){ throw "T34-C runtime invariant missing: $required" }
}

$classifierPath=Join-Path $root 'RimMT/Source/RimMT/AI/CandidateClassificationFabric093T34C.cs'
if(-not (Test-Path $classifierPath)){ throw 'T34-C classifier source was not materialized' }
$classifier=Get-Content $classifierPath -Raw
foreach($required in @(
  'internal const string FeatureId = "parallel.candidateClassification"',
  'CandidateFact[] facts',
  'byte[] rejects',
  'byte[] shadows',
  'ValidateReject(',
  'AuthoritySafe(',
  'HasIncompatiblePatch(',
  'IsDiagnosticsMeasurementPatch(',
  'authorityPatches[compatible/foreign]',
  'TryGetRejectReason(',
  'entries[i].SourceIndex',
  'sourcePlans[hit/miss/crossRoot/scheduled/rejected/built/fail]',
  'entryIndex[invalid/duplicate]',
  'Root-independent source-index plans',
  'JobPriority.High',
  'mutable stack/pawn/building facts are shadow-only'
)){
  if(-not $classifier.Contains($required)){ throw "T34-C classifier invariant missing: $required" }
}
foreach($forbidden in @(
  'SpinWait', '.Wait(', '.Join(', 'Thread.Sleep(', 'ManualResetEvent',
  'AutoResetEvent', 'Task.Run(', 'ThreadPool.QueueUserWorkItem',
  'ReservationManager', 'JobMaker.', 'StartJob(', 'EndCurrentJob(', 'UnityEngine.'
)){
  if($classifier.Contains($forbidden)){ throw "T34-C no-wait/authority violation token: $forbidden" }
}

$buildStart=$classifier.IndexOf('        private static void BuildPlan(')
$nextMethod=$classifier.IndexOf('        private static byte EvaluateAuthoritative',$buildStart)
if($buildStart -lt 0 -or $nextMethod -lt 0){ throw 'Cannot isolate T34-C worker body' }
$worker=$classifier.Substring($buildStart,$nextMethod-$buildStart)
foreach($forbidden in @(
  'Thing ', '.Position', '.Map', '.Spawned', 'CanReach', 'CanReserve',
  'validator(', 'WorkGiver', 'Harmony.', 'TryGetComp', 'typeof(Pawn)', ' as Pawn',
  ' is Pawn', 'typeof(Building)', ' as Building', ' is Building'
)){
  if($worker.Contains($forbidden)){ throw "T34-C worker body mutable-world violation: $forbidden" }
}
if(-not $worker.Contains('facts[i].Flags')){ throw 'T34-C worker does not consume primitive fact rows' }

$scanner=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ScannerParallelFabric093T34B.cs') -Raw
foreach($required in @(
  'CandidateClassificationFabric093T34C.TryGetOrSchedule(',
  'CandidateClassificationFabric093T34C.ValidateReject(',
  'CandidateClassificationFabric093T34C.Quarantine(',
  'CandidateClassificationFabric093T34C.NoteConsumed('
)){
  if(-not $scanner.Contains($required)){ throw "T34-C scanner bridge missing: $required" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.19\.2"'){ throw 'Diagnostics v0.19.2 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('"RimMT.CandidateClassificationFabric093T34C"')){
  throw 'Diagnostics T34-C reflection bridge missing'
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
$mainRoot=Join-Path $build 'stage-t34c-main'
$diagRoot=Join-Path $build 'stage-t34c-diag'
$bundle=Join-Path $build 'stage-t34c-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T34C2_SourceIndexedClassificationReuse.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.19.2_T34C2.zip'
$bundleZip=Join-Path $build 'RimMT_T34C2_SourceIndexedClassificationReuse_Bundle.zip'
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
