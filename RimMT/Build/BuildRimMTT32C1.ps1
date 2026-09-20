$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32C.ps1')
if(-not $?){ throw 'T32-C prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T32C1PositiveCanReserveReplay.ps1')
if(-not $?){ throw 'T32-C.1 transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T32-C.1 build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.14 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t32c1-positive-canreserve-replay'){ throw 'T32-C.1 version marker missing' }

$t32Path=Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs'
$t32=Get-Content $t32Path -Raw

foreach($required in @(
  'PositiveWarmupMatches = 32',
  'PositiveVerifyMask = 63',
  'positiveRuntimeQuarantined',
  'positiveVerifyRuns',
  'positiveVerifyMatches',
  'positiveVerifyMismatches',
  'positiveAuthoritativeHits',
  'positiveQuarantines',
  'positiveAuthorityBypass',
  'PositiveTrustSample',
  'PositiveAuthoritativeHit',
  'PositiveHitSerial',
  'PositiveValidatedMatches',
  'ForPositiveAuthoritative',
  'positiveReplay[runtimeQuarantined=',
  'warmup=" + PositiveWarmupMatches',
  'verifyEvery=" + (PositiveVerifyMask + 1)',
  '32 live matches',
  '1/64 live parity',
  'T32-A false replay and T32-C.1 positive replay use independent trust',
  'WarmupMatches = 16',
  'VerifyMask = 63',
  'runtimeQuarantined = true',
  'mutationMethodsMissing == 0'
)){
  if(-not $t32.Contains($required)){ throw "T32-C.1 marker/invariant missing: $required" }
}

# Exactly one positive result assignment is permitted in this module.
$trueAssignments=[regex]::Matches($t32,'__result\s*=\s*true\s*;')
if($trueAssignments.Count -ne 1){
  throw "T32-C.1 expected exactly one __result=true assignment, found $($trueAssignments.Count)"
}

$prefixStart=$t32.IndexOf('public static bool CanReservePrefix')
$prefixEnd=$t32.IndexOf('public static Exception CanReserveFinalizer',$prefixStart)
if($prefixStart -lt 0 -or $prefixEnd -lt 0){ throw 'Cannot isolate T32-C.1 CanReservePrefix' }
$prefix=$t32.Substring($prefixStart,$prefixEnd-$prefixStart)

foreach($required in @(
  'context.PositiveShadows.TryGetValue',
  'positiveEntry.MutationEpoch != context.MutationEpoch',
  '!positiveEntry.Fingerprint.Matches(target)',
  'chainAuthoritativeSafe',
  '!runtimeQuarantined',
  '!positiveRuntimeQuarantined',
  'context.PositiveValidatedMatches < PositiveWarmupMatches',
  '(context.PositiveHitSerial & PositiveVerifyMask) == 0',
  'CallState.ForPositiveShadow(',
  'CallState.ForPositiveAuthoritative(context)',
  '__result = true;',
  'return false;',
  '__result = false;'
)){
  if(-not $prefix.Contains($required)){ throw "T32-C.1 prefix invariant missing: $required" }
}

$lookup=$prefix.IndexOf('context.PositiveShadows.TryGetValue')
$safety=$prefix.IndexOf('bool positiveAuthoritySafe',$lookup)
$verify=$prefix.IndexOf('bool positiveVerify',$safety)
$trueSet=$prefix.IndexOf('__result = true;',$verify)
$positiveReturn=$prefix.IndexOf('return false;',$trueSet)
if($lookup -lt 0 -or $safety -lt 0 -or $verify -lt 0 -or $trueSet -lt 0 -or $positiveReturn -lt 0){
  throw 'T32-C.1 positive authority ordering lost'
}
if(-not ($lookup -lt $safety -and $safety -lt $verify -and $verify -lt $trueSet -and $trueSet -lt $positiveReturn)){
  throw 'T32-C.1 positive authority ordering invalid'
}

# Verify branch must stay live and must precede the positive replay assignment.
$verifyBranch=$prefix.Substring($verify,$trueSet-$verify)
if(-not $verifyBranch.Contains('Interlocked.Increment(ref positiveVerifyRuns)') -or
   -not $verifyBranch.Contains('return true;')){
  throw 'T32-C.1 positive live parity branch missing'
}
if($verifyBranch -match '__result\s*=\s*true'){ throw 'Positive verify branch illegally replays true' }

# Finalizer: positive live mismatch quarantines positive authority only, then falls through
# to inherited negative-store handling.
$finStart=$t32.IndexOf('public static Exception CanReserveFinalizer')
$finEnd=$t32.IndexOf('public static void ReservationMutationPrefix',$finStart)
if($finStart -lt 0 -or $finEnd -lt 0){ throw 'Cannot isolate T32-C.1 finalizer' }
$fin=$t32.Substring($finStart,$finEnd-$finStart)
$posStart=$fin.IndexOf('if (__state.PositiveShadow)')
$negVerify=$fin.IndexOf('if (__state.Verify)',$posStart)
if($posStart -lt 0 -or $negVerify -lt 0){ throw 'Cannot isolate positive finalizer block' }
$posBlock=$fin.Substring($posStart,$negVerify-$posStart)

