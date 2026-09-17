$ErrorActionPreference='Stop'

# Rebuild verified T27.7, then apply the bottom-layer T27.8 delta.
& (Join-Path $PSScriptRoot 'BuildRimMTT27_7.ps1')
if(-not $?){ throw 'T27.7 prerequisite build failed' }
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T27_8JobSearchFoundation.ps1')
if(-not $?){ throw 'T27.8 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'RimMT T27.8 build failed' }
dotnet build $diagProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.5 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27\.8-job-search-foundation'){ throw 'T27.8 version marker missing' }
if($boot -match 'AggressiveReachabilityProfilesV17\.Apply\(harmony\)'){ throw 'retired ReachProfile returned' }

$compat=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Compatibility/CompatibilityGuard.cs') -Raw
foreach($required in @('allen.rimmt.diagnostics','RimMT.Diagnostics.','prefix','postfix')){ if(-not $compat.Contains($required)){ throw "Diagnostics neutrality marker missing: $required" } }
if($compat -notmatch 'StartsWith\("RimMT\.Diagnostics\."'){ throw 'diagnostics owner whitelist is not namespace constrained' }

$bill=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs') -Raw
foreach($required in @('PackageReadinessShouldDoNow','inactiveStacksInPackage','readinessFalseMemoHits','readinessFalseMemoStores','readinessAvoidRate','CurrentScopeStartTicks')){ if(-not $bill.Contains($required)){ throw "T27.8 readiness marker missing: $required" } }
if($bill -match 'Dictionary<BillStack, bool>'){ throw 'readiness memo must remain false-only set, not a true/false result cache' }

$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.5\.0"'){ throw 'Diagnostics v0.5 version missing' }
if($diag -notmatch 'if \(RimMTDiagnosticsSettings\.EnableSearchTiming\)'){ throw 'search timing patches are still unconditional' }

# Hard invariants.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){ if($kernel -match [regex]::Escape($x)){ throw "T27.8 zero-wait violation: $x" } }

$diagSources=(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics') -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach($x in @('StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(')){ if($diagSources -match [regex]::Escape($x)){ throw "Diagnostics unexpectedly mutates/schedules gameplay: $x" } }

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'; New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t278-main'; $diagRoot=Join-Path $build 'stage-t278-diag'; $bundle=Join-Path $build 'stage-t278-bundle'
foreach($p in @($mainRoot,$diagRoot,$bundle)){ if(Test-Path $p){ Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force -Path $p | Out-Null }
$mainStage=Join-Path $mainRoot 'RimMT'; $diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.8_Production.zip'; $diagZip=Join-Path $build 'RimMT_Diagnostics_v0.5.zip'; $bundleZip=Join-Path $build 'RimMT_T27.8_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force; Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse; Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $mainZip"; Write-Host "Built $diagZip"; Write-Host "Built $bundleZip"
