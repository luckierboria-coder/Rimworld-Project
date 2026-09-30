$ErrorActionPreference='Stop'

# First reproduce the fully verified T27.6 state, then apply the narrow T27.7 delta.
& (Join-Path $PSScriptRoot 'BuildRimMTT27_6.ps1')
if(-not $?){ throw 'T27.6 prerequisite build failed' }
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T27_7DeadPathRetirement.ps1')
if(-not $?){ throw 'T27.7 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'RimMT T27.7 build failed' }
dotnet build $diagProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.4 build failed' }

# ---- T27.7 production assertions ----
$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27\.7-dead-path-retirement'){ throw 'T27.7 version marker missing' }
if($boot -match 'AggressiveReachabilityProfilesV17\.Apply\(harmony\)'){ throw 'retired ReachProfile production Harmony path returned' }
foreach($forbidden in @('TailAttributionPatches093T1.Apply(harmony);','TailPawnPatches093T2.Apply(harmony);','PlayerHumanResidualPatches093T14.Apply(harmony);','WorldRootAttribution093T22.Apply(harmony);','DoBillTailFabric092.Apply(harmony);','ParallelWorkKernel093T27.Apply(harmony);','MobileSourceRescue093T18.Apply(harmony);')){
  if($boot.Contains($forbidden)){ throw "T27.7 retired/diagnostic install present: $forbidden" }
}

$merge=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs') -Raw
foreach($required in @('DiagnosticsHarmonyOwner','allen.rimmt.diagnostics','authorityTargetForeign','ThingCanStackAuthorityMode')){
  if(-not $merge.Contains($required)){ throw "T27.7 T4 marker missing: $required" }
}
if($merge -notmatch 'string\.Equals\(patch\.owner, DiagnosticsHarmonyOwner'){ throw 'T4 does not ignore measurement-only diagnostics owner' }

# Historical hard invariants stay mandatory.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate T26.1 zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){ if($kernel -match [regex]::Escape($x)){ throw "T27.7 zero-wait violation: $x" } }

# ---- Diagnostics v0.4 assertions ----
$diagPatches=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diagPatches -notmatch 'Version = "0\.4\.0"'){ throw 'Diagnostics v0.4 version missing' }
$v02=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV02.cs') -Raw
if($v02 -match 'PatchWorkGiverMethods\(harmony\)'){ throw 'duplicated v0.2 WorkGiver profiler still installed' }
if($v02 -notmatch 'WorkGiverSampled=RETIRED in Diagnostics v0\.4'){ throw 'v0.2 WorkGiver retirement marker missing' }
$v03=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV03.cs') -Raw
foreach($required in @('MaxMethodsPerDetermine = 256','WorstSlowDNJ','AddWorst','Worst.Clear')){ if(-not $v03.Contains($required)){ throw "Diagnostics v0.4 marker missing: $required" } }
$diagSources=(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics') -Filter '*.cs' -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach($x in @('StartJob(','EndCurrentJob(','scheduler.TryEnqueue','Task.Run(','ThreadPool.QueueUserWorkItem','SpinWait','Thread.Sleep(','Parallel.For(')){ if($diagSources -match [regex]::Escape($x)){ throw "Diagnostics unexpectedly mutates/schedules gameplay: $x" } }

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

# Package final installables.
$build=Join-Path $root 'build'; New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t277-main'; $diagRoot=Join-Path $build 'stage-t277-diag'; $bundle=Join-Path $build 'stage-t277-bundle'
foreach($p in @($mainRoot,$diagRoot,$bundle)){ if(Test-Path $p){ Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force -Path $p | Out-Null }
$mainStage=Join-Path $mainRoot 'RimMT'; $diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
if(Test-Path (Join-Path $root 'RimMT/README.md')){ Copy-Item (Join-Path $root 'RimMT/README.md') $mainStage }
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
if(Test-Path (Join-Path $root 'RimMTDiagnostics/README.md')){ Copy-Item (Join-Path $root 'RimMTDiagnostics/README.md') $diagStage }
$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.7_Production.zip'; $diagZip=Join-Path $build 'RimMT_Diagnostics_v0.4.zip'; $bundleZip=Join-Path $build 'RimMT_T27.7_With_Diagnostics_Bundle.zip'
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force; Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse; Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $mainZip"; Write-Host "Built $diagZip"; Write-Host "Built $bundleZip"