foreach($required in @(
  'positiveShadowMatches',
  'context.PositiveValidatedMatches++',
  'positiveVerifyMatches',
  'context.PositiveShadows.Remove(__state.Key)',
  'positiveShadowMismatches',
  'if (__state.PositiveTrustSample)',
  'context.PositiveShadows.Clear()',
  'context.PositiveValidatedMatches = 0',
  'context.PositiveHitSerial = 0',
  'positiveRuntimeQuarantined = true',
  'positiveVerifyMismatches',
  'positiveQuarantines'
)){
  if(-not $posBlock.Contains($required)){ throw "T32-C.1 positive finalizer invariant missing: $required" }
}
if($posBlock.Contains('runtimeQuarantined = true')){
  throw 'Positive mismatch must not quarantine T32-A negative authority'
}
if($fin -notmatch 'if \(!__state\.Store\)[\s\S]*new NegativeEntry\('){
  throw 'Positive false transition no longer reaches inherited negative-store path'
}

# Authoritative positive state must be result-only: never Store/Verify/TrustSample.
$stateStart=$t32.IndexOf('internal struct CallState')
$ctxStart=$t32.IndexOf('internal sealed class PackageContext',$stateStart)
$state=$t32.Substring($stateStart,$ctxStart-$stateStart)
$pa=$state.IndexOf('ForPositiveAuthoritative')
$na=$state.IndexOf('ForAuthoritative',$pa)
if($pa -lt 0 -or $na -lt 0){ throw 'Cannot isolate positive authoritative state factory' }
$paFactory=$state.Substring($pa,$na-$pa)
if(-not $paFactory.Contains('PositiveAuthoritativeHit = true')){
  throw 'Positive authoritative marker missing'
}
foreach($forbidden in @('Store = true','Verify = true','PositiveTrustSample = true')){
  if($paFactory.Contains($forbidden)){ throw "Positive authoritative state illegally contains: $forbidden" }
}

# Reservation mutation must clear both result classes and reset both package trust tracks.
$mutStart=$t32.IndexOf('public static void ReservationMutationPrefix')
$mutEnd=$t32.IndexOf('private static void PatchMutationMethods',$mutStart)
$mut=$t32.Substring($mutStart,$mutEnd-$mutStart)
foreach($required in @(
  'context.Negatives.Clear()',
  'context.PositiveShadows.Clear()',
  'context.ValidatedMatches = 0',
  'context.PositiveValidatedMatches = 0',
  'context.PositiveHitSerial = 0'
)){
  if(-not $mut.Contains($required)){ throw "T32-C.1 mutation invalidation missing: $required" }
}

# Late authority loss must remove positive replay eligibility immediately.
$reauditStart=$t32.IndexOf('private static void ReauditChain()')
$auditStart=$t32.IndexOf('private static void AuditChain',$reauditStart)
$reaudit=$t32.Substring($reauditStart,$auditStart-$reauditStart)
foreach($required in @(
  'context.Negatives.Clear()',
  'context.PositiveShadows.Clear()',
  'context.PositiveValidatedMatches = 0',
  'context.PositiveHitSerial = 0'
)){
  if(-not $reaudit.Contains($required)){ throw "T32-C.1 late authority invalidation missing: $required" }
}

# T32-B.1 remains intact and generic.
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'validatorAdaptiveSwitchToEager',
  'validatorAdaptiveSwitchToLazy',
  'enter>=1/8@128+trusted, return<1/16@256',
  'validator caches false only'
)){
  if(-not $t20.Contains($required)){ throw "T32-B.1 invariant lost in T32-C.1: $required" }
}

# Generic architecture only. No named WorkGiver/mod special cases.
foreach($forbidden in @(
  'Warden_DeliverFood','HaulToInventory','IrrigationBasic','MedievalOverhaul',
  'PickUpAndHaul','DubsBadHygiene','Hospitality','ProcessorFramework',
  'TakeEntityToHoldingPlatform','FeedHemogen','HaulCorpses'
)){
  if($t32 -match [regex]::Escape($forbidden) -or $t20 -match [regex]::Escape($forbidden)){
    throw "T32-C.1 named WorkGiver/mod specialization found: $forbidden"
  }
}

# No gameplay mutation or worker/wait authority expansion.
foreach($forbidden in @(
  'StartJob(','EndCurrentJob(','Task.Run(','ThreadPool.QueueUserWorkItem',
  'SpinWait','Thread.Sleep(','Parallel.For(','ManualResetEvent'
)){
  if($t32 -match [regex]::Escape($forbidden)){ throw "T32-C.1 forbidden mutation/worker primitive: $forbidden" }
}

foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T32-C.1: $dead" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.14\.0"'){ throw 'Diagnostics v0.14 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('"RimMT.ReservationTransaction093T32A"')){ throw 'Diagnostics T32-C.1 reflection bridge lost' }
if($diagReport -match 'SharedEligibilityPrimitiveCensus093T31|JobSearchRedundancyCensus093T30'){
  throw 'T30/T31 profiler leaked into Diagnostics v0.14'
}

# Preserve inherited zero-wait.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T32-C.1 inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t32c1-main'
$diagRoot=Join-Path $build 'stage-t32c1-diag'
$bundle=Join-Path $build 'stage-t32c1-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T32C1_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.14.zip'
$bundleZip=Join-Path $build 'RimMT_T32C1_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
