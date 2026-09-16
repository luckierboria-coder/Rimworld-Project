$ErrorActionPreference='Stop'

$steps=@(
'ApplyUnifiedLeanTransforms.ps1',
'ApplyUnifiedLeanV23S4Pruners.ps1',
'ApplyUnifiedLeanV24S4TelemetryAndPruners.ps1',
'ApplyUnifiedLeanV26Consolidated.ps1',
'ApplyRimMTV093T0TailObservatory.ps1',
'ApplyRimMTV093T1TailAttribution.ps1',
'ApplyRimMTV093T1ANoGlobalFuse.ps1',
'ApplyRimMTV093T2PawnTailAttribution.ps1',
'ApplyRimMTV093T3HeavyWorkGiverEarlyRescue.ps1',
'ApplyRimMTV093T4HaulMergePartnerIndex.ps1',
'ApplyRimMTV093T5HaulMergePatchCensus.ps1',
'ApplyRimMTV093T8T5CarrierPruners.ps1',
'ApplyRimMTV093T13PawnTickAggregateAttribution.ps1',
'ApplyRimMTV093T14PlayerHumanResidualAttribution.ps1',
'ApplyRimMTV093T15WorkGiverDeepAttribution.ps1',
'ApplyRimMTV093T16GenClosestDeepAttribution.ps1',
'ApplyRimMTV093T17HaulUrgentlyDynamicRescue.ps1',
'ApplyRimMTV093T18MobileSourceRescueStorytellerDeep.ps1',
'ApplyRimMTV093T19QuestDeepMobileGlobalRescue.ps1',
'ApplyRimMTV093T20FoundationTransactionCore.ps1',
'ApplyRimMTV093T21FoundationReachChain.ps1',
'ApplyRimMTV093T22FoundationGenClosestSMF.ps1',
'ApplyRimMTV093T23TailContainmentCompat.ps1',
'ApplyRimMTV093T23Finalize.ps1',
'ApplyRimMTV093T24StutterFirst.ps1',
'ApplyRimMTV093T24_1GenericDefSafety.ps1',
'ApplyRimMTV093T26EngineParallel.ps1',
'ApplyRimMTV093T26_1ZeroWaitFightFires.ps1'
)
foreach($s in $steps){
  Write-Host "== $s =="
  & (Join-Path $PSScriptRoot $s)
  if($LASTEXITCODE){ throw "$s failed with exit code $LASTEXITCODE" }
}

$root=Resolve-Path (Join-Path $PSScriptRoot '../..')
$proj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
dotnet restore $proj
if($LASTEXITCODE){ throw 'dotnet restore failed' }
dotnet build $proj --configuration Release --no-restore
if($LASTEXITCODE){ throw 'dotnet build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t26\.1-zero-wait-fightfires'){ throw 'T26.1 version marker missing' }
if($boot -match 'SingleCallCandidatePartition\.Apply'){ throw 'T26.1 regression: candidate partition still installed' }
if($boot -match 'AsyncJobCandidatePlan04182\.Apply' -or $boot -match 'JobDecisionRootAttribution093T25\.Apply'){ throw 'T25 experiment leaked into T26.1' }

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys')
$b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate T26.1 kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($forbidden in @('SpinWait','ParallelFor','.Wait(','.Join(','Thread.Sleep','ManualResetEvent')){
  if($kernel -match [regex]::Escape($forbidden)){ throw "T26.1 zero-wait violation: $forbidden" }
}

$csproj=Get-Content $proj -Raw
if($csproj -notmatch '<Compile Remove="AI\\SingleCallCandidatePartition\.cs"'){ throw 'T26.1 candidate partition is not excluded' }

$s4=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs') -Raw
foreach($marker in @('TargetedPrefilterKind.FightFires','RimWorld.WorkGiver_FightFires','targetedFightFiresRejected','worker.Map.areaManager.Home[fire.Position]')){
  if($s4 -notmatch [regex]::Escape($marker)){ throw "FightFires marker missing: $marker" }
}
if($s4 -notmatch 'IsTargetedPrefilterAuthoritySafe'){ throw 'FightFires authority safety gate missing' }

$world=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/WorldRootAttribution093T22.cs') -Raw
if($world -notmatch 'genericHarmony=OFF'){ throw 'T24.1 generic-Harmony safety marker missing' }

$dllDir=Join-Path $root 'RimMT/1.5/Assemblies'
$dlls=@(Get-ChildItem $dllDir -Filter '*.dll' -File)
if($dlls.Count -ne 1 -or $dlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected DLL set: $($dlls.Name -join ', ')" }

$build=Join-Path $root 'build'
$stage=Join-Path $build 'RimMT'
if(Test-Path $stage){ Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $stage
if(Test-Path (Join-Path $root 'RimMT/README.md')){ Copy-Item (Join-Path $root 'RimMT/README.md') $stage }
$zip=Join-Path $build 'RimMT_V0.9.3_T26.1_Zero_Wait_FightFires.zip'
Compress-Archive -Path $stage -DestinationPath $zip -Force
Write-Host "Built $zip"
