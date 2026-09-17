$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'ApplyRimMTV093T27_7DeadPathRetirement.ps1')
if(-not $?){ throw 'T27.7 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'
dotnet build $mainProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'RimMT T27.7 build failed' }
dotnet build $diagProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.4 build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27\.7-dead-path-retirement'){ throw 'T27.7 version marker missing' }
if($boot -match 'AggressiveReachabilityProfilesV17\.Apply\(harmony\)'){ throw 'ReachProfile production Harmony path returned' }
foreach($x in @('TailPawnPatches093T2.Apply(harmony);','PlayerHumanResidualPatches093T14.Apply(harmony);','WorldRootAttribution093T22.Apply(harmony);','DoBillTailFabric092.Apply(harmony);','ParallelWorkKernel093T27.Apply(harmony);','MobileSourceRescue093T18.Apply(harmony);')){ if($boot.Contains($x)){ throw "retired path present: $x" } }

$merge=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs') -Raw
foreach($x in @('DiagnosticsHarmonyOwner','allen.rimmt.diagnostics','authorityTargetForeign','ThingCanStackAuthorityMode')){ if(-not $merge.Contains($x)){ throw "T4 marker missing: $x" } }
if($merge -notmatch 'string\.Equals\(patch\.owner, DiagnosticsHarmonyOwner'){ throw 'T4 does not recognize diagnostics owner' }

$diagPatches=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diagPatches -notmatch 'Version = "0\.4\.0"'){ throw 'Diagnostics v0.4 version missing' }
$v02=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV02.cs') -Raw
if($v02 -match 'PatchWorkGiverMethods\(harmony\)'){ throw 'v0.2 duplicate WorkGiver profiler remains installed' }
$v03=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV03.cs') -Raw
foreach($x in @('MaxMethodsPerDetermine = 256','WorstSlowDNJ','AddWorst','Worst.Clear')){ if(-not $v03.Contains($x)){ throw "Diagnostics v0.4 marker missing: $x" } }

$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){ if($kernel -match [regex]::Escape($x)){ throw "zero-wait violation: $x" } }

$diagSources=(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics') -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach($x in @('StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(')){ if($diagSources -match [regex]::Escape($x)){ throw "Diagnostics gameplay mutation/scheduling marker: $x" } }

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw 'unexpected RimMT DLL set' }
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw 'unexpected Diagnostics DLL set' }

$build=Join-Path $root 'build'; New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t277-main'; $diagRoot=Join-Path $build 'stage-t277-diag'; $bundleRoot=Join-Path $build 'stage-t277-bundle'
foreach($p in @($mainRoot,$diagRoot,$bundleRoot)){ if(Test-Path $p){ Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force -Path $p | Out-Null }
$mainStage=Join-Path $mainRoot 'RimMT'; $diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
if(Test-Path (Join-Path $root 'RimMT/README.md')){ Copy-Item (Join-Path $root 'RimMT/README.md') $mainStage }
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
if(Test-Path (Join-Path $root 'RimMTDiagnostics/README.md')){ Copy-Item (Join-Path $root 'RimMTDiagnostics/README.md') $diagStage }
$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.7_Production.zip'; $diagZip=Join-Path $build 'RimMT_Diagnostics_v0.4.zip'; $bundleZip=Join-Path $build 'RimMT_T27.7_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force; Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundleRoot 'RimMT') -Recurse; Copy-Item $diagStage (Join-Path $bundleRoot 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $bundleZip -Force
Write-Host 'Built T27.7 Dead Path Retirement + Diagnostics v0.4 packages.'