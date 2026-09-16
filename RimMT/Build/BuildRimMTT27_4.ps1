$ErrorActionPreference='Stop'

$steps=@(
'ApplyUnifiedLeanTransforms.ps1','ApplyUnifiedLeanV23S4Pruners.ps1','ApplyUnifiedLeanV24S4TelemetryAndPruners.ps1','ApplyUnifiedLeanV26Consolidated.ps1',
'ApplyRimMTV093T0TailObservatory.ps1','ApplyRimMTV093T1TailAttribution.ps1','ApplyRimMTV093T1ANoGlobalFuse.ps1','ApplyRimMTV093T2PawnTailAttribution.ps1',
'ApplyRimMTV093T3HeavyWorkGiverEarlyRescue.ps1','ApplyRimMTV093T4HaulMergePartnerIndex.ps1','ApplyRimMTV093T5HaulMergePatchCensus.ps1','ApplyRimMTV093T8T5CarrierPruners.ps1',
'ApplyRimMTV093T13PawnTickAggregateAttribution.ps1','ApplyRimMTV093T14PlayerHumanResidualAttribution.ps1','ApplyRimMTV093T15WorkGiverDeepAttribution.ps1','ApplyRimMTV093T16GenClosestDeepAttribution.ps1',
'ApplyRimMTV093T17HaulUrgentlyDynamicRescue.ps1','ApplyRimMTV093T18MobileSourceRescueStorytellerDeep.ps1','ApplyRimMTV093T19QuestDeepMobileGlobalRescue.ps1',
'ApplyRimMTV093T20FoundationTransactionCore.ps1','ApplyRimMTV093T21FoundationReachChain.ps1','ApplyRimMTV093T22FoundationGenClosestSMF.ps1','ApplyRimMTV093T23TailContainmentCompat.ps1',
'ApplyRimMTV093T23Finalize.ps1','ApplyRimMTV093T24StutterFirst.ps1','ApplyRimMTV093T24_1GenericDefSafety.ps1','ApplyRimMTV093T26EngineParallel.ps1','ApplyRimMTV093T26_1ZeroWaitFightFires.ps1',
'ApplyRimMTV093T27ParallelWorkKernel.ps1','ApplyRimMTV093T27_1WorkPlanWindow.ps1','ApplyRimMTV093T27_2SafetyLayerReset.ps1','ApplyRimMTV093T27_3WaitStallTrace.ps1','ApplyRimMTV093T27_4DiagnosticsSplit.ps1'
)
foreach($s in $steps){
  Write-Host "== $s =="
  & (Join-Path $PSScriptRoot $s)
  if(-not $?){ throw "$s failed" }
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet restore $mainProj
if($LASTEXITCODE -ne 0){ throw 'RimMT restore failed' }
dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT build failed' }

dotnet restore $diagProj
if($LASTEXITCODE -ne 0){ throw 'Diagnostics restore failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics build failed' }

# ---- production split assertions ----
$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27\.4-diagnostics-split'){ throw 'T27.4 version marker missing' }
if($boot -match 'ParallelWorkKernel093T27\.Apply\(harmony\)'){ throw 'retired T27 work kernel returned' }
if($boot -match 'MobileSourceRescue093T18\.Apply\(harmony\)'){ throw 'retired T18/T19 mobile source rescue returned' }

$patch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs') -Raw
if($patch -match 'WaitStallTrace093T27_3'){ throw 'Wait-stall diagnostics still execute in RimMT.dll' }

$mainProjText=Get-Content $mainProj -Raw
if($mainProjText -notmatch 'Compile Remove="Diagnostics\\WaitStallTrace093T27_3.cs"'){ throw 'Wait tracer is not excluded from RimMT.dll' }

# Historical hard safety invariants remain mandatory.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys')
$b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate T26.1 zero-wait kernel' }
$epochKernel=$epoch.Substring($a,$b-$a)
foreach($forbidden in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($epochKernel -match [regex]::Escape($forbidden)){ throw "T27.4 inherited zero-wait violation: $forbidden" }
}
$world=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/WorldRootAttribution093T22.cs') -Raw
if($world -notmatch 'genericHarmony=OFF'){ throw 'T24.1 generic-Harmony safety marker missing' }
if($world -match 'PatchTraitDefDatabaseSignals\(harmony\)'){ throw 'closed generic TraitDef Harmony patch returned' }

# ---- standalone Diagnostics assertions ----
$diagAbout=Get-Content (Join-Path $root 'RimMTDiagnostics/About/About.xml') -Raw
if($diagAbout -notmatch '<packageId>allen\.rimmt\.diagnostics</packageId>'){ throw 'Diagnostics packageId missing' }
if($diagAbout -notmatch '<packageId>allen\.rimmt</packageId>'){ throw 'Diagnostics RimMT dependency missing' }
$diagProjText=Get-Content $diagProj -Raw
if($diagProjText -match 'ProjectReference' -or $diagProjText -match 'RimMT\.dll'){ throw 'Diagnostics must not compile-link against RimMT.dll' }

$diagSources=(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics') -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach($required in @('allen.rimmt.diagnostics','DiagnosticsHub','ObserveDetermine','HarmonyAudit','RimMTBridge','EnableSearchTiming','Write report to Config folder')){
  if(-not $diagSources.Contains($required)){ throw "Diagnostics marker missing: $required" }
}
foreach($forbidden in @('StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(')){
  if($diagSources -match [regex]::Escape($forbidden)){ throw "Diagnostics unexpectedly mutates/schedules gameplay: $forbidden" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

# ---- package ----
$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainStage=Join-Path $build 'stage-main/RimMT'
$diagStage=Join-Path $build 'stage-diag/RimMTDiagnostics'
$bundleStage=Join-Path $build 'stage-bundle'
foreach($p in @((Split-Path $mainStage),(Split-Path $diagStage),$bundleStage)){
  if(Test-Path $p){ Remove-Item $p -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $p | Out-Null
}
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.4_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.1.zip'
$bundleZip=Join-Path $build 'RimMT_T27.4_With_Diagnostics_Bundle.zip'
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundleStage 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundleStage 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundleStage '*') -DestinationPath $bundleZip -Force
Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
