# RimMT

Compatibility-aware multithreading runtime for **RimWorld 1.5.4063**.

## Design goal

RimMT's primary objective is **reducing visible stutter and frame-time spikes**. Average TPS is secondary. A change that raises throughput but introduces foreground waits, worker spin, GC churn, or new main-thread spikes is treated as a regression.

Preferred architecture:

**main-thread capture -> worker computation across spare cores -> later main-thread consumption/validation -> Vanilla commit**

The main thread must never wait for a worker result. If a worker result is missing, stale, unsupported, incompatible, or ambiguous, RimMT falls back to Vanilla.

## V0.4.17.1 — Work Prefilter compatibility hotfix

The first V0.4.17 playtest on the target large mod list showed that all three Work kinds were correctly fail-closed by foreign Harmony owners: Biomes! Core on GrowerSow, Humanoid Alien Races on GrowerHarvest, and Watchtowers on BuildRoof.

V0.4.17.1 source-reviews and re-enables only two exact cases:

- **GrowerHarvest + Humanoid Alien Races:** the exact `AlienRace.HarmonyPatches.HasJobOnCellHarvestPostfix` is a downstream restriction that only preserves false or changes true to false. RimMT hard-negative false results therefore remain safe.
- **GrowerSow + Biomes! Core:** the exact Biomes prefix changes growth-season semantics through shared per-plant context. RimMT does not reproduce that shared-static protocol off-thread. Sow prefiltering is re-enabled in restricted mode, but any worker prediction containing `OutOfGrowthSeason` is forced back to live Vanilla.
- **BuildRoof + Watchtowers:** remains fail-closed because the Watchtowers Postfix semantics have not yet been source-reviewed.

Compatibility is still exact-method based. Any additional unknown Prefix/Postfix/Transpiler/Finalizer on the authoritative target keeps that Work kind Vanilla.

## V0.4.17 — Parallel Work Prefilter

The current target save showed that V0.4.16 aggressive Reachability successfully moved large volumes of `CanReach` work off the main thread, while JobGiver long-tail spikes still concentrated in `GenClosest` and expensive WorkGiver scans.

V0.4.17 adds `parallel.workPrefilter` for exact Vanilla:

- `WorkGiver_GrowerSow`
- `WorkGiver_GrowerHarvest`
- `WorkGiver_BuildRoof`

RimMT records the exact cells Vanilla already enumerates. The current scan stays unchanged. After enumeration, those cells are classified on the validated eight-worker scheduler, and a later scan may consume only **hard-negative** hints.

Workers may perform a narrow whitelist of live read-only Verse checks in this experimental module. They do **not** create Jobs, reserve targets, change WorkGiver priority/order, or mutate Pawn/Thing/Map state. Unknown/positive candidates always run Vanilla.

Negative hints use warmup parity validation, continuing sampled validation, per-kind cooldown, a short authority lifetime, and a global false-negative fuse.

BuildRoof deliberately does not run Vanilla roof-support flood-fill on workers because `Map.floodFiller` is shared mutable scratch state.

## V0.4.16 — Aggressive Reachability

`parallel.reachProfile` captures live `Region.Allows(TraverseParms)` decisions on the main thread and builds per-Pawn connectivity profiles on workers. Validated profiles may bypass live `RegionTraverser`/`Reachability` work.

The target V0.4.16.3 run produced more than 874k profile hits and roughly 792k authoritative profile results. 82,106 sampled comparisons produced one `predTrue/liveFalse` mismatch and zero `predFalse/liveTrue` mismatches. Per-profile cooldown and the global parity fuse remain active.

Runtime Harmony census support records every foreign Prefix/Postfix/Transpiler/Finalizer on the exact `Reachability.CanReach` overload. Current reviewed coexistence cases include VFECore Phasing, Pathfinding Framework diagnostics, and Hospitality guest-area authority.

## Scheduler

V0.4.15 replaced the old AutoResetEvent wake path with independent SemaphoreSlim work credits. On the target 12-logical-processor system, the fixed eight-worker pool has been proven to reach `peakActive=8`.

The worker cap remains eight until real gameplay telemetry shows sustained queue/capacity pressure. Adding workers before there is enough useful parallel work would only add contention.

## Retained production modules

- V0.4.6 hauling Work-search accelerator
- V0.4.7 exact ListerHaulables global-search accelerator
- V0.4.14 persistent map search fabric
- V0.4.15 permissive Region connectivity fallback
- Path snapshot parity validation (Vanilla PawnPath remains authoritative)
- Text metric cache
- Butter++ logical-tick dispatcher barrier

Unsafe state-parallel modules remain disabled by default:

- `parallel.pawnTick`
- `parallel.reservations`
- `parallel.thingTick`

## Compatibility principles

RimMT prefers narrow exact-method compatibility decisions over broad mod-name allowlists. A foreign Harmony owner on a V0.4.17 authoritative WorkGiver method disables only that WorkGiver kind unless a later compatibility shim has source-reviewed that exact patch method and preserved its authority. Clean Pathfinding does not need to be removed. Butter++ uses its manager-level logical-tick probe as the dispatcher commit boundary.

See `V0.4.17.1_NOTES.md` for the current playtest acceptance checklist.
