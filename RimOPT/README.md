# RimOPT 0.1.4

## Module switches

Options -> Mod Settings -> RimOPT contains seven independent switches: reachability memo, DoBill index, HaulMerge index, Work search tail, roof, harvest and snow/sand pruning. Changes require a complete game restart. The startup configuration is snapshotted; toggling a checkbox does not partly uninstall a live Harmony chain. A disabled module does not install its hooks. If all four package-dependent modules are off, PackageScope is not installed either.

Defaults preserve 0.1.3 behavior: all seven flags true. Existing patch-chain compatibility checks are unchanged. Enabled means permitted, not guaranteed to take authority. The UI and startup log distinguish configured flags from the startup installation state. This update does not change Performance Fish settings, re-enable the RimOPT package or bypass safety checks.

See `PF-COMPARISON.zh-CN.md` for exact overlap and limitations. Same target does not automatically mean an actual conflict or equivalent functionality. Tests cover all 128 switch combinations, immutable startup plans and persistence keys; actual settings rendering and Harmony application still require a fresh game session.

RimOPT is a focused RimWorld 1.6 work-search optimizer extracted from measured RimMT modules.

RimMT 2.0 can run alongside RimOPT. The old RimMT package family remains excluded because it patches overlapping work-search logic. RimMT 2.0 uses RimWorld 1.6's engine job scheduler; RimOPT owns synchronous Work search optimization.

Included:

- package-local false-only `BillStack.AnyShouldDoNow` memoization;
- incrementally maintained DoBill source snapshots;
- package-local HaulMerge impossible-partner rejection;
- authority-gated pruning for already-roofed cells, non-harvestable plants, and shallow snow.
- exact `CanReach` result reuse inside one synchronous `JobGiver_Work` package, with live warm-up checks, periodic rechecks, target fingerprints, patch-chain auditing, and local quarantine on any mismatch.
- an S4-derived, validator-first tail for `GenClosest.ClosestThingReachable` custom `IList<Thing>` sources after 16 ms of the current Work package, only where the 1.6 request skips region search and uses the original `JobGiver_Work` validator;
- a T34-B-derived, source-indexed distance plan reused inside that same Work package after checking source membership and positions. This is synchronous and does not run a worker.

The new tail runs the original validator and `CanReach` on live state, preserving distance and original-list tie order. It changes their order from the 1.6 global fallback (`CanReach` before validator) to validator before `CanReach`; this is the intended reduction in wasted reachability calls. Foreign `GenClosest` transpilers/finalizers disable this route. Runtime performance and behavior require a fresh in-game check; the old RimMT counters are not 1.6 proof.

Excluded:

- Pawn or map tick threading;
- whole job-search offload;
- path-result or cross-package reachability caches;
- reservation changes;
- cross-tick Job, target, priority, or positive-result caches;
- RimMT diagnostics and compatibility experiments.

Final WorkGiver validation and Job creation stay in the game and active mods. Modules check their relevant patch chains and fall back when a supported call shape cannot be established.

The old Clean Pathfinding A1/A2 cost-grid patches are intentionally absent. Clean Pathfinding 1.6 already maintains native cached cost/overlay arrays and incrementally refreshes dirty cells, so porting the old 1.5-era hooks would duplicate its current implementation.
