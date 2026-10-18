# Native local build against the user's licensed RimWorld 1.6 installation.
# Requires the .NET SDK already installed on the user's computer.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'Source\RimMT2\RimMT2.csproj'
$ManagedDir = $env:RIMWORLD_MANAGED_DIR
$HarmonyDll = $env:RIMMT_HARMONY_DLL
if (-not $ManagedDir -or -not (Test-Path -LiteralPath (Join-Path $ManagedDir 'Assembly-CSharp.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $ManagedDir 'UnityEngine.CoreModule.dll'))) {
    throw 'Set RIMWORLD_MANAGED_DIR to RimWorld 1.6 RimWorldWin64_Data\Managed.'
}
if (-not $HarmonyDll -or -not (Test-Path -LiteralPath $HarmonyDll)) {
    throw 'Set RIMMT_HARMONY_DLL to installed Harmony 0Harmony.dll.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'A .NET SDK is required. This script never downloads/installs it.'
}
dotnet build $Project -c Release -p:RimWorldManagedDir="$ManagedDir" -p:RimMTHarmonyDll="$HarmonyDll" -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$Dll = Join-Path $Root '1.6\Assemblies\RimMT2.dll'
$Hash = (Get-FileHash -LiteralPath $Dll -Algorithm SHA256).Hash
Write-Host "RIMMT2_BUILD_OK $Dll SHA256=$Hash"
