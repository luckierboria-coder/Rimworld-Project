# Common Sense Performance — RimWorld 1.6

Status: **M2 candidate source, pending GitHub Actions compilation and gameplay validation.** The M1 compiled package is still available in `dist-artifacts/CommonSense_Performance_1.6_M1.zip`. M2 will publish separately as `dist-artifacts/CommonSense_Performance_1.6_M2.zip` after a verified build.

## Baseline
- User-provided 1.6 CommonSense.dll Git blob SHA: 145f41329d2f69b03fac198a6132ad1bf8ad6c61
- Verified identical to catgirlfighter/RimWorld_CommonSense upstream master, 1.6/Assemblies/CommonSense.dll.
- Decompiled/available upstream source method: Source/CommonSense16/CommonSense/Utility.cs, SelectAllFilth(Pawn, LocalTargetInfo, int).

## What M1 changes
- Single-pass filth selection rather than LINQ OfType/Where/Take enumerators.
- Avoid duplicate eligibility scans from Count() followed by queueing for small rooms.
- Preserve room ordering, radial search first 200 cells, allowed areas, doorway and reserveability checks via HasJobOnThing.
- Keep the upstream per-cell limit semantics in huge rooms.
- No tick throttling, multi-threading, or changing work priority.
- Automatically skip if a Harmony patch already targets this method.
- On exception, fall back to original Common Sense for the session.

The small-room results are now snapshotted once rather than lazy; in rare cases this may differ if the world changes between the original two enumerations. This still requires gameplay testing.

## Build (user's Windows machine, no new toolchain set up here)
Requires RimWorld 1.6 Managed assemblies, matching Harmony 0Harmony.dll, .NET Framework 4.7.2 targeting pack and dotnet SDK / MSBuild.

PowerShell example (adjust paths):
dotnet build ./CommonSense-Performance-1.6/Source/CommonSensePerformance.csproj -c Release -p:RimWorldManagedDir=C:/Games/RimWorld/RimWorldWin64_Data/Managed -p:HarmonyDllPath=C:/Games/RimWorld/Mods/Harmony/Current/Assemblies/0Harmony.dll

Output: CommonSense-Performance-1.6/1.6/Assemblies/Allen.CommonSensePerformance.dll

Install the mod directory under RimWorld/Mods. Load after Common Sense, with Missile Girl last in the overall list.

## Test matrix
Compare patch on/off using same save and equal tick/sample windows with Dubs Performance Analyzer. Inspect TPS, CommonSense.Utility.SelectAllFilth allocations/calls, colony work decisions, pawn idle, cleaning jobs, no dirt, huge/outdoor rooms, doors, forbidden dirt, reservation contention, and Limit = 0/1/5. Check Player.log for patch message.

The GitHub Actions job verifies successful compilation against `Krafs.Rimworld.Ref` 1.6.4871 and stages `Allen.CommonSensePerformance.dll`. This does **not** prove runtime compatibility, work-behavior equivalence, or actual TPS improvement; validate in-game before normal use.
 
## M2 — conservative path pruning
- Installs an independent Harmony Prefix on private `CommonSense.OpportunisticTasks.Cleaning_Opportunity(Job, Pawn, int)`. Checks exact signatures and skips patches if the target already has Harmony modifiers.
- Active only if Common Sense's `calculate_full_path` setting is true. Other modes use original method.
- Preserves the initial eligibility check, first two pathfinder calls, path pooling, and original worker/job selection via a cached delegate to upstream `MakeCleaningJob`.
- If first two paths are found and `stob > 500`, `stot < 0.65 * stob`, the original method's rejection predicate will necessarily reject the job for all non-negative third-path costs. M2 skips the third path and returns null. An unreachable third path also implies null in the original.
- Otherwise computes third path and applies the exact original `stob > 500 && stot / (stob + btot) < 0.7f` predicate, preserving result.
- The 0.65 safety margin is deliberate: pruning covers fewer cases in exchange for float comparison robustness.
- No cross-tick caching, no global pathfinder patches, no multi-threading, no AI polling changes.
- If the optimized selector fails during runtime, M2 disables itself for the session and falls back to the original Common Sense method. M1 remains independent.
- The private method delegates are bound at initialization; if a different Common Sense build changed the signatures, M2 refuses to install.

### M2 regression matrix
1. Test `calculate_full_path` true/false, `clean_before_work` true/false.
2. Compare M1 vs M2, first in reachable open rooms, then doors, mountain corridors and deep-water barriers.
3. For path costs above 500, verify the near-material rejection case and reachable vs unreachable target.
4. Confirm job insertion and completed cleaning tasks are unchanged; capture Dubs Performance Analyzer profile for `Cleaning_Opportunity` and `FindPathNow`.
5. Test alongside Missile Girl, Performance Optimizer and Performance Fish as separate toggles; check logs for `[CS Perf M2]`.
