param([switch]$ValidateCurrentTree)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT34C8.ps1')
  if(-not $?){ throw 'T34-C.8 prerequisite build failed' }
  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T34C9ParallelDoBillReadiness.ps1')
  if(-not $?){ throw 'T34-C.9 transform failed' }
}

dotnet build (Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T34-C.9 build failed' }
dotnet build (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj') --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.20.1 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @('0.9.3-t34c9-parallel-dobill-readiness','DoBillParallelReadinessFabric093T34C9.Apply();')){
  if(-not $boot.Contains($required)){ throw "T34-C.9 bootstrap invariant missing: $required" }
}

$fabricPath=Join-Path $root 'RimMT/Source/RimMT/AI/DoBillParallelReadinessFabric093T34C9.cs'
if(-not (Test-Path $fabricPath)){ throw 'T34-C.9 fabric source was not materialized' }
$fabric=Get-Content $fabricPath -Raw
foreach($required in @(
  'internal const string FeatureId = "parallel.doBillReadiness"',
  'scheduler.TryEnqueue(', 'CapturePrimitive(source)', 'bill.GetType() == typeof(Bill_Production)',
  'bill.repeatMode != BillRepeatModeDefOf.Forever', 'bill.paused = false',
  'Worker input is primitive-only; no wait'
)){
  if(-not $fabric.Contains($required)){ throw "T34-C.9 fabric invariant missing: $required" }
}
foreach($forbidden in @('stack.AnyShouldDoNow','CanReach(','CanReserve(','JobOnThing(','Thread.Sleep(','SpinWait','.Wait(')){
  if($fabric.Contains($forbidden)){ throw "T34-C.9 worker/main boundary violation token: $forbidden" }
}

$bill=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs') -Raw
foreach($required in @('TryGetOrSchedule(things, out readinessPlan)','TryEvaluate(','else keep = PackageReadinessShouldDoNow(stack);')){
  if(-not $bill.Contains($required)){ throw "T34-C.9 DoBill integration missing: $required" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.20\.1"'){ throw 'Diagnostics v0.20.1 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('"RimMT.DoBillParallelReadinessFabric093T34C9"')){ throw 'Diagnostics T34-C.9 bridge missing' }

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$bundle=Join-Path $build 'stage-t34c9-bundle'
if(Test-Path $bundle){ Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
$mainStage=Join-Path $bundle 'RimMT'
$diagStage=Join-Path $bundle 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
$bundleZip=Join-Path $build 'RimMT_T34C9_ParallelDoBillReadiness_Bundle.zip'
if(Test-Path $bundleZip){ Remove-Item $bundleZip -Force }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $bundleZip"
