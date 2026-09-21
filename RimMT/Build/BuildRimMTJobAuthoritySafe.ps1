$ErrorActionPreference='Stop'

# Build the exact A/B2 lineage first so this SAFE build is a one-step transform on the
# binary the user just tested.
& (Join-Path $PSScriptRoot 'BuildRimMTAB2GenClosestOff.ps1')
if(-not $?){ throw 'A/B2 prerequisite build failed' }

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

& (Join-Path $PSScriptRoot 'ApplyRimMTV093JobAuthoritySafe.ps1')
if(-not $?){ throw 'JOB AUTHORITY SAFE transform failed' }

$mainProj=Join-Path $root 'RimMT/Source/RimMT/RimMT.csproj'
$diagProj=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnostics.csproj'

dotnet build $mainProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'JOB AUTHORITY SAFE production build failed' }

dotnet build $diagProj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'Idle Authority Trace diagnostics build failed' }

# ---------------------------------------------------------------------------
# Hard validation: this is a correctness baseline, not another partial A/B.
# ---------------------------------------------------------------------------

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
if($boot -notmatch '0\.9\.3-job-authority-safe'){
    throw 'SAFE version marker missing'
}

$activeApplies=[regex]::Matches(
    $boot,
    '(?m)^\s*([A-Za-z_][A-Za-z0-9_\.]*\.Apply\(harmony\);)\s*$')
$unexpected=@()
foreach($m in $activeApplies){
    $call=$m.Groups[1].Value
    if($call -ne 'RimMTPatches.Apply(harmony);'){
        $unexpected += $call
    }
}
if($unexpected.Count -ne 0){
    throw ('SAFE bootstrap still has active Apply(harmony): ' + ($unexpected -join ', '))
}

$remainingSelfInstall=@()
foreach($dir in @(
    (Join-Path $root 'RimMT/Source/RimMT/AI'),
    (Join-Path $root 'RimMT/Source/RimMT/Compatibility')
)){
    if(-not (Test-Path $dir)){ continue }
    Get-ChildItem $dir -Filter '*.cs' -Recurse -File | ForEach-Object {
        $text=Get-Content $_.FullName -Raw
        if($text.Contains('LongEventHandler.ExecuteWhenFinished(Install);')){
            $remainingSelfInstall += $_.FullName
        }
    }
}
if($remainingSelfInstall.Count -ne 0){
    throw ('SAFE self-installers remain: ' + ($remainingSelfInstall -join ', '))
}

foreach($specific in @(
    'RimMT/Source/RimMT/AI/CompositeWorkPruners092.cs',
    'RimMT/Source/RimMT/AI/LargeSetTailRescue092.cs',
    'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs',
    'RimMT/Source/RimMT/Compatibility/CommonSenseIngredientExpand092.cs'
)){
    $path=Join-Path $root $specific
    if(-not (Test-Path $path)){ throw "SAFE expected source missing: $specific" }
    $text=Get-Content $path -Raw
    if($text.Contains('LongEventHandler.ExecuteWhenFinished(Install);')){
        throw "SAFE failed to neutralize self-installer: $specific"
    }
}

$diagPatch=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs') -Raw
if($diagPatch -notmatch 'Version = "0\.16\.0-safe"'){
    throw 'Diagnostics v0.16 SAFE marker missing'
}
$diagReport=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs') -Raw
if(-not $diagReport.Contains('IdleAuthorityTraceSafe.BuildSummary()')){
    throw 'Idle Authority Trace report integration missing'
}
$idleTrace=Get-Content (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/IdleAuthorityTraceSafe.cs') -Raw
foreach($forbidden in @(
    '.CanReach(',
    '.CanReserve(',
    'ReservationManager.CanReserve',
    'foreach (Thing thing in __result',
    'foreach (IntVec3 cell in __result'
)){
    if($idleTrace.Contains($forbidden)){
        throw "Idle Authority Trace contains forbidden active probe/enumeration marker: $forbidden"
    }
}
foreach($required in @(
    'Read-only correctness trace',
    'BoolPostfix',
    'JobPostfix',
    'ThingSourcePostfix',
    'CellSourcePostfix',
    'RecentIdleAuthority='
)){
    if(-not $idleTrace.Contains($required)){
        throw "Idle Authority Trace invariant missing: $required"
    }
}

$mainDlls=@(Get-ChildItem (Join-Path $root 'RimMT/1.5/Assemblies') -Filter '*.dll' -File)
if($mainDlls.Count -ne 1 -or $mainDlls[0].Name -ne 'RimMT.dll'){
    throw "Unexpected RimMT DLL set: $($mainDlls.Name -join ', ')"
}
$diagDlls=@(Get-ChildItem (Join-Path $root 'RimMTDiagnostics/1.5/Assemblies') -Filter '*.dll' -File)
if($diagDlls.Count -ne 1 -or $diagDlls[0].Name -ne 'RimMT.Diagnostics.dll'){
    throw "Unexpected Diagnostics DLL set: $($diagDlls.Name -join ', ')"
}

# ---------------------------------------------------------------------------
# Package production, diagnostics, and a two-folder install bundle.
# ---------------------------------------------------------------------------

$build=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null

function Stage-Mod {
    param([string]$Source,[string]$Destination)
    if(Test-Path $Destination){ Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item (Join-Path $Source 'About') $Destination -Recurse
    if(Test-Path (Join-Path $Source 'Languages')){
        Copy-Item (Join-Path $Source 'Languages') $Destination -Recurse
    }
    Copy-Item (Join-Path $Source '1.5') $Destination -Recurse
    Copy-Item (Join-Path $Source 'LoadFolders.xml') $Destination
}

$mainStageRoot=Join-Path $build 'stage-job-authority-safe-main'
$mainStage=Join-Path $mainStageRoot 'RimMT'
Stage-Mod (Join-Path $root 'RimMT') $mainStage

$diagStageRoot=Join-Path $build 'stage-idle-authority-trace-diag'
$diagStage=Join-Path $diagStageRoot 'RimMTDiagnostics'
Stage-Mod (Join-Path $root 'RimMTDiagnostics') $diagStage

$bundleRoot=Join-Path $build 'stage-job-authority-safe-bundle'
if(Test-Path $bundleRoot){ Remove-Item $bundleRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null
Copy-Item $mainStage (Join-Path $bundleRoot 'RimMT') -Recurse
Copy-Item $diagStage (Join-Path $bundleRoot 'RimMTDiagnostics') -Recurse

$mainZip=Join-Path $build 'RimMT_V0.9.3_JOB_AUTHORITY_SAFE.zip'
$diagZip=Join-Path $build 'RimMT_Diagnostics_v0.16_IDLE_AUTHORITY_TRACE_SAFE.zip'
$bundleZip=Join-Path $build 'RimMT_JOB_AUTHORITY_SAFE_WITH_IDLE_TRACE_BUNDLE.zip'

foreach($zip in @($mainZip,$diagZip,$bundleZip)){
    if(Test-Path $zip){ Remove-Item $zip -Force }
}
Compress-Archive -Path $mainStage -DestinationPath $mainZip -Force
Compress-Archive -Path $diagStage -DestinationPath $diagZip -Force
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $bundleZip -Force

Write-Host "Built production SAFE: $mainZip"
Write-Host "SHA256(main)=$((Get-FileHash $mainZip -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Host "Built diagnostics SAFE: $diagZip"
Write-Host "SHA256(diag)=$((Get-FileHash $diagZip -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Host "Built bundle: $bundleZip"
Write-Host "SHA256(bundle)=$((Get-FileHash $bundleZip -Algorithm SHA256).Hash.ToLowerInvariant())"
