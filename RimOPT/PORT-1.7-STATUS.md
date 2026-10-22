# RimOPT 0.3.0-alpha / RimWorld 1.7 (Waylith)

## Design goal

Keep sustained TPS at least as high as the unmodified 1.7 baseline while decreasing infrequent long work-search and frame-time spikes. A compile-only pass is **not** evidence of this goal being achieved.

## Changes

- Target .NET 10, Harmony **3.0.0-rc.3** (matching user `Player(8).log`), and public Krafs RimRef `1.7.4982-unstable` reference assembly.
- Performance Fish-specific language/checks are removed from the **compiled 1.7 UI** (unchanged 1.6 UI/source paths are preserved).
- Add optional, default-OFF, low-allocation pulse sampler for frame-to-frame intervals, `Root_Play.Update` CPU time, `TickManager.DoSingleTick` CPU time and `JobGiver_Work` package durations, with in-settings histogram percentiles and manual log export. No per-frame logging and no pawn-work deferral.
- Bound experimental tail-plan sorting to **256** candidates in 1.7, avoiding a 4,096-item sort in an already slow work package. Preserves fail-open vanilla behavior for oversized sources.
- Bound experimental haul-merge scan to **256 things/group** and **24 groups/work package**, falling back to vanilla outside those limits rather than creating unbounded index work.
- Seven original optimization switches remain OFF by default in 1.7. All 1.6 assembly and compilation paths stay untouched.

## Limits / log evidence

User `Player(8).log` (RimWorld 1.7.4984 rev1242) shows RimOPT installed but **all seven optimizations inactive**. The 4,890 warnings `Getting random element from empty collection.` stem from `SizedApparelUtility.GetRandomPubicHair` during save load. VMF patch errors involve VPE projectile handling and vehicle arrival options. These are **other-mod issues** and are not silently 'fixed' or suppressed by RimOPT. The game update is 1.7.4984, while the available compile-only RimRef is 1.7.4982; exact method behavior requires game validation.

## Testing protocol

1. Load a *copy* of the save. Keep all seven optimization modules disabled. Enable only optional pulse sampler and restart.
2. Let game run with same camera/speed/work priorities for 2-5 minutes. Record in-game sampler p95/p99/max for frames, update, ticks and work packages (press manual-export button).
3. Run the same comparison with sampler OFF; verify instrument overhead is acceptable before using samples to justify a change.
4. Enable **one safe candidate module** at a time and repeat. Look for no lost jobs, no idle pawn regression, and no TPS drop. Tail planner stays last because it can itself cause a spike.
5. Only after controlled tests should any optimization default be enabled. Sample tail risks are still under evaluation. No background worker scheduler or TickManager deferral is introduced.

Build artifacts should be retrieved from GitHub Actions, not locally compiled. Game proprietary DLLs stay out of the public repository.
