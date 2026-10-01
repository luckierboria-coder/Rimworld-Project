# RimMT Lean Live V2 module audit

Audit date: 2026-10-01

## Evidence reviewed

- 312 archived packages representing 241 unique ZIP hashes.
- 432 Git commits touching the RimMT source tree.
- 15 preserved non-duplicate runtime reports across 11 reported versions, normalized into 272 module-evidence rows.
- The current installed DLL and latest `Player.log`, including the low scheduler utilization, T34 candidate-fabric counters, Pawn idle regressions and repeated-job errors observed during the D.2.x tests.

Runtime counters are cumulative and sessions have different lengths, so raw maxima are not treated as benchmark scores. A module is retained only when it has repeated production hits, a narrow synchronous lifetime, and a fail-open or live-revalidation path. Compilation and packaging do not count as gameplay proof.

## Retained production modules

| Module | Preserved evidence | Safety boundary |
|---|---:|---|
| Text metric cache | up to 177,002,182 hits with very few misses | UI measurement only |
| T4 HaulMerge partner index | up to 211,547 exact negative rejects; representative reject rates around 43-48% | one synchronous work package; survivors call original `JobOnThing` |
| S4 slow-search rescue | up to 29,476 accelerated searches and 837,063 cheap prefilter rejects | activates only after the measured slow-tail threshold; validator and `CanReach` run live |
| S5.1 small custom-set rescue | up to 150,499 accelerated searches and 1,698,909 live validator rejects | known-size custom sets only; validator and `CanReach` run live |
| T8 carrier/mech negative filter | later reports show large exact-negative batches; unsupported Harmony authority bypasses the optimization | nested inside the S4 slow path; survivors run the original validator |
| Persistent DoBill index | up to 34,246,016 package-local false-memo hits; representative avoidance about 70% | `AnyShouldDoNow` is evaluated live before a false value can be reused inside the same synchronous package |
| Common Sense ingredient memo | up to 5,961,186 hits; 99.8% class hit rates; up to 31,164.6 ms estimated duplicate work avoided | compatibility-specific memo; original live behavior owns misses and unsupported calls |
| T28 package scope | provides the synchronous lifetime used by the retained filters | stores no Job, reachability, reservation or cross-package result |

## Removed from the production assembly

| Family | Reason |
|---|---|
| T34-A/B/C/D candidate fabrics | low useful yield relative to continuous scanning, plan construction and tick flushing; associated experimental branches repeatedly regressed frame pacing |
| Worker scheduler and main-thread dispatcher | scheduler median production utilization was 0.00% and never exceeded 0.04% in the preserved reports; dispatcher drained at most hundreds of callbacks while running tens of thousands of frame drains |
| T20/T21 validator and reachability replay | touched live work eligibility and reachability; this family was present during Pawn idle and worker-thread `CanReach` regressions |
| T22 generic GenClosest transaction index | small authoritative yield compared with observed calls and extra plan construction; no strong end-to-end TPS proof |
| T26/T27 epoch, persistent-map and parallel-kernel infrastructure | infrastructure cost without verified production utilization |
| T32 reservation replay | changed reservation answers and was present in unstable job-selection builds |
| Global haul worker/index paths | explicitly retired after 6/736 accelerations and zero avoided candidates |
| Global-nearest package plan cache | removed even though its old helper type remains as a minimal timing scope; no candidate plan is stored now |
| Clean Pathfinding hooks | removed from RimMT so RimMT cannot stack another cache or RegionCost behavior on Clean Pathfinding/Pathfinding Framework |
| Resident profilers and censuses | useful for temporary diagnosis but pure overhead in a production DLL |
| Path topology generation | its only consumer was the removed reachability cache, so retaining the invalidation hooks had no benefit |

## Source cleanup result

- The project now uses `EnableDefaultCompileItems=false` and lists every production source file explicitly.
- 74 retired C# files, about 26,000 lines, were removed from the active source tree. Their history remains available in Git and the version archive.
- The old nearest-first implementation was reduced to a small synchronous scope timer; its candidate plan cache and Harmony hooks were deleted.
- The final DLL contains 18 explicitly listed source files and no scheduler, dispatcher, tick sampler, T34 fabric, reachability cache, reservation cache, Clean Pathfinding hook or resident profiler.

## Validation status

- Release build: passed with 0 errors and 0 warnings using the persistent local .NET SDK and NuGet cache.
- Assembly content audit: retained module names are present; retired module names are absent.
- Deployment: intentionally pending because RimWorld was still running during the build.
- Runtime acceptance: requires a fresh game launch, a representative save run, and a new on-demand report.
