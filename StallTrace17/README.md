# StallTrace17 — Waylith / RimWorld 1.7.4984

**Status:** Source implementation and automated build, NOT an in-game-validated replacement. The original independently developed StallTrace16 source has not been located in the user's linked repository or file index. Consequently this is an independent 1.7 diagnostic reconstruction, not a verified line-by-line port. It does not depend on RimMT or reintroduce any RimMT runtime optimization.

## Why

Trace daytime pawn activity rather than reporting only average TPS. RimWorld 1.7 runs on Waylith/.NET 10 and Harmony 3. This uses the exact 1.7.4984-unstable reference assemblies.

## Hooks

- Root_Play.Update: inclusive CPU time per play update plus category totals observed inside that frame
- TickManager.DoSingleTick: inclusive game-tick CPU time
- Pawn.TickInterval: Pawn identity on slow spans, with low-cost context for nested path calls
- Pawn_JobTracker.TryFindAndStartJob: job-search time and owner Pawn on slow spans
- JobGiver_Work.TryIssueJobPackage: work search time and pawn
- JobDriver.DriverTick: task driver time and owner pawn
- PathFinder.FindPath: every recognized method overload, with Pawn context when called inside Pawn.TickInterval
- Pawn_JobTracker.StartJob: counts jobs started by each pawn in each tick; flags 10, 30, 50, ... (the observed repeated Ingest loop)

Every hook fails closed individually if the game method signature or Harmony patch validation differs. No Prefix skips original methods, no Postfix overrides results.

## Use

1. Install Harmony 3 for RimWorld 1.7.
2. Obtain the compiled ZIP from the GitHub Actions run and install the mod folder; do not install C# source files without the compiled DLL.
3. Start the game, then open Mod Settings > StallTrace 1.7. Leave capture enabled.
4. Run 2–5 minutes at night and 2–5 minutes during active daytime under the **same speed**.
5. Press **Export report now** after each sample (or leave auto-export on every 120 seconds).
6. Reports go to %LOCALAPPDATA%\RimWorld-StallTrace17 as plain text. Share both night/day reports and Player.log.

**Important:** totals are inclusive/nested, not additive. "cycleHour" is tick-of-day relative to world tick, **not true colony-local solar time**. Screenshots or observations of in-game day/night should be included when comparing samples.

## Safety/performance

- Stopwatch counters have no locking or formatting on the typical successful hot path.
- Captures at most the last 512 slow events.
- Report file I/O is deferred to a background worker for auto export, and no game/Unity APIs are touched on that worker.
- Manual exports are user-triggered and can themselves create a short stall. Prefer exporting while paused.
- A StartJob loop of 10 jobs in a tick generates a *single* warning at count 10; additional detections are retained in the event ring.
- Harmony 3 may reject some injections or overloads, which the startup message and report show. Always review patch success.
- No attempt is made to take live stack samples from a suspended game thread; reports attribute to known wrapped methods.

## Build

.NET 10 SDK: dotnet build StallTrace17/Source/StallTrace17/StallTrace17.csproj -c Release

GitHub Actions workflow packages the compiled DLL without redistributing game binaries or 0Harmony.dll. No local build environment required.

## Differences from 1.6

Targets net10.0 and 1.7.4984-unstable, uses Harmony 3 rc.3 and Waylith-compatible reference stubs. Legacy 1.6 Unity assemblies and net472 are not used.