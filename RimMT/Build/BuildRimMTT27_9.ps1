$ErrorActionPreference='Stop'

# Start from the already verified T27.8 reconstruction, then apply only the T27.9 architectural delta.
& (Join-Path $PSScriptRoot 'BuildRimMTT27_8.ps1')
if(-not $?){ throw 'T27.8 prerequisite build failed' }
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T27_9PackageContext.ps1')
if(-not $?){ throw 'T27.9 transform failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'RimMT T27.9 build failed' }
dotnet build $diagProj --configuration Release --no-restore; if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.5 rebuild failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
if($boot -notmatch '0\.9\.3-t27\.9-package-context'){ throw 'T27.9 version marker missing' }
if($boot -match 'AdaptiveGenClosestAssist\.Apply\(harmony\)'){ throw 'retired persistent-map-fabric consumer still installs' }
if($boot -notmatch 'T27\.9 retired PersistentMapSearchFabric'){ throw 'persistent fabric retirement reason missing' }
if($boot -match 'AggressiveReachabilityProfilesV17\.Apply\(harmony\)'){ throw 'retired ReachProfile returned' }

$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @('CurrentPackageSerial','currentPackageSerial','packageSerialCounter','internal static bool InPackage','internal static Pawn CurrentPawn')){
    if(-not $t20.Contains($required)){ throw "T20 shared package marker missing: $required" }
}
if($t20 -notmatch 'currentPackageSerial = Interlocked\.Increment\(ref packageSerialCounter\)'){ throw 'T20 does not assign unique package serial' }
if($t20 -notmatch 'currentPackageSerial = 0L'){ throw 'T20 package serial is not cleared at outer boundary' }

$global=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs') -Raw
if($global -match 'harmony\.Patch\(jobGiver'){ throw 'GlobalNearest still owns a duplicate JobGiver_Work lifecycle patch' }
foreach($required in @('JobSearchTransaction093T20.InPackage','JobSearchTransaction093T20.CurrentPackageSerial','borrowedPackageSerial')){
    if(-not $global.Contains($required)){ throw "GlobalNearest shared-scope marker missing: $required" }
}

$t22Path=Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
$t22=Get-Content $t22Path -Raw
if($t22 -match 'harmony\.Patch\(package'){ throw 'T22 still owns a duplicate JobGiver_Work lifecycle patch' }
foreach($required in @('JobSearchTransaction093T20.CurrentPackageSerial','borrowedPackageSerial','packagePatched = true;')){
    if(-not $t22.Contains($required)){ throw "T22 shared-scope marker missing: $required" }
}

$bill=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs') -Raw
if($bill -match 'JobGiverGlobalNearest04181\.(InJobGiverScope|CurrentScopeStartTicks)'){ throw 'DoBill still depends on legacy package lifecycle' }
foreach($required in @('JobSearchTransaction093T20.InPackage','JobSearchTransaction093T20.CurrentPackageSerial','readinessFalseMemoHits')){
    if(-not $bill.Contains($required)){ throw "DoBill shared-scope marker missing: $required" }
}

$t4=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs') -Raw
if($t4 -match 'JobGiverGlobalNearest04181\.(InJobGiverScope|CurrentScopeStartTicks)'){ throw 'T4 still depends on legacy package lifecycle' }
foreach($required in @('JobSearchTransaction093T20.InPackage','JobSearchTransaction093T20.CurrentPackageSerial')){
    if(-not $t4.Contains($required)){ throw "T4 shared-scope marker missing: $required" }
}

# Diagnostics v0.5 remains bit-conceptually unchanged: no search hooks when timing is off.
$diag=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diag -notmatch 'Version = "0\.5\.0"'){ throw 'Diagnostics v0.5 version changed unexpectedly' }
if($diag -notmatch 'if \(RimMTDiagnosticsSettings\.EnableSearchTiming\)'){ throw 'Diagnostics search hooks became unconditional' }

# Hard safety invariants from the T27 family.
$epoch=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs') -Raw
$a=$epoch.IndexOf('internal static bool TryComputeRingKeys'); $b=$epoch.IndexOf('internal static string Summary()',$a)
if($a -lt 0 -or $b -lt 0){ throw 'Cannot isolate zero-wait kernel' }
$kernel=$epoch.Substring($a,$b-$a)
foreach($x in @('SpinOnce(','new SpinWait(','.Wait(','.Join(','Thread.Sleep(','ManualResetEvent')){ if($kernel -match [regex]::Escape($x)){ throw "T27.9 zero-wait violation: $x" } }

$allMain=(Get-ChildItem (Join-Path $root 'RimMT/Source/RimMT') -Filter '*.cs' -File -Recurse | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
if($allMain -match 'DefDatabase<TraitDef>.*Harmony' -or $allMain -match 'TechLevelDatabase<TraitDef>.*harmony\.Patch'){ throw 'closed generic Def Harmony safety regression' }

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){ throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')" }
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){ throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')" }

$build=Join-Path $root 'build'; New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t279-main'; $diagRoot=Join-Path $build 'stage-t279-diag'; $bundle=Join-Path $build 'stage-t279-bundle'
foreach($p in @($mainRoot,$diagRoot,$bundle)){ if(Test-Path $p){ Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force -Path $p | Out-Null }
$mainStage=Join-Path $mainRoot 'RimMT'; $diagStage=Join-Path $diagRoot 'RimMTDiagnostics'
New-Item -ItemType Directory -Force -Path $mainStage,$diagStage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/Languages') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/1.5') $mainStage -Recurse; Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $mainStage
Copy-Item (Join-Path $root 'RimMTDiagnostics/About') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/1.5') $diagStage -Recurse; Copy-Item (Join-Path $root 'RimMTDiagnostics/LoadFolders.xml') $diagStage
$mainZip=Join-Path $build 'RimMT_V0.9.3_T27.9_Production.zip'; $diagZip=Join-Path $build 'RimMT_Diagnostics_v0.5_T27.9.zip'; $bundleZip=Join-Path $build 'RimMT_T27.9_With_Diagnostics_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force; Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse; Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force
Write-Host "Built $mainZip"; Write-Host "Built $diagZip"; Write-Host "Built $bundleZip"
