# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving or eliminating CPU-heavy main-thread work **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. A production optimization only counts as successful when it actually lets RimWorld skip or materially shrink work that Vanilla would otherwise perform on the main thread. Unknown or unsafe live-state mutation remains fail-closed with Vanilla fallback.

## V0.4.6 Playtest

V0.4.6 is the first RimMT release with a **production Work search fast-path**. Earlier Path workers were deliberately shadow-only validators: useful for proving worker correctness, but they did not remove Vanilla A* from the main thread. V0.4.6 changes priority toward `JobGiver_Work`, which has been the much larger observed hotspot in the target heavily-modded colony.

### Production hauling Work acceleration

The first strict whitelist targets Vanilla non-prioritized hauling searches.

Vanilla `JobGiver_Work.TryIssueJobPackage` eventually calls `GenClosest.ClosestThingReachable` for hauling with `ListerHaulables.ThingsPotentiallyNeedingHauling()` as a custom global candidate set. That path can linearly walk a large haulable list while repeatedly applying reachability and WorkGiver validation.

V0.4.6 adds a fail-closed fast-path for exactly that call shape:

1. the main thread snapshots only the current haulable `Thing` references and integer positions;
2. a RimMT worker builds a spatial bucket index from that immutable snapshot;
3. the completed index is published only through the normal main-thread dispatcher boundary;
4. later eligible hauling searches walk nearby buckets first instead of linearly iterating the full haulable list;
5. Vanilla `Reachability.CanReach` and the original WorkGiver validator still run on the main thread for candidate confirmation;
6. when an indexed search finishes successfully, RimMT returns the same semantic nearest reachable/valid hauling target and **skips Vanilla's full haulable-list `GenClosest` pass**;
7. unsupported call shapes, small sets, stale/moved entries, failed index publication, unknown Harmony patches, or ambiguous state immediately fall back to Vanilla.

Membership changes are tracked through `ListerHaulables.Check`, `CheckAdd`, and `TryRemove`, allowing an already-published index to be maintained incrementally instead of rebuilt for every hauling change.

This is intentionally narrower than a generic off-thread WorkGiver implementation. Arbitrary mod `WorkGiver_Scanner` methods are not called from worker threads.

Runtime telemetry now contains a line similar to:

```text
Work search production V0.4.6: compatibilityReady=True,
  eligible=..., accelerated=..., acceleratedNoResult=..., fallback=...,
  buildsScheduled=..., buildsPublished=..., buildsDiscarded=...,
  incrementalAdds=..., incrementalRemoves=..., invalidations=...,
  candidatesVisited=..., candidatesAvoided=..., reachChecks=..., validatorChecks=...
```

`accelerated` is the important number: each increment means an eligible call returned through RimMT and the original full hauling `GenClosest` scan was not executed.

### Clean Pathfinding compatibility

V0.4.6 does **not** need Clean Pathfinding to be removed. Clean Pathfinding transpiles `PathFinder.FindPath`; the V0.4.6 production Work accelerator patches the hauling candidate search path in `GenClosest` instead and does not bypass Clean Pathfinding's PathFinder semantics.

RimMT still fails closed if another mod patches the exact `GenClosest.ClosestThingReachable` target used by the production Work fast-path. In that case only `parallel.jobScan` is suppressed and Vanilla behavior continues.

### JobGiver detail capture remains on-demand

Normal play keeps only the outer `JobGiver_Work.TryIssueJobPackage` hot-path timing. Detailed phase probes are **not patched at startup**.

From **Options -> Mod settings -> RimMT**, a temporary JobGiver detail capture can be started manually. RimMT then temporarily patches useful WorkGiver phases, captures up to 32 outer job-package calls, reports time by WorkGiverDef / concrete worker type / phase, and removes the temporary detours from a stable main-thread frame hook.

### Bounded Path shadow validation remains diagnostic-only

Path worker validation remains immutable-snapshot and **Vanilla PawnPath remains authoritative**. The V0.4.5.2 limits remain:

