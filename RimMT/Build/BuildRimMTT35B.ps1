param([switch]$ValidateCurrentTree)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT35A.ps1')
  if(-not $?){ throw 'T35-A prerequisite build failed' }

  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T35BBurstDiagnostics.ps1')
  if(-not $?){ throw 'T35-B burst diagnostics transform failed' }
}

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T35-B build failed' }

dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT Diagnostics v0.22.0 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @(
  '0.9.3-t35b-burst-diagnostics-pair',
  'CandidateFabric093T34A.Apply(harmony);',
  'ScannerParallelFabric093T34B.Apply(harmony);',
  'TargetCountParallelFabric093T34C10.Apply(harmony);',
  'HaulToInventoryParallelEligibility093T34C11.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "T35-B production invariant missing: $required" }
}
foreach($forbidden in @('AggressiveParallelScanner093T34D','parallel.aggressiveScanner','RootFrameStallCensus093T34C8')){
  if($boot.Contains($forbidden)){ throw "T35-B forbidden production bootstrap path: $forbidden" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
foreach($required in @(
  'Version = "0.22.0"',
  'BurstProbeControllerT35B.Initialize(harmony);',
  'BurstProbeControllerT35B.OnTickBegin();',
  'BurstProbeControllerT35B.OnTickEnd();',
  'RootFrameStallCensusT35A.Apply(harmony);'
)){
  if(-not $diag.Contains($required)){ throw "T35-B diagnostics invariant missing: $required" }
}
foreach($forbidden in @(
  'Patch(harmony, AccessTools.Method(typeof(Pawn), "Tick")',
  'Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick")',
  'Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick")',
  'Patch(harmony, AccessTools.Method(typeof(Map), "MapPostTick")',
  'Patch(harmony, AccessTools.Method(typeof(World), "WorldTick")',
  'Patch(harmony, AccessTools.Method(typeof(Storyteller), "StorytellerTick")',
  'DiagnosticsV02.Apply(harmony);',
  'DiagnosticsV03.Apply(harmony);'
)){
  if($diag.Contains($forbidden)){ throw "T35-B permanent hot path still installed: $forbidden" }
}

$controller=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/BurstProbeControllerT35B.cs') -Raw
foreach($required in @(
  'TriggerUs = 100000L',
  'BurstTicks = 6',
  'CooldownTicks = 120',
  'PatchPatherChildren();',
  'RemoveHotProbes();'
)){
  if(-not $controller.Contains($required)){ throw "T35-B controller invariant missing: $required" }
}

$mainRoot=Join-Path $root 'RimMT/Source/RimMT'
foreach($token in @('parallel.aggressiveScanner','AggressiveParallelScanner093T34D')){
  $hit=Get-ChildItem $mainRoot -Recurse -Filter '*.cs' -File | Select-String -SimpleMatch $token | Select-Object -First 1
  if($hit){ throw "T35-B aggressiveScanner regression: $token at $($hit.Path):$($hit.LineNumber)" }
}

$buildRoot=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$mainRootStage=Join-Path $buildRoot 'stage-t35b-main'
$diagRootStage=Join-Path $buildRoot 'stage-t35b-diag'
$bundle=Join-Path $buildRoot 'stage-t35b-bundle'
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

$mainZip=Join-Path $buildRoot 'RimMT_V0.9.3_T35B_BurstDiagnosticsPair.zip'
$diagZip=Join-Path $buildRoot 'RimMT_Diagnostics_v0.22.0_T35B_Burst.zip'
$bundleZip=Join-Path $buildRoot 'RimMT_T35B_BurstDiagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
