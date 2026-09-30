$ErrorActionPreference='Stop'

# Persistent local toolchain chosen by the project owner. Keep build tooling and
# package caches outside transient Codex task directories on C:.
$RimMTDotnetRoot='E:\Codex\toolchains\dotnet-sdk-8.0.425'
$RimMTNugetRoot='E:\Codex\caches\nuget-packages'
$RimMTDotnetHome='E:\Codex\caches\dotnet-home'
$RimMTDotnetExe=Join-Path $RimMTDotnetRoot 'dotnet.exe'

if(-not (Test-Path -LiteralPath $RimMTDotnetExe)){
  throw "Persistent RimMT .NET SDK is missing: $RimMTDotnetExe"
}
if(-not (Test-Path -LiteralPath $RimMTNugetRoot)){
  throw "Persistent RimMT NuGet cache is missing: $RimMTNugetRoot"
}
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
