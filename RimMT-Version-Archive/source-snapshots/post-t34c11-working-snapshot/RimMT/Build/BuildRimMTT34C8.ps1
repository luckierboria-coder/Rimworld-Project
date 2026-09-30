param([switch]$ValidateCurrentTree)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

if(-not $ValidateCurrentTree){
  & (Join-Path $PSScriptRoot 'BuildRimMTT34C.ps1')
  if(-not $?){ throw 'T34-C.7 prerequisite build failed' }
  & (Join-Path $PSScriptRoot 'ApplyRimMTV093T34C8RootFrameStallCensus.ps1')
  if(-not $?){ throw 'T34-C.8 transform failed' }

  dotnet build (Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj') --configuration Release --no-restore
  if($LASTEXITCODE -ne 0){ throw 'RimMT T34-C.8 build failed' }
  dotnet build (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj') --configuration Release --no-restore
  if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.20.0 build failed' }
}

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
foreach($required in @('0.9.3-t34c8-root-frame-stall-census','RootFrameStallCensus093T34C8.Apply(harmony);')){
  if(-not $boot.Contains($required)){ throw "T34-C.8 bootstrap invariant missing: $required" }
}

$censusPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RootFrameStallCensus093T34C8.cs'
if(-not (Test-Path $censusPath)){ throw 'T34-C.8 census source was not materialized' }
$census=Get-Content $censusPath -Raw
foreach($required in @(
  'internal const string FeatureId = "diagnostics.rootFrameStalls"',
  'AccessTools.Method(typeof(Root_Play), "Update")',
  '"OutsideRoot"','"RootUpdate"','Application.isFocused','Find.TickManager.Paused',
  'GC.CollectionCount(0)','Root_Play.Update patches','Priority.First','Priority.Last'
)){
  if(-not $census.Contains($required)){ throw "T34-C.8 census invariant missing: $required" }
}
foreach($forbidden in @('SpinWait','.Wait(','Thread.Sleep(','Task.Run(','ThreadPool.QueueUserWorkItem','StartJob(','EndCurrentJob(')){
  if($census.Contains($forbidden)){ throw "T34-C.8 measurement-only violation token: $forbidden" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.20\.0"'){ throw 'Diagnostics v0.20.0 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
foreach($required in @('"RimMT.RootFrameStallCensus093T34C8"','"Root_Play.Update"')){
  if(-not $diagReport.Contains($required)){ throw "Diagnostics T34-C.8 bridge missing: $required" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$bundle=Join-Path $build 'stage-t34c8-bundle'
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
$bundleZip=Join-Path $build 'RimMT_T34C8_RootFrameStallCensus_Bundle.zip'
if(Test-Path $bundleZip){ Remove-Item $bundleZip -Force }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $bundleZip"
