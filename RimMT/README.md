# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving CPU-heavy work away from the main thread **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. Unknown or unsafe live-state mutation remains fail-closed with vanilla fallback.

## V0.4.3.2 Playtest

V0.4.3.2 is a path-parity validation build. Vanilla `PawnPath` remains authoritative; worker paths are still shadow-only.

### New in V0.4.3.2

- **Critical path reconstruction fix** — `WorkerResult` is a struct; the previous `BuildResultPath(result, ...)` call passed it by value, so reconstructed `NodeCount` / `PathHash` were written to a copy. V0.4.3.2 uses `ref` and retains the reconstructed worker path.
- **Found-state parity** — reports whether worker and vanilla agree on found/unfound.
- **Path legality checks** — validates adjacency, impassable cells and diagonal corner blocking against the immutable snapshot.
- **Endpoint checks** — validates start and destination representation for both worker and vanilla paths.
- **Snapshot-relative cost parity** — compares both paths using the same captured PathGrid costs and reports same-cost / worker-cheaper / worker-costlier counts plus 1% and 5% bands.
- **Node and geometry divergence** — reports node-count delta and shared prefix from the start instead of treating any different geometry as automatically wrong.
- **Bounded mismatch samples** — logs up to four parity samples with found/legal/endpoint/cost/node/prefix details.
- **Milestone reports** — automatic summaries at 8 and 32 paired validations.
- **AdaptiveTPS coexistence retained** — `blue.adaptivetps` is an optional load-after target and its known `TickManagerUpdate` transpiler remains explicitly allowed beside RimMT's dispatcher postfix.

### Enabled by default

- **Worker runtime** — 1-8 persistent background workers with bounded queues and priority scheduling.
- **Main-thread dispatcher** — worker callbacks are committed only on the main thread.
- **Adaptive burst scheduling** — background work yields during severe tick pressure.
- **Text metric cache** — caches repeated `Text.CalcHeight` / `Text.CalcSize` results.
- **PathFinder / JobGiver diagnostics** — call counts and timing telemetry.
- **Path snapshot worker validation** — supported long `OnCell` paths capture an immutable `PathGrid` snapshot on the main thread and run independent A* work on RimMT workers.

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

## Large mod-list policy

RimMT is whitelist-only and fail-closed. Unknown Harmony conflicts suppress only the affected RimMT feature. The AdaptiveTPS exception applies only to its known `TickManagerUpdate` transpiler because RimMT's dispatcher is a frame-end postfix with a separate responsibility.

If another RimThreaded implementation is detected, gameplay optimizations are disabled entirely while diagnostics remain available.

RimMT does not write required state into saves. Removing it should leave the save usable.

## Testing V0.4.3.2

AdaptiveTPS may be enabled.

After loading an existing colony and playing normally, open **Options -> Mod settings -> RimMT** and click **Log current runtime compatibility / performance report**. Useful lines include:

```text
Compatibility / performance report #[N] [runtime]
ProgramState: Playing, mainThreadFrames=...
runtime.dispatcher: ACTIVE
Dispatcher: queued=..., enqueued=..., drained=...
Path snapshot worker: scheduled=..., completed=..., exactGeometry=..., workerFailures=...
Path parity: foundParity=..., foundMismatch=..., workerLegal=..., workerIllegal=...
Path cost parity: comparable=..., sameCost=..., workerCheaper=..., workerCostlier=..., within1pct=..., within5pct=...
Path geometry parity: avgAbsNodeDelta=..., avgSharedPrefixFromStart=...
Path snapshot ingress: observed=..., pawnOverload=..., traverseParmsOverload=...
Path snapshot rejects: shortDistance=..., targetThing=..., endMode=..., ...
```

Exact geometry is now diagnostic only. A different route is not automatically wrong if found state, legality, endpoints and cost remain valid.

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT. AdaptiveTPS can remain enabled.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
