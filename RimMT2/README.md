# RimMT 2.0 — RimWorld 1.6

Independent `allen.rimmt2` multithreading and diagnostics mod. **The editable M1.1.5 source is committed** in [Source/RimMT2](./Source/RimMT2/), separate from legacy [RimMT 1.5](../RimMT/).

## Local compilation

**See [BUILDING.md](./BUILDING.md) for setup, both build methods, exact DLL paths and installation steps.** The quickest route (requires a .NET SDK, not just the .NET runtime) from repository root:

```powershell
dotnet build RimMT2/CI/RimMT2.CI.csproj -c Release
```

Produces `RimMT2/CI/bin/Release/net472/RimMT2.dll`. This portable reference-only project uses `Krafs.Rimworld.Ref` **1.6.4871** and Harmony from NuGet. For compilation against your *actual game binaries* use `Build/Build.ps1` and configure `RIMWORLD_MANAGED_DIR` + `RIMMT_HARMONY_DLL`; the native project targets `net48`.

The code here is **M1.1.5**, a diagnostic milestone. Its rolling unknown-caller census samples every 32nd unknown call, bounded to 16 captures per 64 sampled root searches, keeping the last 128 callsites. Work search and actual game job execution are still serial; no TPS improvement has been demonstrated. Never treat successful compilation as runtime validation.

## Repository paths

- `Source/RimMT2/`: all editable C# code, including `Diagnostics/JobSearchCensus.cs`.
- `Source/RimMT2/RimMT2.csproj`: native `net48` project referencing installed RimWorld/Harmony DLLs.
- `CI/RimMT2.CI.csproj`: portable `net472` reference-only project, built from exactly the same `.cs` files.
- `Build/Build.ps1`: optional native local build helper, **does not install tools**.
- `About/About.xml` and `LoadFolders.xml`: RimWorld mod metadata (`2.0.0-m1.1.5`).
- `source-archive/`: older encoded import snapshot kept for provenance, **not** the active source tree.

## Engineering contract

Use native Unity `ManagedJob`/`JobHandle` with immutable main-thread snapshots and validated main-thread result commits. Never call live Verse/Pawn/Map, Unity, Missile Girl mod callbacks or mutable reachability searches from worker threads. Profiler patching is opt-in, and idle worker count is 0 when no module is active. Do not reintroduce whole-job-search parallelism from the RimMT 1.5 project.

See [AGENTS.md](./AGENTS.md) for safety conditions, lifecycle and acceptance checks.

## Verification status

- M1.1.4: real RimWorld 1.6.4871 log analyzed; sampling saturated at 128 captures and `submitted=0`.
- M1.1.5: sourced directly from uploaded ZIP, 14 original text entries SHA-1 Git-blob matched against committed source; only Mod `About.xml` was updated from 1.1.4 metadata to 1.1.5.
- GitHub Actions previously built M1.1.5 against the same source snapshot with zero compiler warnings or errors. On this direct-source import, CI has been changed to compile the checked-in files. **No new game-side M1.1.5 validation yet.**
