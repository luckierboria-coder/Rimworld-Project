# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT's first performance objective is **reducing visible stutter and frame-time spikes**. Raising average TPS is secondary. A change that improves average throughput but introduces new main-thread variance, waits, GC pressure, or occasional multi-millisecond stalls is considered a regression for the target playtest environment.

The preferred architecture remains immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit, but only when that pipeline actually removes or shrinks expensive foreground work. RimMT does not pursue worker utilization for its own sake. Unknown or unsafe live-state mutation remains fail-closed with Vanilla fallback.

## V0.4.12 Playtest — Stutter First

V0.4.11 proved that the GenClosest candidate-ordering path was thread-safe, but the target colony reported more noticeable stutter. Runtime telemetry also showed an approximately 15 ms assist outlier and no clear improvement in average `JobGiver_Work.TryIssueJobPackage` cost, even though the observed maximum fell from roughly 964 ms to roughly 790 ms.

V0.4.12 therefore deliberately removes foreground variance before attempting any broader search-space reduction:

- Low-pressure GenClosest calls remain completely Vanilla.
- Normal pressure only considers very large `IList<Thing>` candidate sets (512+).
- High pressure threshold is 256 candidates; Critical is 192.
- Unknown `IEnumerable<Thing>` and non-list collection shapes are **not materialized** by RimMT.
- The V0.4.11 foreground worker assist and all `SpinWait` / micro-wait behavior are removed.
- Thread-local integer scratch buffers replace per-call X/Z and worker-output arrays.
- Candidate membership remains unchanged.
- The assist has a 0.75 ms hard foreground budget. If the budget is exceeded, the reorder is abandoned before commit and Vanilla sees the original input.
- Any assist taking at least 1.0 ms trips a 2-second cooldown so RimMT cannot repeatedly become its own stutter source.
- Reentrant calls are bypassed.

The runtime report exposes `budgetAbort`, `slowTrip`, `cooldownBypass`, `lowPressureBypass`, `nonListBypass`, `thresholdBypass(N/H/C)`, `avgAssistUs`, and `maxAssistUs` so the next playtest can judge **frame-time smoothness first**.

## V0.4.8 diagnostic foundation retained

The bounded JobGiver capture remains available. It instruments useful WorkGiver callbacks plus selected infrastructure while the capture is active:

- `GenClosest.ClosestThing*` search calls;
- `Reachability.CanReach`;
- `RegionTraverser.BreadthFirstTraverse` when available;
- concrete `WorkGiver_Scanner` `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` getters.

All of these detours are diagnostic-only and are automatically unpatched after the bounded 32-package capture completes. Slow packages keep bounded inclusive traces so hotspot work can be identified without permanently instrumenting every WorkGiver.

## Production hauling Work acceleration retained

V0.4.6/V0.4.7 hauling fast paths remain present and fail-closed. The main thread snapshots haulable references/positions; workers may build immutable spatial indices; Vanilla reachability, validators, reservations and final jobs stay main-thread authoritative. Unsupported, stale, patched or ambiguous calls fall back immediately.

### Clean Pathfinding compatibility

RimMT does **not** need Clean Pathfinding to be removed. Clean Pathfinding transpiles `PathFinder.FindPath`; current production Work accelerators act on hauling candidate search and do not bypass Clean Pathfinding's PathFinder semantics.

### Bounded Path shadow validation remains diagnostic-only

Path worker validation remains immutable-snapshot and **Vanilla PawnPath remains authoritative**. Current limits remain conservative: at most one shadow path in flight, sampled admission, bounded distance, no new shadow work under High/Critical load, and Vanilla fallback for unsupported or stale requests.

## Butter++ compatibility baseline

The validated Butter++ barrier remains unchanged:

- `ButterPlusPlus.TickManagerPatch._midTickStarted` is the manager-level logical-tick commit boundary;
- `ButterPlusPlus.TickListPatch.MidTick` is diagnostic only;
- worker-to-main-thread callbacks drain only at a safe logical-tick boundary;
- Butter++ mode samples `TickManagerUpdate` slices rather than split `DoSingleTick` wall time.

AdaptiveTPS remains supported separately. Butter++ itself declares AdaptiveTPS and Dubs Performance Analyzer incompatible, so RimMT does not claim those combinations are safe.
