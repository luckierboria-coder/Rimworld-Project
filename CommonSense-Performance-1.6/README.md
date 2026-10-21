# Common Sense Performance — RimWorld 1.6

Status: **M1 source only; not compiled or gameplay-validated.**

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

Do not claim actual TPS improvement, error-free compilation, or compatibility with every installed mod until measured.