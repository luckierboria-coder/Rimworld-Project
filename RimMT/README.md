# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving CPU-heavy work away from the main thread **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. Unknown or unsafe live-state mutation remains fail-closed with vanilla fallback.

## V0.4.4 Playtest

V0.4.4 moves the validated worker architecture closer to production use while adding explicit Butter++ coexistence. Vanilla `PawnPath` is still authoritative in this build; RimMT will not skip vanilla pathfinding until remaining dynamic path-cost components have been validated.

### New in V0.4.4

- **Butter++ coexistence layer** — detects `olli.butterplusplus` and its split-tick `ButterPlusPlus.TickManagerPatch` runtime.
- **Logical-tick dispatcher barrier** — worker-to-main-thread callbacks are not drained while Butter++ reports `MidTick`. This prevents future worker results from committing in the middle of a logical game tick that Butter++ has split across rendered frames.
- **Butter++ pressure sampling** — RimMT does not use `DoSingleTick` wall time when Butter++ is active because Butter++ can keep one logical tick open across multiple frames. Instead it samples CPU time spent in each `TickManagerUpdate` slice.
- **AdaptiveTPS remains supported separately** — its known `TickManagerUpdate` pacing transpiler is still allowed beside RimMT. Butter++ itself declares AdaptiveTPS incompatible, so RimMT reports that external conflict instead of pretending the three-way combination is safe.
- **Dubs Performance Analyzer warning with Butter++** — Butter++ declares DPA incompatible; RimMT reports the combination so Butter++ tests can be run without misleading profiler-side conflicts.
- **Expanded immutable path cost snapshot** — worker A* now captures pawn cardinal/diagonal move ticks and terrain `extraDraftedPerceivedPathCost` / `extraNonDraftedPerceivedPathCost`, in addition to `PathGrid` costs.
- **128-validation milestone** — longer sessions now emit another automatic parity summary after 128 paired path validations.

### Path cost model status

V0.4.4 worker costs include:

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
- **Main-thread dispatcher** — worker callbacks are committed only on the main thread and, with Butter++, only at a logical-tick boundary.
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

Supported pacing configurations for V0.4.4:

- RimMT alone
- RimMT + AdaptiveTPS
- RimMT + Butter++

Not claimed safe:

- Butter++ + AdaptiveTPS — Butter++ declares `Blue.adaptiveTPS` incompatible.
- Butter++ + Dubs Performance Analyzer — Butter++ declares both DPA package IDs incompatible.

If another RimThreaded implementation is detected, gameplay optimizations are disabled entirely while diagnostics remain available.

RimMT does not write required state into saves. Removing it should leave the save usable.

## Testing V0.4.4

For the **Butter++ compatibility test**, enable Butter++ and RimMT, but disable AdaptiveTPS and Dubs Performance Analyzer so Butter++ is not being tested in combinations it explicitly declares incompatible.

For the **AdaptiveTPS compatibility test**, enable AdaptiveTPS and RimMT with Butter++ disabled.

After loading an existing colony and playing normally, open **Options -> Mod settings -> RimMT** and click **Log current runtime compatibility / performance report**. Useful lines include:

```text
Runtime compatibility: Butter++=..., AdaptiveTPS=..., DubsPerformanceAnalyzer=...
Butter++ dispatcher barrier: midTickDrainDeferrals=..., probe=...
Load pressure: ..., sampleSource=..., butterFrameSamples=...
runtime.dispatcher: ACTIVE
Dispatcher: queued=..., enqueued=..., drained=...
Path snapshot worker: scheduled=..., completed=..., exactGeometry=..., workerFailures=...
Path cost model V0.4.4: ...
Path parity: foundParity=..., foundMismatch=..., workerLegal=..., workerIllegal=...
Path cost parity: comparable=..., sameCost=..., workerCheaper=..., workerCostlier=..., within1pct=..., within5pct=...
Path geometry parity: avgAbsNodeDelta=..., avgSharedPrefixFromStart=...
Path snapshot ingress: observed=..., pawnOverload=..., traverseParmsOverload=...
Path snapshot rejects: shortDistance=..., targetThing=..., endMode=..., ...
```

For Butter++ specifically, a healthy report should show `MidTickProbe=True`, a rising `midTickDrainDeferrals` count during split ticks, and `sampleSource=Butter++ TickManagerUpdate slice`.

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT. When Butter++ is used, place RimMT before Butter++ so Butter++ can remain low in the mod list as its author recommends.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
