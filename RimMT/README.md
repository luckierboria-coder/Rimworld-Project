# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving CPU-heavy work away from the main thread **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. Unknown or unsafe live-state mutation remains fail-closed with vanilla fallback.

## V0.4.5.2 Playtest

V0.4.5.2 is a stability-first cleanup after V0.4.5/0.4.5.1 diagnostics showed that RimMT's own validation/profiling could become visible as microstutter on a heavily modded colony.

Two observations drive this release:

- the V0.4.5 detailed JobGiver profiler touched hundreds of high-frequency WorkGiver phase methods and therefore should not remain resident during normal play;
- shadow Path A* is still diagnostic-only, so millions of duplicate worker node expansions are pure validation cost until production offload can safely replace Vanilla work.

V0.4.5.2 therefore removes normal-play diagnostic overhead rather than expanding unsafe production scope.

### JobGiver detail capture is now on-demand

Normal play keeps only the existing outer `JobGiver_Work.TryIssueJobPackage` hot-path timing. The detailed phase probes are **not patched at startup**.

From **Options -> Mod settings -> RimMT**, a temporary JobGiver detail capture can be started manually. RimMT then:

- discovers loaded WorkGiver implementations;
- temporarily patches useful phases such as `ShouldSkip`, `NonScanJob`, `HasJobOnThing`, `HasJobOnCell`, `JobOnThing`, `JobOnCell`, and `GetPriority`;
- captures up to 32 outer `TryIssueJobPackage` calls;
- reports time by WorkGiverDef / concrete worker type / phase;
- automatically removes the temporary detours from a stable main-thread frame hook after the capture finishes.

This keeps hotspot attribution available without making hundreds of detail detours part of normal gameplay.

### Bounded Path shadow validation

Path worker validation remains immutable-snapshot and **Vanilla PawnPath remains authoritative**. V0.4.5.2 bounds the extra diagnostic CPU at admission time without modifying the validated A* inner loop:

- at most **1** shadow path may be in flight;
- only **1 in 4** otherwise eligible TraverseParms requests is sampled;
- validation only admits medium-distance paths up to **96 cells** Chebyshev distance;
- when RimMT's adaptive load pressure is High/Critical, no new shadow validation is admitted;
- after **64 completed paired validations**, subsequent PathFinder calls do not build snapshots or enqueue shadow A* work.

The admission guard skips only `PathSnapshotWorker.TrySchedule`; it never skips Vanilla `PathFinder.FindPath`.

Runtime telemetry now includes:

```text
Path shadow budget V0.4.5.2: quota=64, complete=..., sampleEvery=4, maxDistance=96,
  eligible=..., cadenceSkipped=..., distanceSkipped=..., pressureSkipped=...,
  concurrencySkipped=..., quotaSkipped=...
```

V0.4.5 PathGrid invalidation deduplication remains enabled, as does the finalize-generation stale recheck.

## Butter++ compatibility baseline

The validated V0.4.4.1 Butter++ barrier remains unchanged:

- `ButterPlusPlus.TickManagerPatch._midTickStarted` is the manager-level logical-tick commit boundary;
- `ButterPlusPlus.TickListPatch.MidTick` is diagnostic only;
- worker-to-main-thread callbacks drain only at a safe logical-tick boundary;
- Butter++ mode samples `TickManagerUpdate` slices rather than split `DoSingleTick` wall time.

AdaptiveTPS remains supported separately. Butter++ itself declares AdaptiveTPS and Dubs Performance Analyzer incompatible, so RimMT does not claim those combinations are safe.

## Path worker status

The current worker cost model includes:

- `PathGrid.pathGrid`;
- pawn `TicksPerMoveCardinal` / `TicksPerMoveDiagonal` as float;
- Vanilla-compatible rounding;
- drafted / non-drafted terrain extra path cost.

Dynamic Vanilla costs are still not production-authoritative:

- avoid grid;
- allowed-area penalty;
- pawn collision penalty;
- building / door cost;
- blueprint cost;
- lord walk-grid cost;
- custom `PathFinderCostTuning`.

Until these are captured safely, worker A* remains shadow validation only.

## Enabled by default

- bounded worker runtime;
- main-thread dispatcher with Butter++ logical-tick barrier;
- adaptive burst scheduling;
- text metric cache;
- outer PathFinder / JobGiver hot-path diagnostics;
- bounded Path snapshot parity validation;
- PathGrid invalidation deduplication.

Detailed per-WorkGiver profiling is now **OFF during normal play** and only temporarily installed by the manual capture button.

### Experimental and OFF by default

- short-lived topology-aware unreachable-result cache.

### Intentionally NOT parallelized yet

- `Pawn.Tick`;
- `Thing.Tick`;
- `ReservationManager`;
- live `Pawn_JobTracker` mutation;
- mutable map collections;
- faction ticks.

## Compatibility policy

RimMT is whitelist-only and fail-closed. Unknown Harmony conflicts suppress only the affected RimMT feature. Vanilla remains authoritative for gameplay state.

Supported pacing configurations:

- RimMT alone;
- RimMT + AdaptiveTPS;
- RimMT + Butter++.

Not claimed safe:

- Butter++ + AdaptiveTPS;
- Butter++ + Dubs Performance Analyzer.

If another RimThreaded implementation is detected, gameplay optimizations are disabled while diagnostics remain available. RimMT does not write required state into saves.

## Testing V0.4.5.2

For Butter++, enable RimMT + Butter++ and leave AdaptiveTPS / Dubs Performance Analyzer disabled.

First judge normal gameplay feel. During ordinary play, `diagnostics.jobGiverDetail` should be OFF and the log should state that no per-WorkGiver detail detours are resident.

After a normal session, click **Log current runtime compatibility / performance report**. Useful lines include:

```text
Runtime compatibility: Butter++=True (...)
Dispatcher: queued=..., enqueued=..., drained=..., failures=..., drainCalls=...
PathGrid invalidation V0.4.5: cell=..., bulk=..., skippedNestedWrapper=..., skippedBulkCells=...
Path shadow budget V0.4.5.2: quota=64, complete=..., sampleEvery=4, maxDistance=96, ...
Path snapshot worker: scheduled=..., completed=..., snapshots=..., nodesExpanded=..., workerFailures=..., stale=...
Path parity: foundParity=..., foundMismatch=..., workerLegal=..., workerIllegal=...
JobGiver_Work.TryIssueJobPackage: calls=..., avgMs=..., p95Ms~=..., maxMs=...
JobGiver detail V0.4.5.2: active=False, ...
```

Only when a JobGiver spike needs attribution, start the temporary detail capture from Mod Settings. It automatically removes its temporary WorkGiver patches after 32 outer job-package calls.

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT. When Butter++ is used, place RimMT before Butter++ so Butter++ can remain low in the mod list.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
