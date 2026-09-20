$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT32C1.ps1')
if(-not $?){ throw 'T32-C.1 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T33ACandidateRejectionCensus.ps1')
if(-not $?){ throw 'T33-A transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

# Production is intentionally unchanged from T32-C.1. Rebuild diagnostics only after transform,
# but compile production again as a regression guard against accidental source coupling.
dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T33-A regression build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.15 build failed' }

$diagPatch=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diagPatch -notmatch 'Version = "0\.15\.0"'){ throw 'Diagnostics v0.15 version missing' }
if(-not $diagPatch.Contains('CandidateRejectionCensusT33A.Apply(harmony);')){ throw 'T33-A bootstrap apply missing' }

$censusPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/CandidateRejectionCensusT33A.cs'
if(-not (Test-Path $censusPath)){ throw 'T33-A census source missing' }
$c=Get-Content $censusPath -Raw

foreach($required in @(
  'SampleMask = 7',
  'validatorsRunOriginalBypass',
  'thing.IsForbidden(packagePawn)',
  'CanReservePostfix',
  'CanReachPostfix',
  'rejectAnySharedNegative',
  'acceptAnySharedNegative',
  'RejectMasks',
  'AcceptMasks',
  'Interpretation rule: a primitive is NOT eligible for generic early rejection',
  'Measurement-only; results are never altered.'
)){
  if(-not $c.Contains($required)){ throw "T33-A invariant missing: $required" }
}

# Measurement-only hard guard: census must never assign Harmony results or skip originals.
foreach($forbidden in @(
  '__result =',
  'StartJob(',
  'EndCurrentJob(',
  '.Reserve(',
  '.Release(',
  'Task.Run(',
  'ThreadPool.QueueUserWorkItem',
  'SpinWait',
  'Thread.Sleep(',
  'Parallel.For('
)){
  if($c.Contains($forbidden)){ throw "T33-A measurement-only violation: $forbidden" }
}

# Harmony measurement methods must not be bool prefixes that can skip originals.
# Helper methods such as TargetsCandidate / ScannerAccessor.Find may legitimately return false.
if(-not $c.Contains('if (!__runOriginal)')){ throw 'T33-A __runOriginal live-only guard missing' }
foreach($patchMethod in @('ValidatorPrefix','CanReservePostfix','CanReachPostfix','PackagePrefix','PackageFinalizer','ValidatorPostfix')){
  $sig=[regex]::Match($c,'public static\s+([A-Za-z0-9_<>]+)\s+' + [regex]::Escape($patchMethod) + '\s*\(')
  if(-not $sig.Success){ throw "T33-A Harmony method missing: $patchMethod" }
  if($patchMethod -ne 'PackageFinalizer' -and $sig.Groups[1].Value -eq 'bool'){
    throw "T33-A Harmony method may skip original: $patchMethod"
  }
}

# Candidate-scoped primitive attribution must require the same pawn/target where applicable.
foreach($required in @(
  'ReferenceEquals(__0, probe.Pawn)',
  'TargetsCandidate(__1, probe.Thing)',
  'TargetsCandidate(dest, probe.Thing)'
)){
  if(-not $c.Contains($required)){ throw "T33-A candidate attribution guard missing: $required" }
}

# Production T32-C.1 must remain present and unchanged in its critical authority constants.
$t32=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
foreach($required in @(
  'PositiveWarmupMatches = 32',
  'PositiveVerifyMask = 63',
  'positiveRuntimeQuarantined',
  '__result = true;',
  'positiveAuthoritativeHits'
)){
  if(-not $t32.Contains($required)){ throw "T32-C.1 invariant lost in T33-A: $required" }
}
$trueAssignments=[regex]::Matches($t32,'__result\s*=\s*true\s*;')
if($trueAssignments.Count -ne 1){ throw "T32-C.1 positive authority changed unexpectedly: $($trueAssignments.Count)" }

# T32-B.1 generic adaptive Forbidden remains intact.
$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'enter>=1/8@128+trusted, return<1/16@256',
  'validator caches false only'
)){
  if(-not $t20.Contains($required)){ throw "T32-B.1 invariant lost in T33-A: $required" }
}

# No named optimization rules. Names may appear only at runtime in report data.
foreach($forbidden in @(
  'Warden_DeliverFood','HaulToInventory','IrrigationBasic','MedievalOverhaul',
  'PickUpAndHaul','DubsBadHygiene','Hospitality','ProcessorFramework',
  'TakeEntityToHoldingPlatform','FeedHemogen','HaulCorpses'
)){
  if($c -match [regex]::Escape($forbidden)){ throw "T33-A named specialization found: $forbidden" }
}

$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('CandidateRejectionCensusT33A.BuildSummary()')){
  throw 'T33-A report integration missing'
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

$mainZip=Join-Path $build 'RimMT_V0.9.3_T32C1_Production.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.15_T33A.zip'
$bundleZip=Join-Path $build 'RimMT_T33A_Census_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
