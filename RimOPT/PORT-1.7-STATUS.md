# RimOPT 0.4.0-alpha — RimWorld 1.7 / Waylith work-search hot paths

## Engine-specific basis
- User build: RimWorld 1.7.4984 rev1242, Waylith .NET 10.0.13, Harmony runtime 3.0.0-rc.3.
- Public compile reference: Krafs.Rimworld.Ref 1.7.4982-unstable, Harmony 3.0.0-rc.3, .NET 10.
- Waylith executes its own Vulkan rendering and work-tick environment. RimOPT **does not** hook its graphics job threads, modify its job queues, or run Verse job discovery asynchronously; synchronous RimWorld gameplay semantics remain relevant.
- The supplied game `Waylith.dll` and `Assembly-CSharp.dll` are private game binaries and are not copied to the public repository. Source-based signature and semantic verification is incomplete until in-game runtime acceptance.

## Main motivation from user diagnostics
- JobGiver_Work: 4,438 packages, 6.28 ms mean, histogram p99 <=250 ms, max 397.3 ms.
- Frame p99 <=75 ms, frame maximum 3116.6 ms; Tick max 2221 ms.
- Last Player.log: ReachabilityMemo `authoritative=False`, 2 external transpilers, 1 result-mutating postfix; TailPlans `chain=False`.
- It is **not** safe to claim the full 397ms is from reachability, GenClosest, or one named WorkGiver; these are package-level aggregates.

## Directly implemented in 0.4
1. **ReachabilityMemo**: if the installed CanReach patch chain includes foreign transpilers, finalizers or a result-mutating postfix, install **zero** CanReach hooks rather than recording verification-only cache entries. A late patch-chain change causes immediate pass-through. Prevent no-benefit .NET10/Harmony3 hot-path overhead.
2. **WorkSearchTailPlans**: never install a no-op Prefix when foreign GenClosest patches already exist. Harmony chain preflight now checks all patch categories (not only IL transpilers/finalizers); a pristine method without any patches is supported. The experiment no longer waits until 16ms has already elapsed; candidates 32..128 may be processed on the first call, still under strict existing 1.7 gating. **Still experimental** — substitute semantics on 1.7 not proven; leave disabled in the normal profile.
3. **DoBillIndex**: retain snapshot/lazy candidate-source optimization from 0.3.1; the 1.7 ShouldSkip Prefix no longer blindly enumerates every BillStack and replaces per-pawn vanilla work-giver rules. Only an empty indexed workshop source can skip vanilla.
4. **HaulMergeIndex**: all storage-group builds in one work package now share a 256-item scan budget, with <=128 per group and <=8 groups. Overflow falls back to the original. A negative early-out is allowed only when there is **no second partial stack** with matching def/stuff. Earlier heuristic requiring another stack's count >= source count was too aggressive and could miss legal merges.
5. Roof/harvest/snow candidate pruners retain conservative opt-in mode; no arbitrary change to WorkGiver/Thing definitions.
6. The optional pulse profiler remains default OFF and is never a prerequisite.

## Safety / limitations
- Preserve original 1.6 DLL and source compiled with old flags; new behavior only under `RIMWORLD17`.
- No Performance Fish integration for 1.7.
- A successful GitHub Actions build is an ABI/syntax check, *not* evidence that tail P99 or sustained TPS improved.
- `RimWorld 1.7.4984` was the player's live build while `RimRef 1.7.4982` was the publicly available reference; exact runtime behavior is unverified.
- Due to earlier 1.7 Vulkan VK_ERROR_DEVICE_LOST crash, graphics issues must remain a separate investigation and should not be attributed to job-search optimizations without evidence.

## v0.4 release hardening
- The old 1.6 global RimMT coexistence guard is deliberately excluded in the 1.7 build; per-module Harmony chain checks take precedence.
- Sampler status now reports whether the package hook was actually installed, rather than whether a package happens to be active while rendering the Mod Settings UI.
