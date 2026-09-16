$ErrorActionPreference='Stop'

$steps=@(
'ApplyUnifiedLeanTransforms.ps1','ApplyUnifiedLeanV23S4Pruners.ps1','ApplyUnifiedLeanV24S4TelemetryAndPruners.ps1','ApplyUnifiedLeanV26Consolidated.ps1',
'ApplyRimMTV093T0TailObservatory.ps1','ApplyRimMTV093T1TailAttribution.ps1','ApplyRimMTV093T1ANoGlobalFuse.ps1','ApplyRimMTV093T2PawnTailAttribution.ps1',
'ApplyRimMTV093T3HeavyWorkGiverEarlyRescue.ps1','ApplyRimMTV093T4HaulMergePartnerIndex.ps1','ApplyRimMTV093T5HaulMergePatchCensus.ps1','ApplyRimMTV093T8T5CarrierPruners.ps1',
'ApplyRimMTV093T13PawnTickAggregateAttribution.ps1','ApplyRimMTV093T14PlayerHumanResidualAttribution.ps1','ApplyRimMTV093T15WorkGiverDeepAttribution.ps1','ApplyRimMTV093T16GenClosestDeepAttribution.ps1',
'ApplyRimMTV093T17HaulUrgentlyDynamicRescue.ps1','ApplyRimMTV093T18MobileSourceRescueStorytellerDeep.ps1','ApplyRimMTV093T19QuestDeepMobileGlobalRescue.ps1',
'ApplyRimMTV093T20FoundationTransactionCore.ps1','ApplyRimMTV093T21FoundationReachChain.ps1','ApplyRimMTV093T22FoundationGenClosestSMF.ps1','ApplyRimMTV093T23TailContainmentCompat.ps1',
'ApplyRimMTV093T23Finalize.ps1','ApplyRimMTV093T24StutterFirst.ps1','ApplyRimMTV093T24_1GenericDefSafety.ps1','ApplyRimMTV093T26EngineParallel.ps1','ApplyRimMTV093T26_1ZeroWaitFightFires.ps1',
'ApplyRimMTV093T27ParallelWorkKernel.ps1','ApplyRimMTV093T27_1WorkPlanWindow.ps1','ApplyRimMTV093T27_2SafetyLayerReset.ps1','ApplyRimMTV093T27_3WaitStallTrace.ps1'
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
if($boot -notmatch '0\.9\.3-t27\.3-wait-stall-trace'){ throw 'T27.3 version marker missing' }
if($boot -match 'MobileSourceRescue093T18\.Apply\(harmony\)'){ throw 'T18/T19 mobile-source behavior is still installed' }
if($boot -match 'ParallelWorkKernel093T27\.Apply\(harmony\)'){ throw 'T27/T27.1 speculative work kernel returned' }
if($boot -notmatch 'JobSearchTransaction093T20\.Apply\(harmony\)'){ throw 'T20/T21 foundation unexpectedly removed in T27.3 A/B' }
if($boot -notmatch 'WorkGiverParallelSafety093T27_2\.Initialize\(\)'){ throw 'T27.2 safety registry missing' }
if($boot -match 'SingleCallCandidatePartition\.Apply'){ throw 'T26 low-ROI same-call partition returned' }
if($boot -match 'AsyncJobCandidatePlan04182\.Apply' -or $boot -match 'JobDecisionRootAttribution093T25\.Apply'){ throw 'T25 experiment leaked into T27.3' }

$patch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs') -Raw
if($patch -notmatch 'DeterminePostfix\(Pawn_JobTracker __instance, ThinkResult __result, long __state\)'){ throw 'Wait tracer is not wired into existing T2 Determine postfix' }
if($patch -notmatch 'WaitStallTrace093T27_3\.Observe\(__instance, __result\)'){ throw 'Wait tracer observe call missing' }

$trace=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/WaitStallTrace093T27_3.cs') -Raw
foreach($required in @('T27.3 Wait-stall trace','TopWaitSources','ActiveLongest','result[idle/nonIdle/noJob]','SourceNode')){
  if(-not $trace.Contains($required)){ throw "Wait tracer marker missing: $required" }
}
foreach($forbidden in @('harmony.Patch(','TryIssueJobPackage(','StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep')){
  if($trace -match [regex]::Escape($forbidden)){ throw "Wait tracer unexpectedly mutates/executes gameplay: $forbidden" }
}

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'V0\.9\.3-T27\.3 Wait Stall Trace'){ throw 'T27.3 report version missing' }
if($report -notmatch 'WaitStallTrace093T27_3\.Summary'){ throw 'T27.3 wait trace summary missing from report' }
if($report -notmatch 'speculative work-plan kernel: RETIRED/OFF'){ throw 'T27 work-kernel retirement status missing' }

# Historical no-wait invariant stays mandatory.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys')
$b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate T26.1 zero-wait kernel' }
$epochKernel=$epoch.Substring($a,$b-$a)
foreach($forbidden in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($epochKernel -match [regex]::Escape($forbidden)){ throw "T27.3 inherited zero-wait violation: $forbidden" }
}

# T24.1 closed-generic Harmony patches stay banned.
$world=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/WorldRootAttribution093T22.cs') -Raw
if($world -notmatch 'genericHarmony=OFF'){ throw 'T24.1 generic-Harmony safety marker missing' }
if($world -match 'PatchTraitDefDatabaseSignals\(harmony\)'){ throw 'closed generic TraitDef Harmony patch returned' }

$csproj=Get-Content $proj -Raw
if(-not $csproj.Contains('<Compile Remove="AI\SingleCallCandidatePartition.cs" />')){ throw 'candidate partition is not excluded' }

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
$zip=Join-Path $build 'RimMT_V0.9.3_T27.3_Wait_Stall_Trace.zip'
Compress-Archive -Path $stage -DestinationPath $zip -Force
Write-Host "Built $zip"
