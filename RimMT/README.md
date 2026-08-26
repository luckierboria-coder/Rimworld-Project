# RimMT

Compatibility-first performance and multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT exists to raise real gameplay TPS by moving or eliminating CPU-heavy main-thread work **as far as compatibility allows**. The preferred pattern is immutable main-thread snapshot -> worker computation across spare cores -> main-thread validation/commit. A production optimization only counts as successful when it actually lets RimWorld skip or materially shrink work that Vanilla would otherwise perform on the main thread. Unknown or unsafe live-state mutation remains fail-closed with Vanilla fallback.

## V0.4.8 Playtest

V0.4.8 is a diagnosis-first JobGiver release. In the target heavily-modded colony, `JobGiver_Work.TryIssueJobPackage` remains the dominant observed hotspot (roughly 20-30 ms average with 128 ms-class p95 and occasional 600 ms-class spikes), while individual WorkGiver callback probes were mostly sub-millisecond. V0.4.8 therefore does **not** blindly move more live Verse state off-thread. Instead it expands the existing bounded 32-package capture so the next playtest can locate the real outer-search cost before production parallelism is widened.

### Slow JobPackage trace

The on-demand capture still temporarily instruments useful WorkGiver callbacks (`ShouldSkip`, `NonScanJob`, `HasJobOnThing`, `HasJobOnCell`, `JobOnThing`, `JobOnCell`, `GetPriority`), but V0.4.8 additionally measures selected infrastructure while an outer `TryIssueJobPackage` capture is active:

- `GenClosest.ClosestThing*` search calls;
- `Reachability.CanReach`;
- `RegionTraverser.BreadthFirstTraverse` when available;
- concrete `WorkGiver_Scanner` `PotentialWorkThingsGlobal` / `PotentialWorkCellsGlobal` getters.

All of these detours are diagnostic-only and exist only during the bounded capture. They are automatically unpatched when the 32 outer packages have completed.

For any outer package taking at least 64 ms, RimMT now keeps a bounded slow trace (up to the 8 slowest packages), including the Pawn label and the 10 largest **inclusive** measured phases. Inclusive timing is intentional: nested phases may overlap, so the trace is used to locate hotspots rather than pretend nested timings add up to the outer duration.

Runtime reports now contain both:

```text
JobGiver detail V0.4.8: ... slowPackages>=64ms=..., slowTracesKept=...
  SLOW#1: totalMs=... pawn=... topInclusivePhases=...
JobGiver infrastructure V0.4.8: samples=..., tracked=...
  #1 GenClosest.ClosestThingReachable: ...
  #2 Reachability.CanReach: ...
```

The intended V0.4.8 workflow is: run normally, start one bounded JobGiver detail capture from Mod Settings, let it auto-finish, then emit a runtime report. Production workerization should be based on those traces rather than guessing which WorkGiver is expensive.

## Production hauling Work acceleration retained

V0.4.6/V0.4.7 hauling fast paths remain present and fail-closed. The main thread snapshots haulable references/positions; workers may build immutable spatial indices; Vanilla reachability, validators, reservations and final jobs stay main-thread authoritative. Unsupported, stale, patched or ambiguous calls fall back immediately.

The target colony showed that the V0.4.7 direct global hauling path can have very low real hit rate, especially for priority-bearing calls, so V0.4.8 does not broaden that path speculatively. The next production optimization should target whichever outer JobGiver phase the new slow traces prove to be dominant.

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