- at most **1** shadow path in flight;
- only **1 in 4** otherwise eligible TraverseParms requests sampled;
- only medium-distance paths up to **96 cells** Chebyshev distance;
- no new shadow work under High/Critical adaptive load;
- stop after **64 completed paired validations**.

The admission guard skips only `PathSnapshotWorker.TrySchedule`; it never skips Vanilla `PathFinder.FindPath`.

## Butter++ compatibility baseline

The validated Butter++ barrier remains unchanged:

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

Until these are captured safely, worker A* remains bounded shadow validation only.

## Enabled by default

- bounded worker runtime;
- main-thread dispatcher with Butter++ logical-tick barrier;
- adaptive burst scheduling;
- **V0.4.6 production hauling Work search acceleration**;
- text metric cache;
- outer PathFinder / JobGiver hot-path diagnostics;
- bounded Path snapshot parity validation;
- PathGrid invalidation deduplication.

Detailed per-WorkGiver profiling is **OFF during normal play** and only temporarily installed by the manual capture button.

### Experimental and OFF by default

- short-lived topology-aware unreachable-result cache.

### Intentionally NOT parallelized yet

- arbitrary/modded WorkGiver methods;
- `Pawn.Tick`;
- `Thing.Tick`;
- `ReservationManager`;
- live `Pawn_JobTracker` mutation;
- mutable map collections outside explicit snapshots/index maintenance;
- faction ticks.

## Compatibility policy

RimMT is whitelist-only and fail-closed. Unknown Harmony conflicts suppress only the affected RimMT feature. Vanilla remains authoritative for unsupported paths and all final mutable gameplay state.

Supported pacing configurations:

- RimMT alone;
- RimMT + AdaptiveTPS;
- RimMT + Butter++.

Clean Pathfinding can remain installed; the current production Work fast-path does not replace its PathFinder code.

Not claimed safe:

- Butter++ + AdaptiveTPS;
- Butter++ + Dubs Performance Analyzer.

If another RimThreaded implementation is detected, gameplay optimizations including the production Work accelerator are disabled while diagnostics remain available. RimMT does not write required state into saves.

## Testing V0.4.6

For Butter++, enable RimMT + Butter++ and leave AdaptiveTPS / Dubs Performance Analyzer disabled.

On startup, look for:

```text
[RimMT] parallel.jobScan V0.4.6 production haul accelerator installed...
[RimMT] V0.4.6 playtest initialized...
```

After the compatibility scan, `parallel.jobScan` should be `ACTIVE` unless a foreign patch touches the exact `GenClosest.ClosestThingReachable` target.

Run the colony normally, especially during periods with many haulables / hauling pawns, then click **Log current runtime compatibility / performance report**. Useful lines include:

```text
Work search production V0.4.6: compatibilityReady=True, eligible=..., accelerated=..., fallback=...,
  buildsScheduled=..., buildsPublished=..., incrementalAdds=..., incrementalRemoves=...,
  candidatesVisited=..., candidatesAvoided=..., reachChecks=..., validatorChecks=...
JobGiver_Work.TryIssueJobPackage: calls=..., avgMs=..., p95Ms~=..., maxMs=...
Path shadow budget V0.4.5.2: ...
```

The key proof that RimMT is doing real production work is `accelerated > 0`: those calls skipped Vanilla's full hauling candidate scan rather than running a duplicate shadow computation.

## Install

Install Harmony, then extract the release so the game sees:

```text
RimWorld/Mods/RimMT/About/About.xml
RimWorld/Mods/RimMT/1.5/Assemblies/RimMT.dll
```

Load Harmony before RimMT. When Butter++ is used, place RimMT before Butter++ so Butter++ can remain low in the mod list.

## Build

The repository uses `Krafs.Rimworld.Ref 1.5.4063` and `Lib.Harmony` compile references. GitHub Actions produces `RimMT_1.5_Playtest.zip`.
