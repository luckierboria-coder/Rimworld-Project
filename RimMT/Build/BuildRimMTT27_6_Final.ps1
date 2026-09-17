$ErrorActionPreference='Stop'

# Self-contained final builder: run the full historical transform chain, apply T27.6, repair the
# known PowerShell literal-newline artifact before C# compilation, remove the last resident T15/T16
# diagnostic entry points, then compile/assert/package once from the final production tree.
$steps=@(
'ApplyUnifiedLeanTransforms.ps1','ApplyUnifiedLeanV23S4Pruners.ps1','ApplyUnifiedLeanV24S4TelemetryAndPruners.ps1','ApplyUnifiedLeanV26Consolidated.ps1',
'ApplyRimMTV093T0TailObservatory.ps1','ApplyRimMTV093T1TailAttribution.ps1','ApplyRimMTV093T1ANoGlobalFuse.ps1','ApplyRimMTV093T2PawnTailAttribution.ps1',
'ApplyRimMTV093T3HeavyWorkGiverEarlyRescue.ps1','ApplyRimMTV093T4HaulMergePartnerIndex.ps1','ApplyRimMTV093T5HaulMergePatchCensus.ps1','ApplyRimMTV093T8T5CarrierPruners.ps1',
'ApplyRimMTV093T13PawnTickAggregateAttribution.ps1','ApplyRimMTV093T14PlayerHumanResidualAttribution.ps1','ApplyRimMTV093T15WorkGiverDeepAttribution.ps1','ApplyRimMTV093T16GenClosestDeepAttribution.ps1',
'ApplyRimMTV093T17HaulUrgentlyDynamicRescue.ps1','ApplyRimMTV093T18MobileSourceRescueStorytellerDeep.ps1','ApplyRimMTV093T19QuestDeepMobileGlobalRescue.ps1',
'ApplyRimMTV093T20FoundationTransactionCore.ps1','ApplyRimMTV093T21FoundationReachChain.ps1','ApplyRimMTV093T22FoundationGenClosestSMF.ps1','ApplyRimMTV093T23TailContainmentCompat.ps1',
'ApplyRimMTV093T23Finalize.ps1','ApplyRimMTV093T24StutterFirst.ps1','ApplyRimMTV093T24_1GenericDefSafety.ps1','ApplyRimMTV093T26EngineParallel.ps1','ApplyRimMTV093T26_1ZeroWaitFightFires.ps1',
'ApplyRimMTV093T27ParallelWorkKernel.ps1','ApplyRimMTV093T27_1WorkPlanWindow.ps1','ApplyRimMTV093T27_2SafetyLayerReset.ps1','ApplyRimMTV093T27_3WaitStallTrace.ps1','ApplyRimMTV093T27_4DiagnosticsSplit.ps1','ApplyRimMTV093T27_5StutterFoundation.ps1','ApplyRimMTV093T27_6ProductionLean.ps1'
)
foreach($s in $steps){ Write-Host "== $s =="; & (Join-Path $PSScriptRoot $s); if(-not $?){ throw "$s failed" } }

# T27.6 admission-summary insertion used a literal `n token inside a single-quoted PowerShell
# replacement. Normalize exactly that generated token before compilation; no runtime logic changes.
$reachPath=Join-Path $PSScriptRoot '../Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reachPath=(Resolve-Path $reachPath).Path
$reach=Get-Content $reachPath -Raw
$bad=@'
 +`n                ", eligible=" +
'@
$good=@'
 +
                ", eligible=" +
'@
if($reach.Contains($bad)){
  $reach=$reach.Replace($bad,$good)
  Set-Content $reachPath $reach -Encoding UTF8
}

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T27_6ProductionLeanFinalize.ps1')
if(-not $?){ throw 'T27.6 production-lean finalizer failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet restore $mainProj; if($LASTEXITCODE -ne 0){ throw 'RimMT restore failed' }
dotnet build $mainProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'RimMT build failed' }
dotnet restore $diagProj; if($LASTEXITCODE -ne 0){ throw 'Diagnostics restore failed' }
dotnet build $diagProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'Diagnostics build failed' }

# ---- production-lean assertions ----
$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27\.6-production-lean'){ throw 'T27.6 version marker missing' }
foreach($forbidden in @(
'TailAttributionPatches093T1.Apply(harmony);','TailPawnPatches093T2.Apply(harmony);','TailPathfinderPatches093T3.Apply(harmony);',
'PlayerHumanResidualPatches093T14.Apply(harmony);','StorytellerDeepAttribution093T18.Apply(harmony);','QuestDeepAttribution093T19.Apply(harmony);',
'WorldTailBoundary093T22.Apply(harmony);','WorldRootAttribution093T22.Apply(harmony);','DoBillTailFabric092.Apply(harmony);',
'ParallelWorkKernel093T27.Apply(harmony);','MobileSourceRescue093T18.Apply(harmony);','WaitStallPatches093T27_3.Apply(harmony);',
'WorkGiverDetailPatches.Initialize(harmony);')){
  if($boot.Contains($forbidden)){ throw "T27.6 retired/diagnostic install present: $forbidden" }
}

$runtime=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs') -Raw
if($runtime -match 'WorkGiverDeepAttribution093T15\.OnMainThreadFrame\(\)'){ throw 'T15 per-frame coordinator poll remains in production' }

$rimmtPatches=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Patches/RimMTPatches.cs') -Raw
if($rimmtPatches -match 'TailObservatory093T0\.BeginTick|TailObservatory093T0\.RecordTick'){ throw 'T0 observatory still owns production DoSingleTick sampler' }
if($rimmtPatches -notmatch '!FeatureGate\.IsEnabled\("runtime\.adaptiveBurst"\) \|\| RuntimeCompatibility\.ButterPlusPlusActive'){ throw 'production adaptive tick gate not restored' }

$reach=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs') -Raw
foreach($required in @('admissionCompatibilityBypass','admissionFeatureGateBypass','admissionThreadBypass','admissionProgramStateBypass','admissionBypass[compat/gate/thread/state]')){
  if(-not $reach.Contains($required)){ throw "T27.6 Reach admission marker missing: $required" }
}
if($reach -match 'TailObservatory093T0\.NoteReach'){ throw 'T0 Reach correlation callback remains in production path' }
if($reach -match '\+`n'){ throw 'Generated C# still contains literal PowerShell newline token' }

