# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT's first performance objective is **reducing visible stutter and frame-time spikes**. Raising average TPS is secondary. A change that improves average throughput but introduces new main-thread variance, waits, GC pressure, or occasional multi-millisecond stalls is considered a regression for the target playtest environment.

The preferred architecture is now explicit: **main-thread snapshot -> worker precomputation across spare cores -> later main-thread consumption/validation -> Vanilla commit**. Worker work should happen ahead of demand, not as a foreground task that the main thread waits for. Unknown or unsafe live-state mutation remains fail-closed with Vanilla fallback.

## V0.4.13 Playtest — True Offload

V0.4.12 removed the worst foreground-assist variance, but the target colony still showed substantial JobGiver stutter while RimMT workers were frequently idle. V0.4.13 therefore replaces request-time GenClosest reordering with reusable background precomputation:

- Repeated custom global searches backed by `IList<Thing>`, `IList<Pawn>` or `IList<Building>` can be indexed.
- On a cache miss, the main thread only snapshots Thing references and integer X/Z positions.
- A **normal-priority RimMT worker** builds the immutable bucket index. These builds are not suppressed merely because the main thread is under load.
- The main thread never waits, spins, or blocks for an index. The triggering call falls back to Vanilla.
- Later calls validate the published snapshot exactly: same object references in the same source positions and unchanged map positions.
- Any membership or position change invalidates the cache, falls back to Vanilla for that call, and schedules a rebuild.
- Accelerated calls traverse the worker-built spatial index and can avoid most of Vanilla's full-list distance pass while retaining live `Reachability.CanReach` and the original WorkGiver validator.
- Equal-distance tie breaking preserves original list order.
- Exact `ListerHaulables` searches continue to use the dedicated V0.4.6/V0.4.7 accelerators.
- At most four generic index builds may be in flight at once.

The runtime report exposes cache hits/misses, builds scheduled/published/discarded, snapshot capture cost, snapshot validation cost, query cost, candidate visits/avoids, reachability checks and failures. The key test is whether JobGiver P95/max and visible stutter fall while worker utilization rises.

## V0.4.8 diagnostic foundation retained

The bounded JobGiver capture remains available. It instruments useful WorkGiver callbacks plus selected infrastructure while the capture is active:

- `GenClosest.ClosestThing*` search calls;
- `Reachability.CanReach`;
- `RegionTraverser.BreadthFirstTraverse` when available;
- concrete `WorkGiver_Scanner` `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` getters.

All of these detours are diagnostic-only and are automatically unpatched after the bounded 32-package capture completes. Slow packages keep bounded inclusive traces so hotspot work can be identified without permanently instrumenting every WorkGiver.

## Production hauling Work acceleration retained

V0.4.6/V0.4.7 hauling fast paths remain present and fail-closed. The main thread snapshots haulable references/positions; workers build immutable spatial indices; Vanilla reachability, validators, reservations and final jobs stay main-thread authoritative. Unsupported, stale, patched or ambiguous calls fall back immediately.

### Clean Pathfinding compatibility

RimMT does **not** need Clean Pathfinding to be removed. Clean Pathfinding transpiles `PathFinder.FindPath`; current production Work accelerators act on candidate search and do not bypass Clean Pathfinding's PathFinder semantics.

### Bounded Path shadow validation remains diagnostic-only

Path worker validation remains immutable-snapshot and **Vanilla PawnPath remains authoritative**. Current limits remain conservative: at most one shadow path in flight, sampled admission, bounded distance, no new shadow work under High/Critical load, and Vanilla fallback for unsupported or stale requests.

## Butter++ compatibility baseline

The validated Butter++ barrier remains unchanged:

- `ButterPlusPlus.TickManagerPatch._midTickStarted` is the manager-level logical-tick commit boundary;
- `ButterPlusPlus.TickListPatch.MidTick` is diagnostic only;
- worker-to-main-thread callbacks drain only at a safe logical-tick boundary;
- Butter++ mode samples `TickManagerUpdate` slices rather than split `DoSingleTick` wall time.

AdaptiveTPS remains supported separately. Butter++ itself declares AdaptiveTPS and Dubs Performance Analyzer incompatible, so RimMT does not claim those combinations are safe.
