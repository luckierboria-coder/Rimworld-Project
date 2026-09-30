param([switch]$ValidateCurrentTree)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT34C9.ps1')
  if(-not $?){ throw 'T34-C.9 prerequisite build failed' }
  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T34C10TargetCountParallelFabric.ps1')
  if(-not $?){ throw 'T34-C.10 transform failed' }
}

dotnet build (Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T34-C.10 build failed' }
dotnet build (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.20.2 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @('0.9.3-t34c10-target-count-parallel-fabric','TargetCountParallelFabric093T34C10.Apply(harmony);')){
  if(-not $boot.Contains($required)){ throw "T34-C.10 bootstrap invariant missing: $required" }
}

$fabricPath=Join-Path $root 'RimMT/Source/RimMT/AI/TargetCountParallelFabric093T34C10.cs'
if(-not (Test-Path $fabricPath)){ throw 'T34-C.10 fabric source was not materialized' }
$fabric=Get-Content $fabricPath -Raw
foreach($required in @(
  'internal const string FeatureId = "parallel.targetCountProducts"',
  'scheduler.TryEnqueue(', 'byte[] visible', 'plan.Tick != tick', 'plan.Generation != generation',
  'counter.CountValidThing(inner, bill, product)', 'counter.GetType() != typeof(RecipeWorkerCounter)',
  'Same-tick only; worker input is primitive-only; no wait'
)){
  if(-not $fabric.Contains($required)){ throw "T34-C.10 fabric invariant missing: $required" }
}
$workerStart=$fabric.IndexOf('private static void Build(')
$workerEnd=$fabric.IndexOf('private static int CountCarriedLive', $workerStart)
if($workerStart -lt 0 -or $workerEnd -le $workerStart){ throw 'T34-C.10 worker boundary not found' }
$worker=$fabric.Substring($workerStart,$workerEnd-$workerStart)
foreach($forbidden in @('Thing','Map','Bill','CanReach','CanReserve','Job','Fogged','Harmony','Thread.Sleep','SpinWait','.Wait(')){
  if($worker.Contains($forbidden)){ throw "T34-C.10 worker boundary violation token: $forbidden" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.20\.2"'){ throw 'Diagnostics v0.20.2 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('"RimMT.TargetCountParallelFabric093T34C10"')){ throw 'Diagnostics T34-C.10 bridge missing' }

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$bundle=Join-Path $build 'stage-t34c10-bundle'
if(Test-Path $bundle){ Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
$mainStage=Join-Path $bundle 'RimMT'; $diagStage=Join-Path $bundle 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
$bundleZip=Join-Path $build 'RimMT_T34C10_TargetCountParallelFabric_Bundle.zip'
if(Test-Path $bundleZip){ Remove-Item $bundleZip -Force }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $bundleZip"
