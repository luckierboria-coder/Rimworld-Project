# RimOPT 0.4.1-alpha — RimWorld 1.7 Waylith regression remediation

## Triggering evidence (not a controlled benchmark)
- Same user's v0.3.1 sample: 4,438 work packages, mean 6.28 ms, p95 <=16ms, p99 <=250ms, max 397.3ms.
- v0.4 sample: 1,510 work packages, mean 23.53 ms, p95 <=250ms, p99 <=250ms, max 341.5ms.
- The samples differ substantially in call counts and likely workload. Do not conclude 0.4 alone caused the difference without equivalent save/setup comparisons.
- The design target remains sustained TPS without worse long-tail (p95/p99) work search.

## Major source-level regression risk discovered
1. **DoBillIndex.SourcePostfix** previously substituted `WorkGiver_Scanner.PotentialWorkThingsGlobal = null` with a global, filtered snapshot for `WorkGiver_DoBill`. That changes the scanner's choice between native region search and global candidate enumeration. Without a 1.7 semantic proof, it is unsafe for performance *and* job ordering. In 1.7, this invasive Postfix is now **not installed**. The spawn/despawn snapshot maintenance hooks are also **not installed**. Only a cheap `ShouldSkip` early exit when an exact vanilla DoBill giver's native potential ThingRequest has no listed things remains; all other cases use vanilla logic.
2. **HaulMergeIndex** used to eagerly enumerate up to 128 Things on its first visit to each group, possibly followed by an original search anyway. 0.4.1 waits for two vanilla visits, then considers building a cached index only on a subsequent visit to a countable storage group containing 16–128 Things. A work package can scan at most 256 held Things in total, and at most eight groups. A late group-count change forces vanilla. Other foreign stack/merge logic disables the hook before install.
3. **GenClosest tail substitution** is forcibly retired for 1.7 in 0.4.1, even if the old checkbox is persisted as enabled. The sorted global-set replacement is not proven equivalent to native 1.7 `GenClosest` ordering/region fallback, and can cause missing jobs or worse frame spikes. Historical 1.6 code remains intact.
4. **ReachabilityMemo** maintains the 0.4 safety behavior: do not install when foreign IL/return-mutating patches prevent authoritative reuse. No expensive verify-only loop on those chains.
5. Original roof/harvest/snow pruners are opt-in and unchanged.

## Compatibility and accuracy
- Build target .NET 10 / Harmony 3.0.0-rc.3 / public 1.7.4982-unstable reference assembly; user's game was 1.7.4984 rev1242.
- No separate local build environment. User-controlled GitHub Actions compiles and stages a zip.
- Compiling confirms managed API signatures and valid IL generation, **not** measured TPS/p99 improvement or correctness under all mods.
- Preserve 1.6 DLL and 1.6 compile branches; all new behavior is under `RIMWORLD17`.
- No Performance Fish coupling; do not restart RimMT development.
