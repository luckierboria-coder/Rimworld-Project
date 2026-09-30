param([switch]$ValidateCurrentTree)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT34C11.ps1')
  if(-not $?){ throw 'T34-C.11 prerequisite build failed' }

  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T35AProductionSafetySplit.ps1')
  if(-not $?){ throw 'T35-A production safety split transform failed' }
}

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T35-A build failed' }

dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT Diagnostics v0.21.0 build failed' }

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
foreach($required in @(
  '0.9.3-t35a-production-safety-split',
  'CandidateFabric093T34A.Apply(harmony);',
  'ScannerParallelFabric093T34B.Apply(harmony);',
  'TargetCountParallelFabric093T34C10.Apply(harmony);',
  'HaulToInventoryParallelEligibility093T34C11.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "T35-A bootstrap invariant missing: $required" }
}
foreach($forbidden in @(
  'RootFrameStallCensus093T34C8',
  'AggressiveParallelScanner093T34D',
  'parallel.aggressiveScanner'
)){
  if($boot.Contains($forbidden)){ throw "T35-A forbidden bootstrap path present: $forbidden" }
}

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
foreach($required in @(
  'FeatureGate.Register(ScannerParallelFabric093T34B.FeatureId',
  'FeatureGate.Register(CandidateClassificationFabric093T34C.FeatureId',
  'FeatureGate.Register(DoBillParallelReadinessFabric093T34C9.FeatureId',
  'FeatureGate.Register(TargetCountParallelFabric093T34C10.FeatureId',
  'FeatureGate.Register(HaulToInventoryParallelEligibility093T34C11.FeatureId'
)){
  if(-not $runtime.Contains($required)){ throw "T35-A runtime invariant missing: $required" }
}
if($runtime.Contains('RootFrameStallCensus093T34C8')){
  throw 'T35-A diagnostics census leaked into production runtime'
}

$mainRoot=Join-Path $root 'RimMT/Source/RimMT'
foreach($token in @('parallel.aggressiveScanner','AggressiveParallelScanner093T34D')){
  $hit=Get-ChildItem $mainRoot -Recurse -Filter '*.cs' -File |
    Select-String -SimpleMatch $token | Select-Object -First 1
  if($hit){ throw "T35-A aggressiveScanner regression: $token at $($hit.Path):$($hit.LineNumber)" }
}

$censusPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RootFrameStallCensus093T34C8.cs'
if(Test-Path $censusPath){ throw 'T35-A production still contains RootFrameStallCensus093T34C8.cs' }

$scannerPath=Join-Path $root 'RimMT/Source/RimMT/AI/ScannerParallelFabric093T34B.cs'
$scanner=Get-Content $scannerPath -Raw
foreach($required in @(
  'private const int PrefetchSourcesPerPackage = 4;',
  'private const int MaxPackagePlans = 16;',
  'private const int MinPrefetchHotScore = 3;',
  'candidate.Score < MinPrefetchHotScore',
  'main thread never waits'
)){
  if(-not $scanner.Contains($required)){ throw "T35-A scanner tuning invariant missing: $required" }
}
$buildStart=$scanner.IndexOf('        private static void BuildPlan(')
$buildEnd=$scanner.IndexOf('        private static long MakePlanKey',$buildStart)
if($buildStart -lt 0 -or $buildEnd -le $buildStart){ throw 'T35-A cannot isolate scanner worker body' }
$worker=$scanner.Substring($buildStart,$buildEnd-$buildStart)
foreach($forbidden in @(
  'validator(',
  'CanReach',
  'Reachability',
  'CanReserve',
  'ReservationManager',
  'MapPawns',
  '.Wait(',
  '.Join(',
  'SpinWait',
  'Thread.Sleep('
)){
  if($worker.Contains($forbidden)){ throw "T35-A scanner worker invariant violated: $forbidden" }
}

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
foreach($required in @(
  'Version = "0.21.0"',
  'RootFrameStallCensusT35A.Apply(harmony);'
)){
  if(-not $diag.Contains($required)){ throw "T35-A diagnostics bootstrap invariant missing: $required" }
}
if($diag.Contains('MapPostTickComponentCensus.Apply(harmony);')){
  throw 'T35-A safe-startup regression: MapPostTickComponentCensus must not patch every loaded component at bootstrap'
}

$diagV02=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV02.cs') -Raw
if($diagV02.Contains('PatchWorkGiverMethods(harmony);')){
  throw 'T35-A safe-startup regression: broad WorkGiver bootstrap patching is still active'
}

$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
foreach($required in @(
  'RootFrameStallCensusT35A.Summary()',
  'AggressiveScannerRegressionProbeT35A.Summary()',
  'MapPostTickComponentCensus.Summary()'
)){
  if(-not $diagReport.Contains($required)){ throw "T35-A diagnostics report invariant missing: $required" }
}
if($diagReport.Contains('"RimMT.RootFrameStallCensus093T34C8"')){
  throw 'T35-A Diagnostics still expects production RootFrameStallCensus093T34C8'
}

$rootDiagPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RootFrameStallCensusT35A.cs'
$probePath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/AggressiveScannerRegressionProbeT35A.cs'
if(-not (Test-Path $rootDiagPath)){ throw 'T35-A Diagnostics RootFrame census source missing' }
if(-not (Test-Path $probePath)){ throw 'T35-A aggressiveScanner regression probe source missing' }

$probe=Get-Content $probePath -Raw
foreach($required in @(
  'parallel.aggressiveScanner',
  'AggressiveParallelScanner',
  'retired worker-validator scanner route',
  'Reachability.CanReach from a worker'
)){
  if(-not $probe.Contains($required)){ throw "T35-A aggressiveScanner probe invariant missing: $required" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){
  throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')"
}
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){
  throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')"
}

$buildRoot=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$mainRootStage=Join-Path $buildRoot 'stage-t35a-main'
$diagRootStage=Join-Path $buildRoot 'stage-t35a-diag'
$bundle=Join-Path $buildRoot 'stage-t35a-bundle'
foreach($p in @($mainRootStage,$diagRootStage,$bundle)){
  if(Test-Path $p){ Remove-Item $p -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $p | Out-Null
}

$mainStage=Join-Path $mainRootStage 'RimMT'
$diagStage=Join-Path $diagRootStage 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null

Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage

Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage

Get-ChildItem $mainStage -Recurse -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $diagStage -Recurse -Filter '*.pdb' | Remove-Item -Force

$mainZip=Join-Path $buildRoot 'RimMT_V0.9.3_T35A_ProductionSafetySplit.zip'
$diagZip=Join-Path $buildRoot 'RimMT_Diagnostics_v0.21.0_T35A.zip'
$bundleZip=Join-Path $buildRoot 'RimMT_T35A_ProductionSafetySplit_Bundle.zip'
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
