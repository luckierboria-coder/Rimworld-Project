# AGENTS.md — RimMT 2.0 (RimWorld 1.6)

> Project-local agent handoff. This directory is a **source handoff placeholder**, not a claim that the working RimMT 2.0 code is already checked in.

## Identity / boundaries

- **RimMT 2.0**: independent RimWorld 1.6 performance mod, distinct from historical RimMT 0.x for 1.5. Keep package identity and installation paths independent.
- Long-term aim: offload substantial, genuinely parallelizable simulation calculation by following RimWorld 1.6's existing Unity managed-job / `JobHandle` dependency and completion model. Do **not** pretend the entire Pawn/Map simulation is thread-safe.
- Coexistence with Missile Girl and other performance/AI mods must be based on inspected Harmony patch chains, not hard-coded load-order assumptions.
- Prefer the existing linked GitHub repository/Actions for changes and builds. Do not automatically set up an unrelated local SDK or assume host game assemblies are available in CI.

## Latest reported checkpoint (2026-10-17)

The latest imported development conversation reports **RimMT 2.0 M1.1.4** source/build package, but only **M1.1.3** actually loaded in the last game session.

| Milestone | Status from the conversation, NOT independently CI-verified |
| --- | --- |
| M0.2 | Lifecycle fix: `FinalizeInit` requests startup; main-thread `Root.Update` actually starts/cleans up workers |
| M0.3 | Idle overhead reduced; disabled calculation modules should not maintain idle worker threads |
| M0.4 | Switched scheduling core toward game-native `ManagedJob` / `JobHandle` |
| M1.0.2 | Sampled slow `JobGiver_Work` and reachability-related searches |
| M1.1.x | Attributed searches to WorkGiver where possible; large `unidentified` remainder |
| M1.1.3 | Unknown-caller stack probe did not collect traces: per-call >=10ms gate was too restrictive |
| **M1.1.4** | Replaced time gate with sampling 1/8 unidentified calls, maximum 128 traces; reported built but not game-validated |

**No demonstrated multi-threaded gameplay speedup:** last reported `submitted=0`. Never conflate compilation, installed version, and successful in-game validation.

## Immediate next work

1. Commit the **verified M1.1.4 source project** from the supplied archive, including build scripts, mod metadata and tests. A portable local source package is prepared; do not merge its original DLL as a newly compiled binary. Do not reconstruct from summaries or copy historical 1.5 RimMT runtime.
2. Verify exact commit / DLL version / packaged source and target **RimWorld 1.6.4871** API. Validate CI build feasibility before claiming a build.
3. Review unknown-caller probe in context. Limit stack capture to bounded, opt-in sampling; preserve correct nested search attribution and avoid high-frequency overhead while profiler off.
4. Test M1.1.4 on the same representative save with a profiler-off baseline. Record `unknown[seen/traces]`, `unknownCallerTop`, `giverTop`, search counts, TPS, worker submission, relevant exceptions, and sample overhead.
5. Identify the dominant, **verified** search primitive before implementing the first true parallel calculation consumer. Prefer a shared scanner primitive over WorkGiver-specific Harmony shortcuts.
6. Introduce pure immutable snapshots with epoch validation, no gameplay object dereference from job workers, no main-thread synchronous wait, and per-subsystem fail-open / quarantine. Validate gameplay parity plus TPS/P95/P99 A/B.

## Threading correctness and integration

- Main thread: game-object access, snapshots, dispatch, live callbacks, reservations, reachability final check, Job creation, writes/commit.
- Worker: only fully materialized, immutable primitive arrays / pure deterministic computation. Do not use live `Pawn`, `Map`, `Thing`, `StatWorker`, Unity objects, or mod callbacks.
- Publication: verify epoch, freshness, lifecycle, and dependencies. If not ready/invalid/unsupported, use vanilla path; no wait inside tick.
- `CanReach` as implemented by the game owns map-local mutable queues and caches. Do not invoke it concurrently without verified isolation.
- Disabled diagnostics must unpatch hot-path Harmony wrappers where applicable. Do not infer performance gains from instrumented samples alone.
- Any behavior mismatch quarantines only the affected kernel/module. Never silently change selection order or cause false no-job.

## Repository/status caveat

As of this handoff, the linked repository's default branch did not contain a `RimMT2/` source tree, and a branch search did not find a RimMT 2.0 development branch. The earlier machine-specific package location is **not accessible** through a GitHub connector or a text-only conversation archive. The original M1.1.4 source-containing ZIP has now been supplied and verified offline (23 ZIP entries, no CRC errors, full C# source and compiled DLL). The GitHub connector cannot directly transfer that binary conversation attachment, so the **real source has not yet been committed to this branch**. A separate portable source ZIP was prepared with only four differences from the supplied archive: `Build/Build.ps1`, `Source/RimMT2/RimMT2.csproj`, `AUDIT.md`, and `README.md`; all C# implementation files and DLL/PDB are byte-identical. Do not claim a GitHub source sync or new build until they occur.

## Agent workflow

Read this file and `README.md` first; inspect imported source before making edits. Maintain exact version/commit/source/DLL provenance. Keep legacy RimMT 1.5 untouched. Avoid committing personal machine paths, logs, saves, or conversation archives to this public repository. Prefer a focused branch and reviewable PR with source, build evidence, smoke test results, and a rollback path.
