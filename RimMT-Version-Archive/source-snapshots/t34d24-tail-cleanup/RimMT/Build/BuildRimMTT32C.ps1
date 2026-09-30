$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32B1.ps1')
if(-not $?){ throw 'T32-B.1 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T32CPositiveCanReserveShadow.ps1')
if(-not $?){ throw 'T32-C transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T32-C build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.13 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t32c-positive-canreserve-shadow'){ throw 'T32-C version marker missing' }

$t32Path=Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs'
$t32=Get-Content $t32Path -Raw

foreach($required in @(
  'PositiveShadowCapacity = 8192',
  'PositiveShadows',
  'PositiveEntry',
  'ForPositiveShadow',
  'internal bool PositiveShadow;',
  'positiveShadowStores',
  'positiveShadowCandidates',
  'positiveShadowMatches',
  'positiveShadowMismatches',
  'positiveShadowAuthorityEligible',
  'positiveShadowAuthorityUnsafe',
  'positiveShadowFingerprintBypass',
  'positiveShadowCapacityBypass',
  'positiveShadowMutationClears',
  'positiveShadowEntriesCleared',
  'T32-C positive shadow always executes live',
  'no positive replay',
  'WarmupMatches = 16',
  'VerifyMask = 63',
  'runtimeQuarantined = true',
  'mutationMethodsMissing == 0'
)){
  if(-not $t32.Contains($required)){ throw "T32-C marker/invariant missing: $required" }
}

# Positive shadow MUST remain live. It may never assign true or skip CanReserve.
if($t32 -match '__result\s*=\s*true'){ throw 'T32-C illegal positive replay assignment found' }

$prefixStart=$t32.IndexOf('public static bool CanReservePrefix')
$prefixEnd=$t32.IndexOf('public static Exception CanReserveFinalizer',$prefixStart)
if($prefixStart -lt 0 -or $prefixEnd -lt 0){ throw 'Cannot isolate T32-C CanReservePrefix' }
$prefix=$t32.Substring($prefixStart,$prefixEnd-$prefixStart)

$shadowLookup=$prefix.IndexOf('context.PositiveShadows.TryGetValue')
$shadowState=$prefix.IndexOf('CallState.ForPositiveShadow',$shadowLookup)
$shadowReturn=$prefix.IndexOf('return true;',$shadowState)
$negativeReplay=$prefix.IndexOf('__result = false;')
if($shadowLookup -lt 0 -or $shadowState -lt 0 -or $shadowReturn -lt 0){
  throw 'T32-C positive shadow live path missing'
}
if($negativeReplay -lt 0){ throw 'Inherited T32-A false replay missing' }
if($prefix.Substring($shadowState,$shadowReturn-$shadowState) -match '__result\s*='){
  throw 'T32-C positive shadow mutates result before live call'
}

# The finalizer compares live result. true => measurement return; false => fall through to Store path.
$finStart=$t32.IndexOf('public static Exception CanReserveFinalizer')
$finEnd=$t32.IndexOf('public static void ReservationMutationPrefix',$finStart)
if($finStart -lt 0 -or $finEnd -lt 0){ throw 'Cannot isolate T32-C finalizer' }
$fin=$t32.Substring($finStart,$finEnd-$finStart)
foreach($required in @(
  'if (__state.PositiveShadow)',
  'if (__result)',
  'positiveShadowMatches',
  'positiveShadowMismatches',
  'context.PositiveShadows.Remove(__state.Key)',
  'if (!__state.Store)',
  'new NegativeEntry(',
  'new PositiveEntry('
)){
  if(-not $fin.Contains($required)){ throw "T32-C finalizer invariant missing: $required" }
}
if($fin -match '__result\s*=\s*true'){ throw 'T32-C finalizer illegally creates positive result' }

# Positive shadow state intentionally sets Store=true so a live positive->false flip flows into
# the inherited T32-A negative store path in the same call.
$stateStart=$t32.IndexOf('internal struct CallState')
$ctxStart=$t32.IndexOf('internal sealed class PackageContext',$stateStart)
$state=$t32.Substring($stateStart,$ctxStart-$stateStart)
$factoryStart=$state.IndexOf('ForPositiveShadow')
$factoryEnd=$state.IndexOf('ForAuthoritative',$factoryStart)
if($factoryStart -lt 0 -or $factoryEnd -lt 0){ throw 'Cannot isolate ForPositiveShadow' }
$factory=$state.Substring($factoryStart,$factoryEnd-$factoryStart)
if(-not $factory.Contains('Store = true') -or -not $factory.Contains('PositiveShadow = true')){
  throw 'Positive shadow false-transition preservation lost'
}

# Reservation mutation invalidates both result classes.
$mutStart=$t32.IndexOf('public static void ReservationMutationPrefix')
$mutEnd=$t32.IndexOf('private static void PatchMutationMethods',$mutStart)
$mut=$t32.Substring($mutStart,$mutEnd-$mutStart)
if(-not $mut.Contains('context.Negatives.Clear()') -or -not $mut.Contains('context.PositiveShadows.Clear()')){
  throw 'T32-C mutation invalidation incomplete'
}

# Existing T32-B.1 adaptive Forbidden mechanism remains unchanged.
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'validatorAdaptiveSwitchToEager',
  'validatorAdaptiveSwitchToLazy',
  'enter>=1/8@128+trusted, return<1/16@256',
  'validator caches false only'
)){
  if(-not $t20.Contains($required)){ throw "T32-B.1 invariant lost in T32-C: $required" }
}
foreach($forbidden in @(
  'Warden_DeliverFood','HaulToInventory','IrrigationBasic','MedievalOverhaul',
  'PickUpAndHaul','DubsBadHygiene','Hospitality','ProcessorFramework',
  'TakeEntityToHoldingPlatform','FeedHemogen','HaulCorpses'
)){
  if($t32 -match [regex]::Escape($forbidden) -or $t20 -match [regex]::Escape($forbidden)){
    throw "T32-C named WorkGiver/mod specialization found: $forbidden"
  }
}

# No gameplay/worker authority expansion.
foreach($forbidden in @(
  'StartJob(','EndCurrentJob(','Task.Run(','ThreadPool.QueueUserWorkItem',
  'SpinWait','Thread.Sleep(','Parallel.For(','ManualResetEvent'
)){
  if($t32 -match [regex]::Escape($forbidden)){ throw "T32-C forbidden mutation/worker primitive: $forbidden" }
}

# Retired experimental profilers must stay absent.
foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T32-C: $dead" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.13\.0"'){ throw 'Diagnostics v0.13 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('"RimMT.ReservationTransaction093T32A"')){ throw 'Diagnostics T32-C reflection bridge lost' }
if($diagReport -match 'SharedEligibilityPrimitiveCensus093T31|JobSearchRedundancyCensus093T30'){
  throw 'T30/T31 profiler leaked into Diagnostics v0.13'
}

# Preserve zero-wait production policy.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T32-C inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t32c-main'
$diagRoot=Join-Path $build 'stage-t32c-diag'
$bundle=Join-Path $build 'stage-t32c-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T32C_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.13.zip'
$bundleZip=Join-Path $build 'RimMT_T32C_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
