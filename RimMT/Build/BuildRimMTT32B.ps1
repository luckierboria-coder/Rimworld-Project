$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32A.ps1')
if(-not $?){ throw 'T32-A prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T32BLazyForbiddenFingerprint.ps1')
if(-not $?){ throw 'T32-B transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T32-B build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.11 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t32b-lazy-forbidden-fingerprint'){ throw 'T32-B version marker missing' }

$t20Path=Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw

$validatorStateStart=$t20.IndexOf('internal struct ValidatorCallState')
$reachStateStart=$t20.IndexOf('internal struct ReachCallState',$validatorStateStart)
if($validatorStateStart -lt 0 -or $reachStateStart -lt 0){ throw 'Cannot isolate ValidatorCallState/ReachCallState' }
$validatorState=$t20.Substring($validatorStateStart,$reachStateStart-$validatorStateStart)
$reachStateEnd=$t20.IndexOf('internal sealed class TransactionContext',$reachStateStart)
if($reachStateEnd -lt 0){ throw 'Cannot isolate ReachCallState end' }
$reachState=$t20.Substring($reachStateStart,$reachStateEnd-$reachStateStart)
foreach($requiredState in @('internal bool Prime;','PrimeForbiddenBefore','PrimeFingerprint')){
  if(-not $validatorState.Contains($requiredState)){ throw "T32-B validator state marker missing: $requiredState" }
  if($reachState.Contains($requiredState)){ throw "T32-B leaked prime state into ReachCallState: $requiredState" }
}
if(-not $validatorState.Contains('ForStore(')){ throw 'Generated ValidatorCallState ForStore helper missing' }

foreach($required in @(
  'CaptureCheap(__state.Thing)',
  'MatchesCheap(thing)',
  '!entry.Fingerprint.HasForbidden',
  'PrimeFingerprint',
  'PrimeForbiddenBefore',
  'TryReadForbidden(context.Pawn, thing',
  'MatchesForbidden(context.Pawn, thing)',
  'validatorLazyStores',
  'validatorLazyRepeatProbes',
  'validatorLazyPrimeSuccess',
  'validatorLazyPrimePositive',
  'validatorLazyPrimeUnstable',
  'storeForbiddenReadsAvoided',
  'validator caches false only',
  'ValidatorWarmupMatches = 12',
  'ValidatorVerifyMask = 63'
)){
  if(-not $t20.Contains($required)){ throw "T32-B marker missing: $required" }
}

foreach($forbidden in @(
  'ThingFingerprint.Capture(context.Pawn',
  'CaptureCheap(context.Pawn',
  'global forbidden cache',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For(',
  'ManualResetEvent',
  'StartJob(',
  'EndCurrentJob('
)){
  if($t20 -match [regex]::Escape($forbidden)){ throw "T32-B forbidden marker/path: $forbidden" }
}

# Store path must not evaluate IsForbidden.
$storeStart=$t20.IndexOf('public static void ValidatorPostfix')
$storeEnd=$t20.IndexOf('public static bool ReachPrefix',$storeStart)
if($storeStart -lt 0 -or $storeEnd -lt 0){ throw 'Cannot isolate ValidatorPostfix' }
$validatorPost=$t20.Substring($storeStart,$storeEnd-$storeStart)
if($validatorPost -match 'ThingFingerprint\.Capture\('){ throw 'Old eager fingerprint capture survived' }

$fpStart=$t20.IndexOf('internal struct ThingFingerprint')
$fpEnd=$t20.IndexOf('internal struct TargetFingerprint',$fpStart)
if($fpStart -lt 0 -or $fpEnd -lt 0){ throw 'Cannot isolate ThingFingerprint' }
$fp=$t20.Substring($fpStart,$fpEnd-$fpStart)
if($fp -notmatch 'CaptureCheap\(Thing thing\)'){ throw 'CaptureCheap missing' }
if($fp -notmatch 'MatchesForbidden\(Pawn pawn, Thing thing'){ throw 'MatchesForbidden missing' }

# All IsForbidden reads are centralized through TryReadForbidden; the store path must stay free of it.
$allT20ForbidCount=([regex]::Matches($t20,'IsForbidden\(pawn\)')).Count
if($allT20ForbidCount -ne 1){ throw "Unexpected T32-B direct IsForbidden read count: $allT20ForbidCount" }
if($validatorPost -match 'CaptureCheap\([^\)]*pawn'){ throw 'Store path unexpectedly depends on pawn/Forbidden' }

# T32-A remains intact.
$t32a=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
foreach($required in @(
  'False-only exact CanReserve memo',
  'WarmupMatches = 16',
  'VerifyMask = 63',
  'runtimeQuarantined = true'
)){
  if(-not $t32a.Contains($required)){ throw "T32-A invariant lost: $required" }
}

# T29/T30/T31 remain absent.
foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T32-B: $dead" }
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.11\.0"'){ throw 'Diagnostics v0.11 version missing' }

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T32-B inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t32b-main'
$diagRoot=Join-Path $build 'stage-t32b-diag'
$bundle=Join-Path $build 'stage-t32b-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T32B_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.11.zip'
$bundleZip=Join-Path $build 'RimMT_T32B_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
