# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## V0.3 Playtest

This is the first installable playtest build. It combines a real persistent worker pool with conservative main-thread optimizations, while deliberately avoiding invasive simulation threading that is likely to break large mod lists.

### Enabled by default

- **Text metric cache** — caches repeated `Text.CalcHeight` / `Text.CalcSize` results.
- **Visible Thing overlay cache** — while the camera is stationary, avoids rescanning every overlay-capable Thing every rendered frame; actual drawing remains on the main thread.
- **Worker runtime** — 1-8 persistent background workers, bounded queues, priority scheduling, atomic `ParallelFor`, and bounded main-thread callbacks.
- **Compatibility scan** — any foreign Harmony prefix/postfix/transpiler/finalizer on an optimized target suppresses that RimMT feature and restores the vanilla path.
- **Circuit breakers / fail-closed behavior** — a failing worker feature is isolated instead of repeatedly damaging the game loop.

### Experimental and OFF by default

- **Short-lived unreachable-result cache** — caches only `false` reachability results for cell targets for 5-60 game ticks. It can briefly delay recognition of a newly opened route, so it is opt-in.

### Intentionally NOT parallelized

- `Pawn.Tick`
- `Thing.Tick`
- `ReservationManager`
- job assignment
- mutable map state
- faction ticks

These systems remain on the vanilla main thread until a specific implementation can preserve behavior under heavy Harmony/mod interaction.

## Large mod-list policy

RimMT is whitelist-only. If another mod patches a target RimMT wants to optimize, RimMT disables its own feature rather than attempting to win patch order. If another RimThreaded implementation is detected, gameplay optimizations are disabled entirely while diagnostics remain available.

RimMT does not write required state into saves. Removing it should leave the save usable.

## Settings

Open **Options → Mod settings → RimMT**.

You can toggle the two safe caches, opt into the reachability miss cache, print a compatibility report, and run a deterministic worker-thread self-test. The self-test uses the actual RimMT worker scheduler and returns its completion callback to the main thread.

## Install

Install Harmony, then extract the release so the game sees:

```
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references, so CI does not require redistributing RimWorld assemblies. GitHub Actions produces `RimMT_1.5_Playtest.zip`.

## Testing requested

For a large mod list, first launch an existing colony with the experimental reachability cache left OFF. Check the RimMT compatibility report, run the worker self-test once, then play normally and watch for new red errors, UI overlay anomalies, or TPS regressions. If a feature conflicts with another Harmony patch, it should report itself as OFF and use vanilla behavior.
