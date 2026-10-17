# RimMT 2.0

Independent multithreaded performance project for **RimWorld 1.6**, superseding the architecture of (but not replacing the installation of) historical RimMT 0.x.

> **Status (2026-10-17):** Handoff documentation only. The last chat checkpoint reports an M1.1.4 diagnostic package compiled locally, but the newest tested game version was M1.1.3. The actual M1.1.4 ZIP has now been supplied and reviewed offline, but its source has **not yet been committed into this GitHub directory**. Do not treat this branch as an installable release.

## Target architecture

Reuse the game's built-in **Unity ManagedJob / JobHandle** scheduling and dependency model for heavy pure computation, using main-thread-created immutable snapshots. Keep live Pawn/Map state mutation, mod callbacks, job selection commits, and fallback on the game thread. Optimize expensive shared scan/search stages; do not re-create a separate idle thread pool.

## Current development blocker

The job-search profiler sees high aggregate cost in **unidentified** nested/global/region candidate searches. It is unsafe to attribute that entire cost to `FillProcessor`, even though `FillProcessor`, medical, and visitation tasks are known contributors. The M1.1.3 `>=10 ms per unidentified call` stack threshold produced `unknownTraces=0`, so the latest M1.1.4 iteration uses bounded call-count sampling (1/8; at most 128).

## Next verification

- Commit the verified **M1.1.4 source/build package** to this branch. The supplied ZIP has 23 CRC-clean entries. The portability patch changes only Build/Build.ps1, RimMT2.csproj, AUDIT.md and README.md; all .cs runtime source and DLL/PDB files are byte-identical.
- Confirm its actual installed/runtime DLL version (not from version text alone).
- Run comparable samples in a representative save, including periods of poor TPS, with the profiler explicitly enabled only for attribution.
- Capture `unknown[seen/traces]`, `unknownCallerTop`, `giverTop`, total sampled searches, top slow calls, lifecycle failures, and `submitted`.
- **Turn diagnostics off** for a comparable Production A/B. Evaluate TPS and P95/P99, correctness (job decisions, idle/no-job), and worker utilization only as context.
- After identifying a verified high-cost primitive, write its snapshot → ManagedJob → checked result-consumer path and test with an epoch/fallback/parity contract.

See [AGENTS.md](./AGENTS.md) for the detailed implementation constraints and current handoff.

## Evidence provenance

The milestone chronology in this README is from the user's archived, user-visible previous development conversation (through 2026-10-17), not a fresh run of the game or CI. The public repository did not yet have the actual 2.0 source at the time this documentation was written.
