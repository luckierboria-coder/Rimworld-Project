Clean Pathfinding - Glow Cache Optimizer
RimWorld 1.5.4063

Dependency:
- Clean Pathfinding 2 (Owlchemist.CleanPathfinding)

What it does:
- Hooks CleanPathfinding.CleanPathfindingUtility.GameGlowAtFast(Map,int).
- Reuses each cell's glow result for a bounded 30-tick window.
- Cache is per-map and indexed by cell.
- Cache misses always fall through to Clean Pathfinding's original implementation.
- Worker-thread calls always fall through to the original implementation.
- Cache is cleared if game ticks move backwards (save/load/tick reset safety).

What it does NOT do:
- No RimMT dependency.
- No RimMT Diagnostics integration.
- No changes to A* heuristic, endpoint semantics, Reachability, jobs, or PathEndMode.
- No replacement of Clean Pathfinding's final path-cost logic.
