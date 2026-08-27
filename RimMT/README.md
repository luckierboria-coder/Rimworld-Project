# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT's first performance objective is **reducing visible stutter and frame-time spikes**. Stability/compatibility is second. Raising average TPS is third. A change that improves average throughput but introduces new main-thread variance, waits, GC pressure, or occasional multi-millisecond stalls is a regression.

Preferred architecture: **main-thread snapshot/event capture -> worker precomputation across spare cores -> later main-thread consumption/validation -> Vanilla commit**. Worker work should happen ahead of demand, never as a foreground job the main thread waits for.

## V0.4.14 Playtest — Persistent Map Search Fabric

V0.4.13 proved that true worker offload works in the target 1.5 environment: worker builds completed cleanly and all eight workers could be used. It also proved the per-IList position-snapshot design was the wrong cache layer. Position churn invalidated source caches and broad no-result queries still performed hundreds of live `Reachability.CanReach`/validator calls, producing a performance regression.

V0.4.14 keeps true offload but changes the search-data ownership model:

- Repeated custom global searches backed by `IList<Thing>` / `IList<Building>` cache only **membership and original list order** on the main thread.
- Candidate positions are owned by a **persistent per-map search fabric**.
- `ThingGrid.RegisterInCell` / `DeregisterInCell` events update tracked positions incrementally.
- Worker cores consume only immutable event payloads: Thing references plus primitive X/Z/source-order values. Workers do not dereference Verse/Map/Pawn state.
- Workers publish immutable source bucket snapshots; the main thread never waits for publication.
- Moving a tracked Thing no longer rebuilds an entire source membership snapshot.
- Source membership/order is still compared exactly on the main thread so Vanilla equal-distance tie behavior remains reproducible.
- Mobile Pawn-backed sources and unspawned/inventory sources remain fail-closed for this generic path.

### Stutter-first admission

The V0.4.13 runtime showed an average accelerated query cost of roughly 35 ms because most accelerated calls were broad no-result searches. V0.4.14 therefore adds a hard admission rule:

- The already-built fabric first estimates the number of structurally relevant live candidates.
- If the estimate exceeds **64**, RimMT performs **no** live Reachability/validator work and immediately lets Vanilla execute the call.
- Accepted calls are bounded to at most 64 live candidate checks.
- Any stale fabric observation, publication lag, unsupported source shape or ambiguity falls back to Vanilla.

This version is deliberately conservative. The goal is to preserve worker utilization while removing V0.4.13's self-inflicted main-thread stalls. Region/Reachability structural offload will only expand after this persistent-fabric baseline proves non-regressive.

Runtime diagnostics now report both the map fabric and its consumer: source registrations, grid updates, worker batches/events, publication cost, snapshot hits/misses, broad-query bypasses, live-check caps, query cost, candidates visited/avoided and failures.

## V0.4.8 diagnostic foundation retained

The bounded JobGiver capture remains available. It instruments useful WorkGiver callbacks plus selected infrastructure while the capture is active:

- `GenClosest.ClosestThing*` search calls;
- `Reachability.CanReach`;
- `RegionTraverser.BreadthFirstTraverse` when available;
- concrete `WorkGiver_Scanner` `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` getters.

All detours are diagnostic-only and automatically unpatched after the bounded capture completes.

## Production hauling acceleration retained

V0.4.6/V0.4.7 hauling fast paths remain fail-closed. Vanilla reachability, validators, reservations and final jobs stay main-thread authoritative.

### Clean Pathfinding compatibility

RimMT does **not** require Clean Pathfinding to be removed. Current Work-search accelerators do not bypass `PathFinder.FindPath` semantics.

### Path shadow validation remains diagnostic-only

Path worker validation uses immutable snapshots and **Vanilla PawnPath remains authoritative**. Unsupported or stale requests fall back immediately.

## Butter++ compatibility baseline

- `ButterPlusPlus.TickManagerPatch._midTickStarted` remains the logical-tick commit boundary.
- Worker-to-main-thread callbacks drain only at a safe logical-tick boundary.
- Butter++ mode samples `TickManagerUpdate` slices rather than split `DoSingleTick` wall time.
