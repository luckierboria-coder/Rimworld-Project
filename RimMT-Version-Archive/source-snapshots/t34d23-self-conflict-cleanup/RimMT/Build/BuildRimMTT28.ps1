$ErrorActionPreference='Stop'

# Rebuild the verified T27.8 baseline, then apply the T28 bottom-layer delta.
& (Join-Path $PSScriptRoot 'BuildRimMTT27_8.ps1')
if(-not $?){ throw 'T27.8 prerequisite build failed' }

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T28UnifiedJobSearchTransaction.ps1')
if(-not $?){ throw 'T28 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T28 build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.6 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t28-unified-job-search-transaction'){ throw 'T28 version marker missing' }
if($boot -notmatch 'JobSearchPackageContext093T28\.Apply\(harmony\)'){ throw 'T28 shared package boundary not installed' }
if($boot -match 'AdaptiveGenClosestAssist\.Apply\(harmony\)'){ throw 'dead persistent-fabric GenClosest consumer is still installed' }
if($boot -match 'AggressiveReachabilityProfilesV17\.Apply\(harmony\)'){ throw 'retired ReachProfile returned' }

$sharedPath=Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs'
$shared=Get-Content $sharedPath -Raw
foreach($required in @(
  'TryIssueJobPackage',
  'JobSearchTransaction093T20.PackagePrefix',
  'GenClosestTransactionIndex093T22.PackagePrefix',
  'JobGiverGlobalNearest04181.JobGiverPrefix',
  'IsBillStackKnownInactive',
  'MarkBillStackInactive',
  'GetOrCreateModuleState',
  'TryGetNegative',
  'StoreNegative',
  'One synchronous TryIssueJobPackage boundary only'
)){
  if(-not $shared.Contains($required)){ throw "T28 shared context marker missing: $required" }
}
foreach($forbidden in @('StartJob(','EndCurrentJob(','ReservationManager.','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(','ManualResetEvent')){
  if($shared -match [regex]::Escape($forbidden)){ throw "T28 shared context forbidden gameplay/wait primitive: $forbidden" }
}

$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
if($t20 -notmatch 'packagePatched = JobSearchPackageContext093T28\.Installed;'){ throw 'T20 did not transfer package lifecycle ownership to T28' }
if($t20 -match 'harmony\.Patch\(package,'){ throw 'T20 still installs a package Harmony wrapper' }

$t22=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs') -Raw
if($t22 -notmatch 'packagePatched = JobSearchPackageContext093T28\.Installed;'){ throw 'T22 did not transfer package lifecycle ownership to T28' }
if($t22 -match 'harmony\.Patch\(package,'){ throw 'T22 still installs a package Harmony wrapper' }

$global=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs') -Raw
if($global -notmatch 'if \(!JobSearchPackageContext093T28\.Installed\) return;'){ throw 'GlobalNearest did not bind to T28 lifecycle' }
if($global -match 'harmony\.Patch\(jobGiver,'){ throw 'GlobalNearest still installs a package Harmony wrapper' }

$bill=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs') -Raw
foreach($required in @('JobSearchPackageContext093T28.InScope','IsBillStackKnownInactive','MarkBillStackInactive','readinessFalseMemoHits','readinessAvoidRate')){
  if(-not $bill.Contains($required)){ throw "T28 DoBill marker missing: $required" }
}
if($bill -match 'inactiveStacksInPackage|readinessScopeStamp'){ throw 'legacy DoBill private package memo survived T28' }

$report=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs') -Raw
if($report -notmatch 'JobSearchPackageContext093T28\.Summary\(\)'){ throw 'T28 summary missing from production report' }
if($report -match 'V0\.9\.3-T27\.4 Diagnostics Split'){ throw 'stale T27.4 production report label survived' }
if($report -notmatch 'Persistent-fabric GenClosest consumer: RETIRED/OFF in T28'){ throw 'dead fabric consumer retirement missing from report' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.6\.0"'){ throw 'Diagnostics v0.6 version missing' }
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if($diagReport -notmatch 'RimMT\.JobSearchPackageContext093T28'){ throw 'Diagnostics v0.6 does not surface T28 context' }

# Inherited hard safety invariants.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){
  if($kernel -match [regex]::Escape($x)){ throw "T28 zero-wait violation: $x" }
}

$diagSources=(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics') -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach($x in @('StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(')){
  if($diagSources -match [regex]::Escape($x)){ throw "Diagnostics unexpectedly mutates/schedules gameplay: $x" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t28-main'
$diagRoot=Join-Path $build 'stage-t28-diag'
$bundle=Join-Path $build 'stage-t28-bundle'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T28_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.6.zip'
$bundleZip=Join-Path $build 'RimMT_T28_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
