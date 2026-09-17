$ErrorActionPreference='Stop'

# First run the full historical transform/build/assertion chain.
& (Join-Path $PSScriptRoot 'BuildRimMTT27_6.ps1')
if(-not $?){ throw 'Base T27.6 build failed' }

# Then remove the last T15/T16 resident diagnostic entry points that are injected by historical
# transforms but no longer belong in RimMT production. Rebuild and repackage from the cleaned tree.
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T27_6ProductionLeanFinalize.ps1')
if(-not $?){ throw 'T27.6 production-lean finalizer failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Final RimMT rebuild failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Final Diagnostics rebuild failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -match 'WorkGiverDetailPatches\.Initialize\(harmony\)'){ throw 'T15 WorkGiver detail manager still initialized in production' }
$runtime=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs') -Raw
if($runtime -match 'WorkGiverDeepAttribution093T15\.OnMainThreadFrame\(\)'){ throw 'T15 per-frame coordinator poll remains in production' }

# Explicitly assert the resident hot-path diagnostic installs remain gone after finalization.
foreach($forbidden in @(
'TailAttributionPatches093T1.Apply(harmony);','TailPawnPatches093T2.Apply(harmony);','TailPathfinderPatches093T3.Apply(harmony);',
'PlayerHumanResidualPatches093T14.Apply(harmony);','StorytellerDeepAttribution093T18.Apply(harmony);','QuestDeepAttribution093T19.Apply(harmony);',
'WorldTailBoundary093T22.Apply(harmony);','WorldRootAttribution093T22.Apply(harmony);','DoBillTailFabric092.Apply(harmony);')){
  if($boot.Contains($forbidden)){ throw "Resident diagnostic install returned after finalization: $forbidden" }
}

# Repackage cleaned production and the already-built Diagnostics v0.3.
$build=Join-Path $root 'build'
$mainRoot=Join-Path $build 'stage-main-final'; $diagRoot=Join-Path $build 'stage-diag-final'; $bundleStage=Join-Path $build 'stage-bundle-final'
foreach($p in @($mainRoot,$diagRoot,$bundleStage)){ if(Test-Path $p){ Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force -Path $p | Out-Null }
$mainStage=Join-Path $mainRoot 'RimMT'; $diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
if(Test-Path (Join-Path $root 'RimMT/README.md')){ Copy-Item (Join-Path $root 'RimMT/README.md') $mainStage }
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
if(Test-Path (Join-Path $root 'RimMTDiagnostics/README.md')){ Copy-Item (Join-Path $root 'RimMTDiagnostics/README.md') $diagStage }

$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.6_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.3.zip'
$bundleZip=Join-Path $build 'RimMT_T27.6_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundleStage 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundleStage 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundleStage '*') -DestinationPath $bundleZip -Force

Write-Host 'Built final T27.6 production-lean packages with resident T15/T16 runtime hooks removed.'
