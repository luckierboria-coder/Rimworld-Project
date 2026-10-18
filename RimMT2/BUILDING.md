# RimMT 2.0 M1.1.5 — complete source and local build

All editable C# source is under `Source/RimMT2/`. `About/` and `LoadFolders.xml` are the RimWorld mod metadata. This is the independent RimWorld 1.6 / packageId `allen.rimmt2` project, **not** old `RimMT/` for 1.5.

## Option A — portable local build (recommended)

Prerequisite: **.NET SDK 8 or later** installed, rather than only the .NET runtimes. From the repository root, in PowerShell or cmd.exe:

```powershell
dotnet --list-sdks
dotnet build RimMT2/CI/RimMT2.CI.csproj -c Release
```

Output: `RimMT2/CI/bin/Release/net472/RimMT2.dll`. The portable project uses NuGet reference packages for RimWorld 1.6.4871, Harmony, and .NET Framework 4.7.2; it does not redistribute the game assemblies and does not require copying local game DLLs. It is the same source set and project structure used by CI.

## Option B — build against your actual RimWorld installation

Set both environment variables to existing file locations and run the native `net48` project:

```powershell
$env:RIMWORLD_MANAGED_DIR = 'D:\Rimworld\RimWorld.v1.6.4871\RimWorldWin64_Data\Managed'
$env:RIMMT_HARMONY_DLL = 'D:\Rimworld\RimWorld.v1.6.4871\Mods\1446523594\1.6\Assemblies\0Harmony.dll'
./RimMT2/Build/Build.ps1
```

Adjust Harmony's path to wherever `0Harmony.dll` is actually installed. Native output: `RimMT2/1.6/Assemblies/RimMT2.dll`. Both methods require a local SDK; `dotnet --list-runtimes` does not mean an SDK is installed.

## Install and verification

Copy the freshly compiled DLL to `<your RimMT2 mod folder>/1.6/Assemblies/RimMT2.dll`; copy `About/About.xml` and `LoadFolders.xml` from this repo to the corresponding root locations. Fully exit the game first. On restart, confirm the `[RimMT 2.0 M1.1.5]` bootstrap log. M1.1.5 only changes rolling diagnostics; it does not yet submit live search work to workers. Compilation does not prove game-side performance or compatibility.

The previous encoded archive under `source-archive/` is retained temporarily for provenance. Do not build from it: **build the checked-in `.cs` files directly**.
