param([switch]$ValidateCurrentTree)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT34C10.ps1')
  if(-not $?){ throw 'T34-C.10 prerequisite build failed' }
  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T34C11HaulEligibilityParallelFabric.ps1')
  if(-not $?){ throw 'T34-C.11 transform failed' }
}

dotnet build (Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T34-C.11 build failed' }
dotnet build (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.20.3 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @('0.9.3-t34c11-haul-eligibility-parallel-fabric','HaulToInventoryParallelEligibility093T34C11.Apply(harmony);')){
  if(-not $boot.Contains($required)){ throw "T34-C.11 bootstrap invariant missing: $required" }
}

$fabricPath=Join-Path $root 'RimMT/Source/RimMT/AI/HaulToInventoryParallelEligibility093T34C11.cs'
if(-not (Test-Path $fabricPath)){ throw 'T34-C.11 fabric source was not materialized' }
$fabric=Get-Content $fabricPath -Raw
foreach($required in @(
  'internal const string FeatureId = "parallel.haulToInventoryEligibility"',
  'PotentialWorkThingsGlobal', 'HasJobOnThing', 'scheduler.TryEnqueue(',
  'byte[] accepts', 'LiveProvesNoHigherPriorityDestination',
  'Array.BinarySearch(plan.NegativeThingIds', 'every rejection is re-proved live; no wait'
)){
  if(-not $fabric.Contains($required)){ throw "T34-C.11 fabric invariant missing: $required" }
}
$workerStart=$fabric.IndexOf('private static void Build(')
$workerEnd=$fabric.IndexOf('private static void Publish(', $workerStart)
if($workerStart -lt 0 -or $workerEnd -le $workerStart){ throw 'T34-C.11 worker boundary not found' }
$worker=$fabric.Substring($workerStart,$workerEnd-$workerStart)
foreach($forbidden in @('StoreUtility','IHaulDestination','.Accepts(','CanReach','CanReserve','Pawn ','Thing ','Find.','Current.','Thread.Sleep','SpinWait','.Wait(')){
  if($worker.Contains($forbidden)){ throw "T34-C.11 worker boundary violation token: $forbidden" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.20\.3"' -or -not $diag.Contains('MapPostTickComponentCensus.Apply(harmony);')){
  throw 'Diagnostics v0.20.3 MapPostTick census install missing'
}
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
foreach($required in @('MapPostTickComponentCensus.Summary()','"RimMT.HaulToInventoryParallelEligibility093T34C11"','PickUpAndHaul.WorkGiver_HaulToInventory.HasJobOnThing')){
  if(-not $diagReport.Contains($required)){ throw "Diagnostics C11 invariant missing: $required" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$bundle=Join-Path $build 'stage-t34c11-bundle'
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
$bundleZip=Join-Path $build 'RimMT_T34C11_HaulEligibilityParallelFabric_Bundle.zip'
if(Test-Path $bundleZip){ Remove-Item $bundleZip -Force }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $bundleZip"
