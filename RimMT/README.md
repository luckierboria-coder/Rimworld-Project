# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving CPU-heavy work away from the main thread **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. Unknown or unsafe live-state mutation remains fail-closed with vanilla fallback.

## V0.4.5 Playtest

V0.4.5 starts the next optimization phase after V0.4.4.1 validated sustained RimMT + Butter++ operation. The supplied long-session logs showed two clear targets:

- Path worker correctness is stable (found/legal/endpoint parity), but immutable snapshots were being rebuilt far too often.
- `JobGiver_Work.TryIssueJobPackage` is a much larger main-thread spike source than the currently eligible PathFinder subset, including individual calls hundreds of milliseconds long.

V0.4.5 therefore focuses on **PathGrid invalidation quality** and **per-WorkGiver hotspot attribution** before any JobGiver result is moved off-thread.

### V0.4.5 PathGrid invalidation fix

RimWorld 1.5 has nested `PathGrid.RecalculatePerceivedPathCostAt` overloads, and `RecalculateAllPerceivedPathCosts()` loops through cells and calls those recalculation methods. Earlier RimMT builds attached the same topology-generation postfix to all of them, so one logical update could be counted multiple times and a full-grid rebuild could create a generation storm.

V0.4.5 keeps the fail-closed overload coverage but deduplicates the callbacks:

- the one-argument wrapper no longer generates a second invalidation after its core overload already did;
- nested cell callbacks during `RecalculateAllPerceivedPathCosts()` are suppressed;
- one full-grid recalculation produces one topology invalidation at its boundary;
- normal core single-cell recalculations still invalidate immediately.

Runtime reports now include:

```text
PathGrid invalidation V0.4.5: cell=..., bulk=..., skippedNestedWrapper=..., skippedBulkCells=...
```

This should reduce unnecessary path-snapshot rebuilds without weakening stale-result detection. Vanilla `PawnPath` is still authoritative.

### V0.4.5 JobGiver detail profiler

Long-session telemetry showed `JobGiver_Work.TryIssueJobPackage` dominating observed AI search time and producing very large single-call spikes. V0.4.5 does **not** send the whole JobGiver to worker threads; that would be unsafe with live Pawn/Thing/reservation state and a large mod list.

Instead, V0.4.5 dynamically instruments concrete loaded `WorkGiver` implementations and attributes time by:

- `WorkGiverDef.defName`
- concrete worker type
- phase/method

The detail profiler covers useful scanner/job phases such as:

- `ShouldSkip`
- `NonScanJob`
- `HasJobOnThing`
- `HasJobOnCell`
- `JobOnThing`
- `JobOnCell`
- `GetPriority(Pawn, TargetInfo)`

Runtime reports print the top 12 entries by cumulative sampled time, with call count, total/average/max time and counts over 16/64/128 ms:

```text
JobGiver detail V0.4.5: patchedMethods=..., patchFailures=..., samples=..., tracked=...
  #1 WorkGiverDef / Namespace.WorkerType [HasJobOnThing]: calls=..., totalMs=..., avgMs=..., maxMs=..., >=16ms=..., >=64ms=..., >=128ms=...
```

This is diagnostic groundwork for a later whitelist-only candidate-snapshot worker: main thread captures immutable candidates, workers pre-filter/score, then the main thread revalidates and creates/reserves the actual Job.

## Butter++ compatibility baseline

V0.4.5 carries forward the V0.4.4.1 Butter++ fix:

- `ButterPlusPlus.TickManagerPatch._midTickStarted` is the manager-level logical-tick commit barrier.
- `ButterPlusPlus.TickListPatch.MidTick` is diagnostic only.
- Worker-to-main-thread callbacks drain only at a safe logical-tick boundary.
- Butter++ mode samples `TickManagerUpdate` slices rather than treating a split `DoSingleTick` as one wall-clock sample.

AdaptiveTPS remains supported separately. Butter++ itself declares AdaptiveTPS and Dubs Performance Analyzer incompatible, so RimMT does not claim those Butter++ combinations are safe.

## Path worker status

The V0.4.4 path cost model is retained. Worker costs currently include:

- `PathGrid.pathGrid`
- pawn `TicksPerMoveCardinal`
- pawn `TicksPerMoveDiagonal`
- drafted terrain extra path cost
- non-drafted terrain extra path cost
- Vanilla-compatible float movement rounding

