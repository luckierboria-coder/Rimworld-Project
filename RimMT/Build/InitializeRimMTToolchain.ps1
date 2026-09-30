$ErrorActionPreference='Stop'

# RimMT build-environment policy:
# 1) Prefer the project owner's persistent local SDK/cache when it already exists.
# 2) Otherwise use the dotnet SDK already provided by the host (for example GitHub Actions).
# 3) Never download, install, bootstrap, or construct a new toolchain here.
$RimMTDotnetRoot='E:\Codex\toolchains\dotnet-sdk-8.0.425'
$RimMTNugetRoot='E:\Codex\caches\nuget-packages'
$RimMTDotnetHome='E:\Codex\caches\dotnet-home'
$RimMTDotnetExe=$RimMTDotnetRoot + '\dotnet.exe'

$localReady=(Test-Path -LiteralPath $RimMTDotnetExe -ErrorAction SilentlyContinue) -and
  (Test-Path -LiteralPath $RimMTNugetRoot -ErrorAction SilentlyContinue)

if($localReady){
  New-Item -ItemType Directory -Force -Path $RimMTDotnetHome | Out-Null
  $env:DOTNET_ROOT=$RimMTDotnetRoot
  $env:DOTNET_CLI_HOME=$RimMTDotnetHome
  $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
  $env:NUGET_PACKAGES=$RimMTNugetRoot
  $env:NuGetAudit='false'

  $pathEntries=$env:PATH -split ';'
  if($pathEntries -notcontains $RimMTDotnetRoot){
    $env:PATH=$RimMTDotnetRoot + ';' + $env:PATH
  }

  Write-Host "[RimMT Build] Using existing local SDK: $RimMTDotnetExe"
  return
}

$hostDotnet=Get-Command dotnet -ErrorAction SilentlyContinue
if($null -eq $hostDotnet){
  throw 'No existing RimMT local SDK and no host-provided dotnet SDK. Per project policy the build will not install or bootstrap a toolchain.'
}

# Host-provided environment (e.g. GitHub Actions runner / setup-dotnet).
# Do not override DOTNET_ROOT/NUGET_PACKAGES unless the host already supplied them.
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:NuGetAudit='false'
Write-Host "[RimMT Build] Local persistent SDK unavailable; using existing host SDK: $($hostDotnet.Source)"
