$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../RimMT/Build/InitializeRimMTToolchain.ps1')
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$proj=Join-Path $root 'LargePawnsPathfindingGuard/Source/LargePawnsPathfindingGuard/LargePawnsPathfindingGuard.csproj'

dotnet build $proj --configuration Release --no-restore
if($LASTEXITCODE -ne 0){ throw 'LargePawns Pathfinding Guard build failed' }

$dll=Join-Path $root 'LargePawnsPathfindingGuard/1.5/Assemblies/LargePawns.PathfindingGuard.dll'
if(-not (Test-Path $dll)){ throw 'LargePawns Pathfinding Guard DLL missing' }

$src=Get-Content (Join-Path $root 'LargePawnsPathfindingGuard/Source/LargePawnsPathfindingGuard/LargePawnsPathfindingGuard.cs') -Raw
foreach($required in @(
  'BodySize >= 2',
  'StormScore = 3',
  'RetryIntervalTicks = 600',
  'TryForceExit',
  'pathfinding.framework',
  'neku.largepawns'
)){
  if(-not $src.Contains($required)){ throw "LargePawns Guard invariant missing: $required" }
}

$stage=Join-Path $root 'build/stage-largepawns-pathguard'
if(Test-Path $stage){ Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$mod=Join-Path $stage 'LargePawnsPathfindingGuard'
New-Item -ItemType Directory -Force -Path $mod | Out-Null
Copy-Item (Join-Path $root 'LargePawnsPathfindingGuard/About') $mod -Recurse
Copy-Item (Join-Path $root 'LargePawnsPathfindingGuard/1.5') $mod -Recurse
Copy-Item (Join-Path $root 'LargePawnsPathfindingGuard/LoadFolders.xml') $mod
Get-ChildItem $mod -Recurse -Filter '*.pdb' | Remove-Item -Force

$zip=Join-Path $root 'build/LargePawns_PathfindingGuard_1.5_V1.0.zip'
if(Test-Path $zip){ Remove-Item $zip -Force }
Compress-Archive -Path $mod -DestinationPath $zip -Force
Write-Host "Built $zip"
