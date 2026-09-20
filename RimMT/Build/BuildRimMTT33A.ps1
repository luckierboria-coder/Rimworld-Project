$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32C1.ps1')
if(-not $?){ throw 'T32-C.1 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T33ACandidateAdmissionShadow.ps1')
if(-not $?){ throw 'T33-A transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T33-A build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.15 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t33a-candidate-admission-shadow'){ throw 'T33-A version marker missing' }
if(-not $boot.Contains('CandidateAdmissionFabric093T33A.Apply(harmony);')){ throw 'T33-A bootstrap install missing' }

$t33Path=Join-Path $root 'RimMT/Source/RimMT/AI/CandidateAdmissionFabric093T33A.cs'
if(-not (Test-Path $t33Path)){ throw 'T33-A source missing' }
$t33=Get-Content $t33Path -Raw

foreach($required in @(
  'private const int Capacity = 65536',
  'PersistentKey',
  'PawnFingerprint',
  'ThingFingerprint',
  'lazyPrimeAttempts',
  'lazyPrimeSuccess',
  'replayableCandidates',
  'shadowMatches',
  'shadowMismatches',
  'packageGap[1/2-4/5-16/17-64/65+]',
  'tickAge[<=60/61-250/251-1000/>1000/unknown]',
  'Measurement-only',
  'JobSearchPackageContext093T28.CurrentGeneration',
  'entry.LastGeneration == generation',
  'TryReadForbidden',
  'false=>false',
  'false=>true'
)){
  if(-not $t33.Contains($required)){ throw "T33-A invariant/marker missing: $required" }
}

# T33-A may never alter the validator result or skip original.
if($t33 -match '__result'){ throw 'T33-A illegally references __result' }
if($t33 -match 'public static bool ValidatorPrefix'){ throw 'T33-A ValidatorPrefix must be void measurement-only' }
if($t33 -match 'return\s+false\s*;'){ 
  # Helpers such as Equals may return false; make sure the prefix body itself has no bool return.
  $p0=$t33.IndexOf('public static void ValidatorPrefix')
  $p1=$t33.IndexOf('public static void ValidatorPostfix',$p0)
  if($p0 -lt 0 -or $p1 -lt 0){ throw 'Cannot isolate T33-A ValidatorPrefix' }
  $prefix=$t33.Substring($p0,$p1-$p0)
  if($prefix -match 'return\s+false\s*;'){ throw 'T33-A prefix attempts skip-original' }
}

# No authoritative gameplay operations in the new fabric.
foreach($forbidden in @(
  'JobMaker','StartJob(','EndCurrentJob(','ReservationManager.Reserve',
  '.Reserve(','ReleaseClaimedBy','ReleaseAllClaimedBy',
  'CanReach(','ClosestThing','GetPriority(','JobOnThing(',
  'Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(',
  'Parallel.For(','ManualResetEvent'
)){
  if($t33 -match [regex]::Escape($forbidden)){ throw "T33-A forbidden authority/worker primitive: $forbidden" }
}

# Generic only: scanner names may be emitted at runtime telemetry, but source contains no named
# WorkGiver/mod specialization.
foreach($forbidden in @(
  'HaulMerge','TakeEntityToHoldingPlatform','HaulToCarrier','HaulMechsToCharger',
  'TendOther','DoBill','PickUpAndHaul','MedievalOverhaul','Hospitality',
  'ProcessorFramework','DubsBadHygiene','Warden_DeliverFood'
)){
  if($t33 -match [regex]::Escape($forbidden)){ throw "T33-A named specialization found: $forbidden" }
}

# T32-C.1 positive replay must remain exactly as verified.
$t32=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
foreach($required in @(
  'PositiveWarmupMatches = 32',
  'PositiveVerifyMask = 63',
  'positiveRuntimeQuarantined',
  'positiveAuthoritativeHits',
  'positiveVerifyMismatches',
  'T32-A false replay and T32-C.1 positive replay use independent trust'
)){
  if(-not $t32.Contains($required)){ throw "T32-C.1 invariant lost in T33-A: $required" }
}
$trueAssignments=[regex]::Matches($t32,'__result\s*=\s*true\s*;')
if($trueAssignments.Count -ne 1){
  throw "T32-C.1 positive authority shape changed: expected one __result=true, found $($trueAssignments.Count)"
}

# T21/T32-B.1 remains package-local and unchanged. T33-A is a separate shadow layer.
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'ValidatorCapacity = 8192',
  'ValidatorWarmupMatches = 12',
  'ValidatorVerifyMask = 63',
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'validator caches false only',
  'no Job/JobOnThing/reservation/priority/cross-package result is cached'
)){
  if(-not $t20.Contains($required)){ throw "T21/T32-B.1 invariant lost in T33-A: $required" }
}

# The new layer must run after T21's prefix so same-package authority remains owned by T21.
if(-not $t33.Contains('{ priority = Priority.First + 200 }')){
  throw 'T33-A validator prefix priority changed; expected to run after T21 First+250'
}
if(-not $t20.Contains('{ priority = Priority.First + 250 }')){
  throw 'T21 validator prefix priority unexpectedly changed'
}

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.15\.0"'){ throw 'Diagnostics v0.15 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('"RimMT.CandidateAdmissionFabric093T33A"')){ throw 'Diagnostics T33-A reflection bridge missing' }

# Retired experiments remain absent.
foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T33-A: $dead" }
}

# Preserve zero-wait production kernel.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T33-A inherited zero-wait violation: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t33a-main'
$diagRoot=Join-Path $build 'stage-t33a-diag'
$bundle=Join-Path $build 'stage-t33a-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T33A_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.15.zip'
$bundleZip=Join-Path $build 'RimMT_T33A_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
