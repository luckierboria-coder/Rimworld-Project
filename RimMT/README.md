# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## V0.4.3.1 Playtest

V0.4.3.1 is a compatibility/diagnostic follow-up to the first worker-side path A* build. Vanilla `PawnPath` is still authoritative; the worker path remains shadow validation only.

### New in V0.4.3.1

- **AdaptiveTPS coexistence** — RimMT now explicitly allows the known `blue.adaptivetps` `TickManager.TickManagerUpdate` transpiler to coexist with RimMT's dispatcher postfix. The exception is intentionally narrow; unknown patch shapes remain fail-closed.
- **Dispatcher telemetry** — runtime reports show queued/enqueued/drained callbacks, failures, drain calls and queue high-water.
- **Runtime vs startup reports** — the automatic first-frame report is marked `[startup]`; the settings button now emits a fresh `[runtime]` report with main-thread frame count.
- **PathFinder ingress diagnostics** — Pawn and `TraverseParms` overloads are counted separately, and eligibility rejection reasons are reported individually.
- **High-priority path observation** — RimMT's PathFinder diagnostic prefix runs at Harmony `Priority.First` so later foreign prefixes cannot hide requests by short-circuiting vanilla pathing.
- **Harmony-chain logging** — both `PathFinder.FindPath` overloads print their active prefix/postfix/transpiler/finalizer owners and priorities at startup.
- **Worker failure cleanup** — a worker exception removes the tracked request and decrements in-flight state instead of leaking the request.

### Enabled by default

- **Worker runtime** — 1-8 persistent background workers with bounded queues and priority scheduling.
- **Main-thread dispatcher** — worker callbacks are committed only on the main thread.
- **Adaptive burst scheduling** — background work yields during severe tick pressure.
- **Text metric cache** — caches repeated `Text.CalcHeight` / `Text.CalcSize` results.
- **PathFinder / JobGiver diagnostics** — call counts and timing telemetry.
- **Path snapshot worker validation** — supported long `OnCell` paths capture an immutable `PathGrid` snapshot on the main thread and run independent A* work on RimMT workers.

### Experimental and OFF by default

- **Short-lived unreachable-result cache** — caches only recent `false` reachability results for cell targets and invalidates them when path topology changes.

### Intentionally NOT parallelized

- `Pawn.Tick`
- `Thing.Tick`
- `ReservationManager`
- live `Pawn_JobTracker` mutation
- mutable map collections
- faction ticks

Worker code receives immutable snapshots/primitives only. Main-thread state remains authoritative and every module has vanilla fallback behavior.

## Large mod-list policy

RimMT is whitelist-only and fail-closed. Unknown Harmony conflicts suppress only the affected RimMT feature. The AdaptiveTPS exception applies only to its known `TickManagerUpdate` transpiler because RimMT's dispatcher is a frame-end postfix with a separate responsibility.

If another RimThreaded implementation is detected, gameplay optimizations are disabled entirely while diagnostics remain available.

RimMT does not write required state into saves. Removing it should leave the save usable.

## Testing V0.4.3.1

AdaptiveTPS may be enabled for this build.

After loading an existing colony and playing normally, open **Options → Mod settings → RimMT** and click **Log current runtime compatibility / performance report**. Useful lines include:

```text
Compatibility / performance report #[N] [runtime]
ProgramState: Playing, mainThreadFrames=...
runtime.dispatcher: ACTIVE
Dispatcher: queued=..., enqueued=..., drained=...
Path snapshot ingress: observed=..., pawnOverload=..., traverseParmsOverload=...
Path snapshot rejects: shortDistance=..., targetThing=..., endMode=..., ...
Path snapshot worker: scheduled=..., completed=..., workerFailures=...
```

If the first eligible path is accepted, RimMT also logs a one-time marker saying that runtime path offload validation is functional.

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