$s4=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs') -Raw
if($s4 -match 'TailObservatory093T0\.NoteS4HeavyValidator'){ throw 'T0 S4 correlation callback remains' }

$bill=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs') -Raw
if($bill -match 'GetPackageReadiness\(giver, pawn\.Map, things\)' -or $bill -match 'GetPackageReadiness\(__instance, pawn\.Map, things\)'){ throw 'ineffective T27.5 package readiness route still active' }
if($bill -notmatch 'stack\.AnyShouldDoNow'){ throw 'DoBill live readiness authority missing' }

$merge=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs') -Raw
foreach($required in @('authorityTargetForeign','authorityThingWithCompsForeign','authorityMinifiedForeign','authorityThingUnsafe','authorityForeign[target/thingWithComps/minified/thingUnsafe]')){
  if(-not $merge.Contains($required)){ throw "T27.6 HaulMerge authority census marker missing: $required" }
}

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate T26.1 zero-wait kernel' }
$epochKernel=$epoch.Substring($a,$b-$a)
foreach($forbidden in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){ if($epochKernel -match [regex]::Escape($forbidden)){ throw "T27.6 inherited zero-wait violation: $forbidden" } }
$world=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/WorldRootAttribution093T22.cs') -Raw
if($world -notmatch 'genericHarmony=OFF'){ throw 'T24.1 generic-Harmony safety marker missing' }
if($world -match 'PatchTraitDefDatabaseSignals\(harmony\)'){ throw 'closed generic TraitDef Harmony patch returned' }

# ---- standalone Diagnostics v0.3 assertions ----
$diagAbout=Get-Content (Join-Path $root 'RimMTDiagnostics/About/About.xml') -Raw
if($diagAbout -notmatch 'RimMT Diagnostics v0\.3'){ throw 'Diagnostics v0.3 metadata missing' }
$diagProjText=Get-Content $diagProj -Raw
if($diagProjText -match 'ProjectReference' -or $diagProjText -match 'RimMT\.dll'){ throw 'Diagnostics must not compile-link against RimMT.dll' }
$diagSources=(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics') -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach($required in @('0.3.0','DiagnosticsV03','SlowDNJCorrelation','RecentSlowDNJ','detailPackagesRemaining','WorkGiver_Merge.JobOnThing','ThingWithComps.CanStackWith','TryEnterNextPathCell')){
  if(-not $diagSources.Contains($required)){ throw "Diagnostics v0.3 marker missing: $required" }
}
foreach($forbidden in @('StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(')){
  if($diagSources -match [regex]::Escape($forbidden)){ throw "Diagnostics unexpectedly mutates/schedules gameplay: $forbidden" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

# ---- package ----
$build=Join-Path $root 'build'; New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-main-final'; $diagRoot=Join-Path $build 'stage-diag-final'; $bundleStage=Join-Path $build 'stage-bundle-final'
foreach($p in @($mainRoot,$diagRoot,$bundleStage)){ if(Test-Path $p){ Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force -Path $p | Out-Null }
$mainStage=Join-Path $mainRoot 'RimMT'; $diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
if(Test-Path (Join-Path $root 'RimMT/README.md')){ Copy-Item (Join-Path $root 'RimMT/README.md') $mainStage }
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
if(Test-Path (Join-Path $root 'RimMTDiagnostics/README.md')){ Copy-Item (Join-Path $root 'RimMTDiagnostics/README.md') $diagStage }
$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.6_Production.zip'; $diagZip=Join-Path $build 'RimMT_Diagnostics_v0.3.zip'; $bundleZip=Join-Path $build 'RimMT_T27.6_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force; Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundleStage 'RimMT') -Recurse; Copy-Item $diagStage (Join-Path $bundleStage 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundleStage '*') -DestinationPath $bundleZip -Force

Write-Host 'Built final T27.6 production-lean packages with resident T15/T16 runtime hooks removed.'
