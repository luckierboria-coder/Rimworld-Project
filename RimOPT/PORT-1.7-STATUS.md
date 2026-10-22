# RimOPT 0.3.1-alpha / RimWorld 1.7 source-targeted optimization

## Objective
Reduce synchronous peak costs at the seven existing RimOPT optimization targets without reducing sustained TPS, deferring Pawn work across ticks, or altering game job decisions. 1.6 code paths and DLL are unchanged.

## Based on direct code inspection
1. **DoBillIndex**: The old SpawnSetup/DeSpawn hooks copied `Thing[]` for every workgiver entry, potentially repeatedly during bursts of map/build events. On 1.7, these callbacks now invalidate the cached index only, and a subsequent actual query reconstructs it once. The source Postfix returns a lazy `IEnumerable<Thing>` that filters bill-giver candidates as consumed; short-circuiting work searches no longer have to pre-filter/copy the entire source list. This preserves candidate iteration order, validators and conditions, but requires real-game validation for all modded enumerators.
2. **HaulMergeIndex**: Hard budgets on 1.7: at most 8 distinct groups per work package and at most 128 held things per group. Countable oversized groups skip enumeration entirely. On overflow, the original method runs, not a guessed answer.
3. **ReachabilityMemo**: A foreign Postfix that mutates a CanReach result now forbids authoritative memo skipping. Remove the high-frequency Finalizer hook in 1.7 (an exception prevents the normal cache-store Postfix anyway). Cap in-package memo at 512 items; overflow uses original path. No cross-pawn or cross-tick cache.
4. **WorkSearchTailPlans**: Restrict 1.7 sorts to 128 candidate objects and at most 8 plans per work package, otherwise immediately return to original search. The geometry/region-equivalence proof for 1.7 remains outstanding, so this dangerous shortcut stays opt-in only.
5. Existing roof, harvest and snow/sand source pruners are preserved, unchanged. Only run when individually enabled; do not assume modded plant and workgiver semantics.
6. The optional pulse sampler is no longer a prerequisite for any optimization and defaults off; it remains available for developer diagnosis.

## Compatibility
- Build for .NET 10, Harmony 3.0.0-rc.3, public RimRef 1.7.4982-unstable reference assembly.
- No Performance Fish detection or dependence in the 1.7 runtime.
- Preserve safety checks for foreign Harmony patches and exact type owners.
- Compilation alone does not establish no TPS regression or semantic correctness.
- User log was RimWorld 1.7.4984; API signatures and game behavior remain to be confirmed there.
- The original RimWorld/Waylith binaries supplied by the user are proprietary and **not** pushed to the public repository.

## Validation state
Source-level structural fixes provided without requesting user-side benchmarking first. **In-game behavior and measured stutter reduction are still unverified.** Do not enable the experimental tail planner or reachability fast bypass for critical saves until specifically validated.
