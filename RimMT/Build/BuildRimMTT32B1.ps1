$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32B.ps1')
if(-not $?){ throw 'T32-B prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T32B1AdaptiveForbiddenFingerprint.ps1')
if(-not $?){ throw 'T32-B.1 transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T32-B.1 build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.12 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t32b1-adaptive-forbidden-fingerprint'){ throw 'T32-B.1 version marker missing' }

$t20Path=Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw

foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'AdaptiveEvidenceMaxStores = 4096',
  'internal bool PreferEager;',
  'internal int AdaptiveStores;',
  'internal int AdaptiveFirstRepeats;',
  'ObserveStore()',
  'ObserveFirstRepeat()',
  'EvaluateAdaptiveMode()',
  'ValidatedMatches < ValidatorWarmupMatches',
  'AdaptiveFirstRepeats * 8L >= AdaptiveStores',
  'AdaptiveFirstRepeats * 16L < AdaptiveStores',
  'validatorAdaptiveSwitchToEager',
  'validatorAdaptiveSwitchToLazy',
  'validatorAdaptiveEagerStores',
  'validatorAdaptiveEagerCaptures',
  'validatorAdaptiveEagerFallbackLazy',
  'RepeatObserved',
  'modesLazy/Eager=',
  'enter>=1/8@128+trusted, return<1/16@256',
  'validator caches false only',
  'ValidatorWarmupMatches = 12',
  'ValidatorVerifyMask = 63'
)){
  if(-not $t20.Contains($required)){ throw "T32-B.1 marker missing: $required" }
}

# Generic architecture only: no named WorkGiver/mod specialization.
foreach($forbidden in @(
  'Warden_DeliverFood',
  'HaulToInventory',
  'IrrigationBasic',
  'MedievalOverhaul',
  'PickUpAndHaul',
  'DubsBadHygiene',
  'Hospitality',
  'ProcessorFramework',
  'TakeEntityToHoldingPlatform',
  'FeedHemogen',
  'HaulCorpses',
  '__result = true',
  'StartJob(',
  'EndCurrentJob(',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For(',
  'ManualResetEvent'
)){
  if($t20 -match [regex]::Escape($forbidden)){ throw "T32-B.1 forbidden specialization/mutation/worker primitive: $forbidden" }
}

# Store path may read Forbidden only when generic trust state has selected Eager.
$postStart=$t20.IndexOf('public static void ValidatorPostfix')
$postEnd=$t20.IndexOf('public static bool ReachPrefix',$postStart)
if($postStart -lt 0 -or $postEnd -lt 0){ throw 'Cannot isolate ValidatorPostfix' }
$post=$t20.Substring($postStart,$postEnd-$postStart)
if(-not $post.Contains('if (trust.PreferEager)')){ throw 'Adaptive eager store gate missing' }
if(-not $post.Contains('ThingFingerprint.CaptureCheap(__state.Thing)')){ throw 'Cheap store capture missing' }
if(-not $post.Contains('TryReadForbidden(context.Pawn, __state.Thing')){ throw 'Eager Forbidden capture missing' }

# Lazy first revisit remains live; unprimed entries must never directly authoritative-replay.
$prefixStart=$t20.IndexOf('public static bool ValidatorPrefix')
$prefixEnd=$t20.IndexOf('public static void ValidatorPostfix',$prefixStart)
$prefix=$t20.Substring($prefixStart,$prefixEnd-$prefixStart)
$unprimed=$prefix.IndexOf('if (!entry.Primed)')
$authoritative=$prefix.IndexOf('__result = false')
if($unprimed -lt 0 -or $authoritative -lt 0 -or $unprimed -gt $authoritative){
  throw 'Unprimed live-prime boundary lost'
}
if(-not $prefix.Contains('trust.ObserveFirstRepeat()')){ throw 'First-repeat observation missing' }

# Existing T32-B before/after Forbidden prime stability remains intact.
foreach($required in @(
  'PrimeForbiddenBefore',
  'PrimeFingerprint',
  'TryReadForbidden(context.Pawn, thing, out forbiddenBefore)',
  'TryReadForbidden(context.Pawn, thing, out forbiddenAfter)',
  'forbiddenAfter != __state.PrimeForbiddenBefore',
  'validatorLazyPrimeUnstable'
)){
  if(-not $t20.Contains($required)){ throw "T32-B safety invariant lost: $required" }
}

# Existing T32-A remains intact.
$t32a=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
foreach($required in @(
  'False-only exact CanReserve memo',
  'WarmupMatches = 16',
  'VerifyMask = 63',
  'runtimeQuarantined = true',
  'mutationMethodsMissing == 0'
)){
  if(-not $t32a.Contains($required)){ throw "T32-A invariant lost: $required" }
}

# No retired experiment leakage.
foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T32-B.1: $dead" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.12\.0"'){ throw 'Diagnostics v0.12 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -match 'SharedEligibilityPrimitiveCensus093T31|JobSearchRedundancyCensus093T30'){
  throw 'T30/T31 profiler leaked into Diagnostics v0.12'
}

# Preserve zero-wait.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T32-B.1 inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t32b1-main'
$diagRoot=Join-Path $build 'stage-t32b1-diag'
$bundle=Join-Path $build 'stage-t32b1-bundle'
foreach($p in @($mainRoot,$diagRoot,$bundle)){
  if(Test-Path $p){ Remove-Item $p -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $p | Out-Null
}

$mainStage=Join-Path $mainRoot 'RimMT'
$diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse
Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage

$mainZip=Join-Path $build 'RimMT_V0.9.3_T32B1_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.12.zip'
$bundleZip=Join-Path $build 'RimMT_T32B1_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
