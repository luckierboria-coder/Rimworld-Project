$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32C1.ps1')
if(-not $?){ throw 'T32-C.1 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093AB1S4S51Off.ps1')
if(-not $?){ throw 'A/B1 transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'A/B1 production build failed' }

$boot=Get-Content (Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs') -Raw
$s51=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverHybridTailS51.cs') -Raw
$s4=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs') -Raw
$t32=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw

if($boot -notmatch '0\.9\.3-t32c1-ab1-s4-s51-off'){
  throw 'A/B1 version marker missing'
}

# A/B isolation must be hard, not a runtime timing-dependent bypass.
if($boot.Contains('JobGiverSlowSearch0419S.Apply(harmony);')){
  throw 'A/B1 still installs S4'
}
if($s51.Contains('LongEventHandler.ExecuteWhenFinished(Install);')){
  throw 'A/B1 still schedules S5.1 installer'
}
if(-not $s51.Contains('S5.1 tail rescue authority disabled')){
  throw 'A/B1 S5.1 disabled marker missing'
}
if(-not $boot.Contains('S4 slow-search rescue intentionally not installed')){
  throw 'A/B1 S4 disabled marker missing'
}

# The implementation bodies remain present for later diagnosis; only installation is removed.
foreach($required in @(
  'private static void Install()',
  'public static bool RoutePrefix',
  'private static bool TryFast'
)){
  if(-not $s51.Contains($required)){ throw "A/B1 unexpectedly removed S5.1 implementation: $required" }
}
foreach($required in @(
  'internal static void Apply(Harmony harmony)',
  'public static bool Prefix',
  'private static bool TryAccelerateCustom'
)){
  if(-not $s4.Contains($required)){ throw "A/B1 unexpectedly removed S4 implementation: $required" }
}

# T32-C.1 / T32-A authority remains exactly enabled.
foreach($required in @(
  'PositiveWarmupMatches = 32',
  'PositiveVerifyMask = 63',
  'positiveRuntimeQuarantined',
  'positiveAuthoritativeHits',
  '__result = true;',
  'T32-A false replay and T32-C.1 positive replay use independent trust'
)){
  if(-not $t32.Contains($required)){ throw "A/B1 lost T32-C.1 invariant: $required" }
}
$trueAssignments=[regex]::Matches($t32,'__result\s*=\s*true\s*;')
if($trueAssignments.Count -ne 1){
  throw "A/B1 changed T32-C.1 positive authority count: $($trueAssignments.Count)"
}

# T21/T32-B.1 remains unchanged.
foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'validator caches false only',
  'enter>=1/8@128+trusted, return<1/16@256'
)){
  if(-not $t20.Contains($required)){ throw "A/B1 lost T21/T32-B.1 invariant: $required" }
}

# T22 and other production modules stay installed: this is a two-variable isolation only.
foreach($required in @(
  'JobSearchPackageContext093T28.Apply(harmony);',
  'JobSearchTransaction093T20.Apply(harmony);',
  'GenClosestTransactionIndex093T22.Apply(harmony);',
  'ReservationTransaction093T32A.Apply(harmony);'
)){
  if(-not $boot.Contains($required)){ throw "A/B1 unexpectedly disabled retained module: $required" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){
  throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')"
}

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$stageRoot=Join-Path $build 'stage-ab1-s4-s51-off'
if(Test-Path $stageRoot){ Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$stage=Join-Path $stageRoot 'RimMT'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $root 'RimMT/About') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/Languages') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/1.5') $stage -Recurse
Copy-Item (Join-Path $root 'RimMT/LoadFolders.xml') $stage

$zip=Join-Path $build 'RimMT_V0.9.3_T32C1_AB1_S4_S51_OFF.zip'
if(Test-Path $zip){ Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -Force

$hash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built $zip"
Write-Host "SHA256=$hash"
