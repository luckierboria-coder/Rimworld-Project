# PUAH 1.5 Queue Hotfix V5.1

Source archive for the RimWorld 1.5 Pick Up And Haul queue hotfix developed in this project.

## Archived version

- Mod: PUAH 1.5 Queue Hotfix V5.1
- Package ID: `local.PUAH15.QueueHotfix`
- Source recovered from the final buildable package: `PUAH_1.5_QueueHotfix_Buildable_v5_1.zip`
- Runtime DLL archived at `1.5/Assemblies/PUAHQueueHotfix.dll`
- Archived DLL SHA-256: `f78825f2b06291341667b917339b2a91c183738aa5e66e93cf049a48cf57e6dc`

## Purpose

Repairs malformed `HaulToInventory` target/count queues produced by Pick Up And Haul on RimWorld 1.5. It removes invalid paired entries, trims unmatched queue tails, preserves valid work, and adds a final reservation-stage crash guard. Successful repairs are intentionally silent in V5.1 to avoid hot-path log/stack-trace stutter.

## Source

The complete hotfix logic is in `Source/Hotfix.cs`.

## Building

The original local build scripts are preserved. They compile against RimWorld 1.5 managed assemblies and expect `BuildRefs/0Harmony.dll`. The Harmony reference DLL from the old buildable package is intentionally not duplicated in this source archive; copy `0Harmony.dll` from your installed Harmony mod into `BuildRefs/` before rebuilding.

The already-built V5.1 DLL is included under `1.5/Assemblies/`.
