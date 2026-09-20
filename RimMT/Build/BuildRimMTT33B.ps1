$ErrorActionPreference='Stop'

& (Join-Path $PSScriptRoot 'BuildRimMTT33A.ps1')
if(-not $?){ throw 'T33-A prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $PSScriptRoot 'ApplyRimMTV093T33BRejectionFamilyCensus.ps1')
if(-not $?){ throw 'T33-B transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

# Production must remain byte-source-equivalent to the T32-C.1/T33-A production line.
dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'RimMT T33-B regression build failed' }
dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Diagnostics v0.16 build failed' }

$diagPatch=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diagPatch -notmatch 'Version = "0\.16\.0"'){ throw 'Diagnostics v0.16 version missing' }
foreach($required in @(
  'CandidateRejectionCensusT33A.Apply(harmony);',
  'CandidateRejectionFamilyCensusT33B.Apply(harmony);'
)){
  if(-not $diagPatch.Contains($required)){ throw "T33-B bootstrap marker missing: $required" }
}

$t33bPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/CandidateRejectionFamilyCensusT33B.cs'
if(-not (Test-Path $t33bPath)){ throw 'T33-B source missing' }
$c=Get-Content $t33bPath -Raw

foreach($required in @(
  'SampleMask = 63',
  'MaxFamilies = 4096',
  'MaxMembersPerMethod = 96',
  'DefDatabase<WorkGiverDef>.AllDefsListForReading',
  '"HasJobOnThing"',
  'typeof(Pawn), typeof(Thing), typeof(bool)',
  'GetMethodBody()',
  'ResolveMember(token, typeArgs, methodArgs)',
  'OpCodes.Call.Value',
  'OpCodes.Callvirt.Value',
  'OpCodes.Ldfld.Value',
  'OpCodes.Ldsfld.Value',
  'StructuralScanners',
  'RejectScanners',
  'AcceptScanners',
  'RejectWeight',
  'AcceptWeight',
  'structural IL presence, not executed-call proof',
  'discovery only',
  'No member is called by T33-B and no HasJobOnThing result is changed.'
)){
  if(-not $c.Contains($required)){ throw "T33-B invariant missing: $required" }
}

# Measurement-only hard guards.
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
  'Parallel.For(',
  'MethodInfo.Invoke(',
  'MethodBase.Invoke('
)){
  if($c.Contains($forbidden)){ throw "T33-B measurement-only violation: $forbidden" }
}

# Harmony methods must not be bool prefixes capable of skipping originals.
foreach($patchMethod in @('HasJobPrefix','HasJobPostfix')){
  $sig=[regex]::Match($c,'public static\s+([A-Za-z0-9_<>]+)\s+' + [regex]::Escape($patchMethod) + '\s*\(')
  if(-not $sig.Success){ throw "T33-B Harmony method missing: $patchMethod" }
  if($sig.Groups[1].Value -eq 'bool'){
    throw "T33-B Harmony method may skip original: $patchMethod"
  }
}
if(-not $c.Contains('if (!__runOriginal)')){ throw 'T33-B __runOriginal guard missing' }

# Only direct method calls and field reads may become census families.
if($c.Contains('OpCodes.Newobj.Value')){ throw 'T33-B constructor family expansion is not allowed' }
if($c.Contains('OpCodes.Stfld.Value') -or $c.Contains('OpCodes.Stsfld.Value')){
  throw 'T33-B field-write family expansion is not allowed'
}

# The runtime census must only weight prebuilt MemberFamily records.
$postStart=$c.IndexOf('public static void HasJobPostfix')
$profileStart=$c.IndexOf('private static ScannerProfile GetScannerProfile',$postStart)
if($postStart -lt 0 -or $profileStart -lt 0){ throw 'Cannot isolate T33-B postfix' }
$post=$c.Substring($postStart,$profileStart-$postStart)
foreach($forbidden in @('ResolveMember(','GetMethodBody(','GetILAsByteArray(','AccessTools.Method(')){
  if($post.Contains($forbidden)){ throw "T33-B runtime sample performs discovery work: $forbidden" }
}

# T33-A must remain unchanged in its sampling semantics and measurement-only guards.
$t33a=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/CandidateRejectionCensusT33A.cs') -Raw
foreach($required in @(
  'SampleMask = 7',
  'thing.IsForbidden(packagePawn)',
  'CanReservePostfix',
  'CanReachPostfix',
  'Interpretation rule: a primitive is NOT eligible for generic early rejection'
)){
  if(-not $t33a.Contains($required)){ throw "T33-A invariant lost in T33-B: $required" }
}
if($t33a.Contains('__result =')){ throw 'T33-A result mutation appeared in T33-B branch' }

# Production T32-C.1 remains unchanged in critical positive/negative authority.
$t32=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/ReservationTransaction093T32A.cs') -Raw
foreach($required in @(
  'PositiveWarmupMatches = 32',
  'PositiveVerifyMask = 63',
  'positiveRuntimeQuarantined',
  '__result = true;',
  'positiveAuthoritativeHits'
)){
  if(-not $t32.Contains($required)){ throw "T32-C.1 invariant lost in T33-B: $required" }
}
$trueAssignments=[regex]::Matches($t32,'__result\s*=\s*true\s*;')
if($trueAssignments.Count -ne 1){ throw "T32-C.1 positive authority changed unexpectedly: $($trueAssignments.Count)" }

$t20=Get-Content (Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs') -Raw
foreach($required in @(
  'AdaptiveEagerEnterMinStores = 128',
  'AdaptiveLazyReturnMinStores = 256',
  'enter>=1/8@128+trusted, return<1/16@256',
  'validator caches false only'
)){
  if(-not $t20.Contains($required)){ throw "T32-B.1 invariant lost in T33-B: $required" }
}

# No named WorkGiver/mod optimization specialization.
foreach($forbidden in @(
  'Warden_DeliverFood','HaulToInventory','IrrigationBasic','MedievalOverhaul',
  'PickUpAndHaul','DubsBadHygiene','Hospitality','ProcessorFramework',
  'TakeEntityToHoldingPlatform','FeedHemogen','HaulCorpses',
  'FightFires','TakeToPen','RebalanceAnimalsInPens'
)){
  if($c -match [regex]::Escape($forbidden)){ throw "T33-B named specialization found: $forbidden" }
}

$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
foreach($required in @(
  'CandidateRejectionCensusT33A.BuildSummary()',
  'CandidateRejectionFamilyCensusT33B.BuildSummary()'
)){
  if(-not $diagReport.Contains($required)){ throw "T33-B report integration missing: $required" }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){
  throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')"
}
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){
  throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')"
}

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$mainRoot=Join-Path $build 'stage-t33b-main'
$diagRoot=Join-Path $build 'stage-t33b-diag'
$bundle=Join-Path $build 'stage-t33b-bundle'
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
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.16_T33B.zip'
$bundleZip=Join-Path $build 'RimMT_T33B_Family_Census_Bundle.zip'
foreach($z in @($mainZip,$diagZip,$bundleZip)){ if(Test-Path $z){ Remove-Item $z -Force } }

Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Copy-Item $mainStage (Join-Path $bundle 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundle 'RimMTDiagnostics') -Recurse
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $bundleZip -Force

Write-Host "Built $mainZip"
Write-Host "Built $diagZip"
Write-Host "Built $bundleZip"