The following dynamic Vanilla costs are still deliberately **not production-authoritative** in V0.4.5:

- avoid grid
- allowed-area penalty
- pawn collision penalty
- building / door cost
- blueprint cost
- lord walk-grid cost
- custom `PathFinderCostTuning` (still rejected by the worker eligibility gate)

The long-session validation pattern — mismatches consistently worker-cheaper rather than worker-costlier — is consistent with missing positive dynamic costs. V0.4.5 first removes snapshot churn and improves diagnostics; the dynamic-cost snapshot will be expanded only while keeping immutable worker inputs and Vanilla fallback.

### Enabled by default

- **Worker runtime** — 1-8 persistent background workers with bounded queues and priority scheduling.
- **Main-thread dispatcher** — worker callbacks commit only on the main thread and respect the Butter++ manager-level logical-tick barrier.
- **Adaptive burst scheduling** — background work reacts to recent main-thread pressure.
- **Text metric cache** — caches repeated `Text.CalcHeight` / `Text.CalcSize` results.
- **PathFinder diagnostics** — call counts, timing and paired path parity telemetry.
- **JobGiver diagnostics** — outer `TryIssueJobPackage` timing plus V0.4.5 per-WorkGiver detail attribution.
- **Path snapshot worker validation** — supported long `OnCell` paths run independent A* on immutable snapshots; Vanilla remains authoritative.
- **PathGrid invalidation deduplication** — prevents nested/full-grid generation storms.

### Experimental and OFF by default

- **Short-lived unreachable-result cache** — caches only recent `false` reachability results and invalidates on topology changes.

### Intentionally NOT parallelized yet

- `Pawn.Tick`
- `Thing.Tick`
- `ReservationManager`
- live `Pawn_JobTracker` mutation
- mutable map collections
- faction ticks

## Compatibility policy

RimMT is whitelist-only and fail-closed. Unknown Harmony conflicts suppress only the affected RimMT feature.

Supported pacing configurations:

- RimMT alone
- RimMT + AdaptiveTPS
- RimMT + Butter++

Not claimed safe:

- Butter++ + AdaptiveTPS — Butter++ declares `Blue.adaptiveTPS` incompatible.
- Butter++ + Dubs Performance Analyzer — Butter++ declares both DPA package IDs incompatible.

If another RimThreaded implementation is detected, gameplay optimizations are disabled while diagnostics remain available. RimMT does not write required state into saves.

## Testing V0.4.5

For the Butter++ profile, enable RimMT + Butter++ and leave AdaptiveTPS / Dubs Performance Analyzer disabled.

Play a real colony under normal load, then open **Options -> Mod settings -> RimMT** and click **Log current runtime compatibility / performance report**. Useful lines include:

```text
[RimMT] Compatibility / performance report #... [runtime]
Runtime compatibility: Butter++=True (LogicalTickProbe=True, source=ButterPlusPlus.TickManagerPatch._midTickStarted, ...)
Dispatcher: queued=..., enqueued=..., drained=..., failures=..., drainCalls=...
PathGrid invalidation V0.4.5: cell=..., bulk=..., skippedNestedWrapper=..., skippedBulkCells=...
Path snapshot worker: scheduled=..., completed=..., snapshots=..., workerFailures=..., stale=...
Path parity: foundParity=..., foundMismatch=..., workerLegal=..., workerIllegal=...
Path cost parity: sameCost=..., workerCheaper=..., workerCostlier=..., within1pct=..., within5pct=...
JobGiver_Work.TryIssueJobPackage: calls=..., avgMs=..., p95Ms~=..., maxMs=...
JobGiver detail V0.4.5: patchedMethods=..., patchFailures=..., samples=..., tracked=...
  #1 ...
```

For PathGrid churn, compare `snapshots / scheduled` with V0.4.4.1 and inspect `skippedNestedWrapper` / `skippedBulkCells`. A healthy result should reduce unnecessary snapshot rebuilds while keeping `workerFailures=0`, `stale` low, and found/legal/endpoint parity intact.

For JobGiver, the top entries tell us which concrete scanners should be considered for the first whitelist-only worker prefilter. High `maxMs` and repeated `>=64ms` / `>=128ms` counts are especially important.

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT. When Butter++ is used, place RimMT before Butter++ so Butter++ can remain low in the mod list.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
