$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32A.ps1')
if(-not $?){ throw 'T32-A prerequisite build failed' }

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T32BLazyValidatorFingerprint.ps1')
if(-not $?){ throw 'T32-B transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T32-B build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.11 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t32b-lazy-validator-fingerprint'){ throw 'T32-B version marker missing' }
if($boot -notmatch 'ReservationTransaction093T32A\.Apply\(harmony\)'){ throw 'T32-A reservation transaction lost' }
if($boot -match 'SharedEligibilityPrimitiveCensus093T31\.Apply|JobSearchRedundancyCensus093T30\.Apply'){
  throw 'T30/T31 profiler unexpectedly active in T32-B'
}

$corePath=Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$core=Get-Content $corePath -Raw
foreach($required in @(
  'validatorLazyStores',
  'validatorLazyPrimeAttempts',
  'validatorLazyPrimeSuccess',
  'validatorLazyPrimePositive',
  'validatorLazyPrimeForbiddenReads',
  'validatorLazyPrimeForbiddenFailures',
  'netStoreReadAvoided',
  'CaptureCheap(__state.Thing)',
  'ForbiddenKnown',
  'TryCapturePrimed',
  'CheapMatches',
  'ForbiddenMatches',
  'ValidatorCallState.LazyPrime',
  'if (__state.LazyPrime)',
  'Only Primed'
)){
  if(-not $core.Contains($required)){ throw "T32-B marker missing: $required" }
}

# Critical proof: first negative store must not call IsForbidden.
$storeNeedle='new ValidatorNegativeEntry(ThingFingerprint.CaptureCheap(__state.Thing))'
if(-not $core.Contains($storeNeedle)){ throw 'T32-B cheap store path missing' }
if($core -match 'new ValidatorNegativeEntry\(ThingFingerprint\.Capture\(context\.Pawn'){
  throw 'T32-B legacy eager forbidden capture survived'
}

# Lazy-prime branch must run live and must not inject a false result.
$lpStart=$core.IndexOf('if (!entry.Fingerprint.ForbiddenKnown)')
$lpEnd=$core.IndexOf('Interlocked.Increment(ref validatorMemoCandidates);',$lpStart)
if($lpStart -lt 0 -or $lpEnd -lt 0){ throw 'Cannot isolate T32-B lazy-prime prefix gate' }
$lp=$core.Substring($lpStart,$lpEnd-$lpStart)
if($lp -match '__result\s*='){ throw 'T32-B unprimed entry can alter validator result' }
if($lp -notmatch 'return true;'){ throw 'T32-B unprimed entry does not force live validator' }

# Only the existing post-prime validator path may authorize false replay.
if($core -notmatch '__result = false;'){ throw 'Inherited T21 false-only replay unexpectedly missing' }
if($core -match '__result = true;'){ throw 'T32-B must not add positive validator replay' }

# Forbidden reads are allowed only in priming and primed-match helpers, never CaptureCheap.
$cheapStart=$core.IndexOf('internal static ThingFingerprint CaptureCheap')
$cheapEnd=$core.IndexOf('internal static bool TryCapturePrimed',$cheapStart)
if($cheapStart -lt 0 -or $cheapEnd -lt 0){ throw 'Cannot isolate CaptureCheap' }
$cheap=$core.Substring($cheapStart,$cheapEnd-$cheapStart)
if($cheap -match 'IsForbidden'){ throw 'CaptureCheap still reads IsForbidden' }

# T32-A safety remains intact.
$t32a=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
foreach($required in @(
  'False-only exact CanReserve memo',
  'runtimeQuarantined = true',
  'ReservationMutationPrefix',
  'foreignTranspilers == 0',
  'foreignFinalizers == 0'
)){
  if(-not $t32a.Contains($required)){ throw "T32-A safety marker missing after T32-B: $required" }
}

# No named WorkGiver/mod special case was added to T32-B transform.
$t32bTransform=Get-Content (Join-Path $root 'RimMT/Build/ApplyRimMTV093T32BLazyValidatorFingerprint.ps1') -Raw
foreach($forbidden in @(
  'Hospitality',
  'PickUpAndHaul',
  'HaulToInventory',
  'Warden_DeliverFood',
  'BrothelColony',
  'DubsBadHygiene',
  'MedievalOverhaul'
)){
  if($t32bTransform -match [regex]::Escape($forbidden)){ throw "T32-B named special case detected: $forbidden" }
}

# No new worker/wait/gameplay mutation.
foreach($forbidden in @(
  'StartJob(',
  'EndCurrentJob(',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For(',
  'ManualResetEvent'
)){
  if($t32bTransform -match [regex]::Escape($forbidden)){ throw "T32-B forbidden worker/gameplay primitive: $forbidden" }
}

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'V0\.9\.3-T32B Lazy Validator Fingerprint'){ throw 'T32-B production report label missing' }
if($report -notmatch 'ReservationTransaction093T32A\.Summary\(\)'){ throw 'T32-A summary missing from T32-B report' }
if($report -notmatch 'JobSearchTransaction093T20\.Summary\(\)'){ throw 'T21/T32-B summary missing from report' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.11\.0"'){ throw 'Diagnostics v0.11 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -match 'SharedEligibilityPrimitiveCensus093T31|JobSearchRedundancyCensus093T30'){
  throw 'T30/T31 profiler leaked into Diagnostics v0.11'
}

# Preserve T22 authority and engine zero-wait.
$t22=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs') -Raw
if($t22 -notmatch 'original live validator is called exactly once'){ throw 'T22 live-validator authority marker missing' }

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate inherited zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T32-B inherited zero-wait violation: $x" }
}

# T29/T30/T31 experiments remain absent.
foreach($dead in @(
  'RimMT/Source/RimMT/AI/SourceMetadata093T29.cs',
  'RimMT/Source/RimMT/AI/JobSearchRedundancyCensus093T30.cs',
  'RimMT/Source/RimMT/AI/SharedEligibilityPrimitiveCensus093T31.cs'
)){
  if(Test-Path (Join-Path $root $dead)){ throw "Retired experiment unexpectedly present in T32-B: $dead" }
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
