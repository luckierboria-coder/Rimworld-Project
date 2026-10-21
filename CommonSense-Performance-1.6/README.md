# Common Sense Performance 1.6 — M5 integrated source

**Build status:** M5 successfully compiled with existing GitHub Actions for RimWorld 1.6.4871 (0 errors). Gameplay/TPS compatibility is NOT verified.

## Authoritative upstream and targets
- Common Sense 1.6 source: https://github.com/catgirlfighter/RimWorld_CommonSense (Source/CommonSense16/CommonSense)
- Audited the following hot-path implementations: Utility.SelectAllFilth, Utility.OptimizePath, OpportunisticTasks.Cleaning_Opportunity, IngredientPriority.PreProcess / DoSort, JobGiver_DoBIll_Rework, Utility.CleanFilthToil, MealStacking, RandomIngredients.
- Original uploaded 1.6 CommonSense.dll SHA (Git blob): 145f41329d2f69b03fac198a6132ad1bf8ad6c61; matches upstream at audit time.

## Included components
1. M1 — one-pass filth candidate selection, removing repeat LINQ enumeration.
2. M2 — provable null-result pruning of third path search in Cleaning_Opportunity (when enabled in original).
3. M3-A — single-call snapshots of MedicalPotency and rot-bucket keys before ingredient List.Sort, preserving the original comparator, only for 8+ candidates; private helper Prefix; no cross-tick caches.
4. M3-B — eliminate a redundant GetSlotGroup() request in Common Sense's private extended storage search, preserving collection order, validators, and pawn.CanReach.
5. M4 — optimize greedy ordering for cleaning targets and ingredient ThingCount queues, same comparison/tie rules, fewer repeated list/position reads. No cross-tick caching of CleaningSpeed or terrain cleaning factor.
6. M5 — opt-out settings for M3/M4, per-target Harmony conflicts fail closed, exact signature checks, error fallbacks, source audit, build pipeline and regression checklist.

## Compatibility and verification boundary
This is an independently installed patch (no original Common Sense DLL replacement).
It does NOT globally patch pawn paths, WorkGiver_DoBill, stat calculations, or threading.
Each patch refuses to attach to an already Harmony-patched target. Runtime exceptions disable the affected module for the remainder of that game session and fall back to upstream logic.
**Unverified**: actual runtime compatibility with the user's Missile Girl, Performance Fish, Performance Optimizer, Common Sense - Fix or other installed mods. Those need a loaded Game + Harmony patch list and in-game profiling.

## Explicitly excluded high-risk optimizations
- Cross-tick caching of pathfinding, cleaning speeds, reservations, room membership, work settings.
- Rewriting stat-dependent CleanFilthToil tickAction (terrain/stat changes can alter results).
- Replacing Common Sense's global game-engine transpilers.
- Rewriting IngredientPlaceCellsInOrder or JumpIfTargetInsideBillGiver reflection used by job construction without exact assembly-level tests.
- Changing List.Sort tie behavior or the greedy ordering to another algorithm.
- Multithreaded pawn work processing.

## Test and rollback
1. Install after Harmony + Common Sense. Do not install alongside older M1/M2 copies. Keep Missile Girl in its recommended position.
2. Use a backed-up **test save** for first startup. Verify console messages [CS Perf] / [CS Perf M2] / [CS Perf M3-A] / [CS Perf M3-B] / [CS Perf M4-*].
3. Test: dirty workshop cleaning before/after job, tending, giant rooms, doors, allowed zones, WorkGiver reservations, medicine and food stockpiles, spoiling preference, recipe ingredient choice, work bills, pawn carrying capacity.
4. With Dubs Performance Analyzer compare identical save snapshots, speed, camera area and tick windows: TPS, Update/Tick time, CleanOnOpportunity/SelectAllFilth, Cleaning_Opportunity/FindPathNow, ingredient search and queue ordering, GC Alloc.
5. Test feature toggles individually and paired with Missile Girl, Performance Fish and Performance Optimizer. Never interpret successful compilation as compatibility proof.
6. On any unexpected behavior, uncheck individual M3/M4 features (restart) or remove this independent patch. Existing Common Sense is not modified.

## Build and artifacts
Build on existing GitHub Actions pipeline using Krafs.Rimworld.Ref 1.6.4871, Lib.Harmony and net472. On a verified compilation, the release ZIP is published at dist-artifacts/CommonSense_Performance_1.6_M5.zip.
