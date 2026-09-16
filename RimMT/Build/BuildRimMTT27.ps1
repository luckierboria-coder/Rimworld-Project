$ErrorActionPreference='Stop'

$steps=@(
'ApplyUnifiedLeanTransforms.ps1','ApplyUnifiedLeanV23S4Pruners.ps1','ApplyUnifiedLeanV24S4TelemetryAndPruners.ps1','ApplyUnifiedLeanV26Consolidated.ps1',
'ApplyRimMTV093T0TailObservatory.ps1','ApplyRimMTV093T1TailAttribution.ps1','ApplyRimMTV093T1ANoGlobalFuse.ps1','ApplyRimMTV093T2PawnTailAttribution.ps1',
'ApplyRimMTV093T3HeavyWorkGiverEarlyRescue.ps1','ApplyRimMTV093T4HaulMergePartnerIndex.ps1','ApplyRimMTV093T5HaulMergePatchCensus.ps1','ApplyRimMTV093T8T5CarrierPruners.ps1',
'ApplyRimMTV093T13PawnTickAggregateAttribution.ps1','ApplyRimMTV093T14PlayerHumanResidualAttribution.ps1','ApplyRimMTV093T15WorkGiverDeepAttribution.ps1','ApplyRimMTV093T16GenClosestDeepAttribution.ps1',
'ApplyRimMTV093T17HaulUrgentlyDynamicRescue.ps1','ApplyRimMTV093T18MobileSourceRescueStorytellerDeep.ps1','ApplyRimMTV093T19QuestDeepMobileGlobalRescue.ps1',
'ApplyRimMTV093T20FoundationTransactionCore.ps1','ApplyRimMTV093T21FoundationReachChain.ps1','ApplyRimMTV093T22FoundationGenClosestSMF.ps1','ApplyRimMTV093T23TailContainmentCompat.ps1',
'ApplyRimMTV093T23Finalize.ps1','ApplyRimMTV093T24StutterFirst.ps1','ApplyRimMTV093T24_1GenericDefSafety.ps1','ApplyRimMTV093T26EngineParallel.ps1','ApplyRimMTV093T26_1ZeroWaitFightFires.ps1',
'ApplyRimMTV093T27ParallelWorkKernel.ps1'
)
foreach($s in $steps){
  Write-Host "== $s =="
  & (Join-Path $PSScriptRoot $s)
  if(-not $?){ throw "$s failed" }
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$proj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
dotnet restore $proj
if($LASTEXITCODE -ne 0){ throw 'dotnet restore failed' }
dotnet build $proj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'dotnet build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27-parallel-work-kernel'){ throw 'T27 version marker missing' }
if($boot -notmatch 'ParallelWorkKernel093T27\.Apply'){ throw 'T27 kernel is not installed' }
if($boot -match 'SingleCallCandidatePartition\.Apply'){ throw 'T26 low-ROI same-call partition returned' }
if($boot -match 'AsyncJobCandidatePlan04182\.Apply' -or $boot -match 'JobDecisionRootAttribution093T25\.Apply'){ throw 'T25 experiment leaked into T27' }

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys')
$b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate T26.1 zero-wait kernel' }
$epochKernel=$epoch.Substring($a,$b-$a)
foreach($forbidden in @('SpinWait','ParallelFor','.Wait(','.Join(','Thread.Sleep','ManualResetEvent')){
  if($epochKernel -match [regex]::Escape($forbidden)){ throw "T27 inherited zero-wait violation: $forbidden" }
}

$kernelPath=Join-Path $root 'RimMT/Source/RimMT/AI/ParallelWorkKernel093T27.cs'
$kernel=Get-Content $kernelPath -Raw
foreach($required in @('FeatureId = "parallel.workKernel"','SnapshotParallel','fullParallel=OFF, waits=0','PersistentMapSearchFabric.TryGetSourceSnapshot','scheduler.TryEnqueue','customGlobalSearchSet = slot.Plan.OrderedThings')){
  if(-not $kernel.Contains($required)){ throw "T27 kernel marker missing: $required" }
}
foreach($forbidden in @('SpinWait','.Wait(','.Join(','Thread.Sleep','ManualResetEvent')){
  if($kernel -match [regex]::Escape($forbidden)){ throw "T27 blocking primitive found: $forbidden" }
}

$workerStart=$kernel.IndexOf('bool accepted = scheduler.TryEnqueue')
$workerEnd=$kernel.IndexOf('if (accepted)',$workerStart)
if($workerStart -lt 0 -or $workerEnd -lt 0){ throw 'Cannot isolate T27 worker delegate' }
$worker=$kernel.Substring($workerStart,$workerEnd-$workerStart)
foreach($unsafe in @('.Position','.Map','.Spawned','CanReach','HasJobOn','JobOnThing','Reserve(','Reservation','Pawn ','UnityEngine')){
  if($worker -match [regex]::Escape($unsafe)){ throw "T27 worker dereferences unsafe gameplay state: $unsafe" }
}

$fabric=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabric.cs') -Raw
if($fabric -notmatch 'internal static partial class PersistentMapSearchFabric'){ throw 'PersistentMapSearchFabric is not partial in T27' }
if($fabric -notmatch 'internal sealed partial class SourceSnapshot'){ throw 'SourceSnapshot is not partial in T27' }
$plan=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabricT27.cs') -Raw
$pa=$plan.IndexOf('internal DistancePlan BuildDistancePlan')
$pb=$plan.IndexOf('internal sealed class DistancePlan',$pa)
if($pa -lt 0 -or $pb -lt 0){ throw 'Cannot isolate T27 distance plan worker code' }
$planWorker=$plan.Substring($pa,$pb-$pa)
foreach($unsafe in @('.Position','.Map','.Spawned','CanReach','HasJobOn','JobOnThing','Reserve(','Reservation','Pawn ','UnityEngine')){
  if($planWorker -match [regex]::Escape($unsafe)){ throw "T27 distance planner dereferences unsafe gameplay state: $unsafe" }
}

$csproj=Get-Content $proj -Raw
if(-not $csproj.Contains('<Compile Remove="AI\SingleCallCandidatePartition.cs" />')){ throw 'candidate partition is not excluded' }
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
$zip=Join-Path $build 'RimMT_V0.9.3_T27_Parallel_Work_Kernel.zip'
Compress-Archive -Path $stage -DestinationPath $zip -Force
Write-Host "Built $zip"
