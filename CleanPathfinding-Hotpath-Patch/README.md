# Clean Pathfinding 2 - Hotpath Performance Patch

This independent compatibility patch keeps Clean Pathfinding 2's path-cost behavior while reducing its per-cell overhead.

- Redirects Clean Pathfinding's injected `AdjustCosts` call to a direct fast implementation.
- Prepares pawn state, map arrays, door-cost grid, sky glow, and relevant settings once per `FindPath` call.
- Removes development logging and debug-cell work from the production path.
- Unpatches the older `allen.cleanpathfinding.glowcache.optimizer` per-cell prefix/postfix if that mod remains active.
- Does not patch RimMT and does not run pathfinding on worker threads.

The patch fails closed: if the expected Clean Pathfinding call is not found exactly once, it leaves the original pathfinder IL unchanged.
