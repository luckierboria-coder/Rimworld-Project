$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTAB1S4S51Off.ps1')
if(-not $?){ throw 'A/B1 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093AB2GenClosestOff.ps1')
if(-not $?){ throw 'A/B2 transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'A/B2 production build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
$t28=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs') -Raw
$t22=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs') -Raw
$global=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs') -Raw
$broad=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/BroadGenClosestOrder0418.cs') -Raw
$s51=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverHybridTailS51.cs') -Raw
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
$t32=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw

if($boot -notmatch '0\.9\.3-t32c1-ab2-genclosest-off'){
  throw 'A/B2 version marker missing'
}

if($boot.Contains('JobGiverSlowSearch0419S.Apply(harmony);')){
  throw 'A/B2 unexpectedly re-enabled S4'
}
if($s51.Contains('LongEventHandler.ExecuteWhenFinished(Install);')){
  throw 'A/B2 unexpectedly re-enabled S5.1'
}

$sourceParts=@()
Get-ChildItem (Join-Path $root 'RimMT/Source/RimMT') -Filter '*.cs' -Recurse -File | ForEach-Object {
  $sourceParts += (Get-Content $_.FullName -Raw)
}
$allSource=[string]::Join([Environment]::NewLine,$sourceParts)

if($allSource.Contains('GenClosestTransactionIndex093T22.Apply(harmony);')){
  throw 'A/B2 still installs T22 authority'
}
if($allSource.Contains('JobGiverGlobalNearest04181.Apply(harmony);')){
  throw 'A/B2 still installs GlobalNearest candidate rewriting'
}

if($t28.Contains('GenClosestTransactionIndex093T22.PackagePrefix(ref __state.T22);')){
  throw 'A/B2 still enters T22 package state'
}
if($t28.Contains('GenClosestTransactionIndex093T22.PackageFinalizer(__exception, __state.T22);')){
  throw 'A/B2 still exits T22 package state'
}

foreach($required in @(
  'JobGiverGlobalNearest04181.JobGiverPrefix(__0);',
  '__state.GlobalNearestEntered = true;',
  'JobGiverGlobalNearest04181.JobGiverFinalizer(__exception)'
)){
  if(-not $t28.Contains($required)){ throw "A/B2 lost GlobalNearest scope-only guard: $required" }
}
if(-not $broad.Contains('JobGiverGlobalNearest04181.InJobGiverScope')){
  throw 'A/B2 BroadGenClosestOrder no longer bypasses JobGiver scope'
}

foreach($required in @(
  'public static bool GlobalPrefix',
  'authoritativeNull',
  'ClosestThing_Global_NewTemp'
)){
  if(-not $t22.Contains($required)){ throw "A/B2 unexpectedly removed T22 implementation: $required" }
}
foreach($required in @(
  'public static void GlobalPrefix',
  'public static void GlobalReachablePrefix',
  'private static void TryReorder'
)){
  if(-not $global.Contains($required)){ throw "A/B2 unexpectedly removed GlobalNearest implementation: $required" }
}

foreach($required in @(
  'JobSearchTransaction093T20.Apply(harmony);',
  'ReservationTransaction093T32A.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "A/B2 unexpectedly disabled retained module: $required" }
}
foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'validator caches false only',
  'enter>=1/8@128+trusted, return<1/16@256'
)){
  if(-not $t20.Contains($required)){ throw "A/B2 lost T21/T32-B.1 invariant: $required" }
}

foreach($required in @(
  'PositiveWarmupMatches = 32',
  'PositiveVerifyMask = 63',
  'positiveRuntimeQuarantined',
  'positiveAuthoritativeHits',
  '__result = true;',
  'T32-A false replay and T32-C.1 positive replay use independent trust'
)){
  if(-not $t32.Contains($required)){ throw "A/B2 lost T32-C.1 invariant: $required" }
}
$trueAssignments=[regex]::Matches($t32,'__result\s*=\s*true\s*;')
if($trueAssignments.Count -ne 1){
  throw "A/B2 changed T32-C.1 positive authority count: $($trueAssignments.Count)"
}

foreach($required in @(
  'PersistentDoBillIndex092',
  'HaulWorkAccelerator.Apply(harmony);',
  'GlobalHaulAccelerator.Apply(harmony);'
)){
  if(-not $allSource.Contains($required)){ throw "A/B2 retained production marker missing: $required" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){
  throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')"
}

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$stageRoot=Join-Path $build 'stage-ab2-genclosest-off'
if(Test-Path $stageRoot){ Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$stage=Join-Path $stageRoot 'RimMT'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $stage

$zip=Join-Path $build 'RimMT_V0.9.3_T32C1_AB2_GenClosest_OFF.zip'
if(Test-Path $zip){ Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -Force

$hash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built $zip"
Write-Host "SHA256=$hash"
