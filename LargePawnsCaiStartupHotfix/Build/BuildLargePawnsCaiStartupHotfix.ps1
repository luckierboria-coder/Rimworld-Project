$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../RimMT/Build/InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$proj=Join-Path $root 'LargePawnsCaiStartupHotfix/Source/LargePawnsCaiStartupHotfix/LargePawnsCaiStartupHotfix.csproj'

dotnet build $proj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'LargePawns CAI Startup Hotfix build failed' }

$dll=Join-Path $root 'LargePawnsCaiStartupHotfix/1.5/Assemblies/LargePawns.CaiStartupHotfix.dll'
if(-not (Test-Path $dll)){ throw 'LargePawns CAI Startup Hotfix DLL missing' }

$stage=Join-Path $root 'build/stage-largepawns-cai-hotfix'
if(Test-Path $stage){ Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$mod=Join-Path $stage 'LargePawnsCaiStartupHotfix'
New-Item -ItemType Directory -Force -Path $mod | Out-Null
Copy-Item (Join-Path $root 'LargePawnsCaiStartupHotfix/About') $mod -Recurse
Copy-Item (Join-Path $root 'LargePawnsCaiStartupHotfix/1.5') $mod -Recurse
Copy-Item (Join-Path $root 'LargePawnsCaiStartupHotfix/LoadFolders.xml') $mod
Get-ChildItem $mod -Recurse -Filter '*.pdb' | Remove-Item -Force

$zip=Join-Path $root 'build/LargePawns_CAI_Startup_Hotfix_1.5_V1.0.zip'
if(Test-Path $zip){ Remove-Item $zip -Force }
Compress-Archive -Path $mod -DestinationPath $zip -Force
Write-Host "Built $zip"
