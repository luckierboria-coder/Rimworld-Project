# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving CPU-heavy work away from the main thread **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. Unknown or unsafe live-state mutation remains fail-closed with vanilla fallback.

## V0.4.4.1 Playtest

V0.4.4.1 is a Butter++ compatibility hotfix on top of the V0.4.4 path-cost work. Vanilla `PawnPath` is still authoritative; RimMT will not skip Vanilla pathfinding until the remaining dynamic path-cost components have been validated.

### Butter++ V0.4.4.1 fix

The first V0.4.4 playtest used the wrong reflection target for the Butter++ logical-tick state. Inspection of the supplied Butter++ 1.5 assembly shows two distinct states:

- `ButterPlusPlus.TickManagerPatch._midTickStarted` — manager-level state indicating that one logical `DoSingleTick` is still split/incomplete. **This is now RimMT's commit barrier.**
- `ButterPlusPlus.TickListPatch.MidTick` / `_midTick` — lower-level TickList split state. V0.4.4.1 keeps this only as diagnostic telemetry; it does not define the manager-level commit boundary.

Because V0.4.4 looked for `TickManagerPatch.MidTick/_midTick`, the probe could be unavailable and RimMT would conservatively defer dispatcher draining forever. V0.4.4.1 fixes that failure mode and also guarantees that a future unreadable Butter++ probe produces an explicit fail-closed compatibility report instead of silently stranding queued callbacks.

### Included V0.4.4 path work

- **Butter++ pressure sampling** — when Butter++ is active, RimMT samples CPU time spent in `TickManagerUpdate` slices rather than treating one split `DoSingleTick` as a single wall-clock sample.
- **AdaptiveTPS remains supported separately** — its known `TickManagerUpdate` pacing transpiler is still allowed beside RimMT. Butter++ itself declares AdaptiveTPS incompatible, so RimMT reports that external conflict instead of treating the three-way combination as safe.
- **Dubs Performance Analyzer warning with Butter++** — Butter++ declares DPA incompatible; RimMT reports the combination.
- **Expanded immutable path cost snapshot** — worker A* captures pawn cardinal/diagonal move ticks and terrain `extraDraftedPerceivedPathCost` / `extraNonDraftedPerceivedPathCost` in addition to `PathGrid` costs.
- **Vanilla-compatible float movement rounding** — RimWorld 1.5 uses float pawn move ticks and rounds accumulated known cost; RimMT mirrors that behavior without calling Unity APIs from workers.
- **128-validation milestone** — longer sessions emit another automatic parity summary after 128 paired path validations.

### Path cost model status

V0.4.4.1 worker costs include:

- `PathGrid.pathGrid`
- pawn `TicksPerMoveCardinal`
- pawn `TicksPerMoveDiagonal`
- drafted terrain extra path cost
- non-drafted terrain extra path cost

The following dynamic Vanilla costs are deliberately **not** treated as production-safe yet:

- avoid grid
- allowed-area penalty
- pawn collision penalty
- building / door cost
- blueprint cost
- lord walk-grid cost
- custom `PathFinderCostTuning` (still rejected by the worker eligibility gate)

This is intentional. RimMT prioritizes compatibility over prematurely replacing Vanilla `FindPath` with a route that is legal but behaviorally different.

### Enabled by default

- **Worker runtime** — 1-8 persistent background workers with bounded queues and priority scheduling.
- **Main-thread dispatcher** — worker callbacks are committed only on the main thread and, with Butter++, only when `TickManagerPatch._midTickStarted == false`.
- **Adaptive burst scheduling** — background work yields during severe pressure. Uses `DoSingleTick` samples normally and Butter++ `TickManagerUpdate` slice samples in Butter++ mode.
- **Text metric cache** — caches repeated `Text.CalcHeight` / `Text.CalcSize` results.
- **PathFinder / JobGiver diagnostics** — call counts and timing telemetry.
- **Path snapshot worker validation** — supported long `OnCell` paths capture immutable data on the main thread and run independent A* work on RimMT workers.

### Experimental and OFF by default

- **Short-lived unreachable-result cache** — caches only recent `false` reachability results for cell targets and invalidates them when path topology changes.

### Intentionally NOT parallelized yet

- `Pawn.Tick`
- `Thing.Tick`
- `ReservationManager`
- live `Pawn_JobTracker` mutation
- mutable map collections
- faction ticks

Worker code receives immutable snapshots/primitives only. Main-thread state remains authoritative and every module has vanilla fallback behavior.

## Compatibility policy

RimMT is whitelist-only and fail-closed. Unknown Harmony conflicts suppress only the affected RimMT feature.

Supported pacing configurations for V0.4.4.1:

- RimMT alone
- RimMT + AdaptiveTPS
- RimMT + Butter++

Not claimed safe:

- Butter++ + AdaptiveTPS — Butter++ declares `Blue.adaptiveTPS` incompatible.
- Butter++ + Dubs Performance Analyzer — Butter++ declares both DPA package IDs incompatible.

If another RimThreaded implementation is detected, gameplay optimizations are disabled entirely while diagnostics remain available.

RimMT does not write required state into saves. Removing it should leave the save usable.

## Testing V0.4.4.1

For the **Butter++ compatibility test**, enable Butter++ and RimMT, but disable AdaptiveTPS and Dubs Performance Analyzer so Butter++ is not being tested in combinations it explicitly declares incompatible.

After loading an existing colony and playing normally, open **Options -> Mod settings -> RimMT** and click **Log current runtime compatibility / performance report**. Useful lines include:

```text
[RimMT] Compatibility / performance report #... [startup|runtime]
Runtime compatibility: Butter++=True (LogicalTickProbe=True, source=ButterPlusPlus.TickManagerPatch._midTickStarted, ...)
Butter++ dispatcher barrier: logicalTickDrainDeferrals=..., probeFailureDrainDeferrals=..., managerProbeReadable=True, managerInProgress=...
Load pressure: ..., sampleSource=Butter++ TickManagerUpdate slice, butterFrameSamples=...
runtime.dispatcher: ACTIVE
Dispatcher: queued=..., enqueued=..., drained=..., butterLogicalTickDeferred=..., butterProbeFailureDeferred=..., drainCalls=...
Path snapshot worker: scheduled=..., completed=..., exactGeometry=..., workerFailures=...
Path cost model V0.4.4: ...
Path parity: foundParity=..., foundMismatch=..., workerLegal=..., workerIllegal=...
Path cost parity: comparable=..., sameCost=..., workerCheaper=..., workerCostlier=..., within1pct=..., within5pct=...
Path geometry parity: avgAbsNodeDelta=..., avgSharedPrefixFromStart=...
Path snapshot ingress: observed=..., pawnOverload=..., traverseParmsOverload=...
```

Healthy Butter++ behavior should show:

- `LogicalTickProbe=True`
- source `ButterPlusPlus.TickManagerPatch._midTickStarted`
- `managerProbeReadable=True`
- `runtime.dispatcher: ACTIVE`
- `drainCalls` rising over time
- `butterFrameSamples` rising over time
- logical-tick deferrals may rise during split ticks
- `probeFailureDrainDeferrals=0` and `butterProbeFailureDeferred=0`

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT. When Butter++ is used, place RimMT before Butter++ so Butter++ can remain low in the mod list as its author recommends.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
