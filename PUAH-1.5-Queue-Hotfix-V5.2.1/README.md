# PUAH 1.5 Queue Hotfix V5.2.1

Independent archive of the V5.2.1 test release. This directory does **not** replace the V5.1 archive.

## Architecture

V5.2.1 is one RimWorld mod folder containing two DLLs:

- `PUAHQueueHotfix.dll` — the verified V5.1 queue repair / reservation guard, preserved byte-for-byte.
- `PUAHPerformanceV52.dll` — standalone V5.2.1 performance layer that patches only `PickUpAndHaul.WorkGiver_HaulToInventory.FindClosestThing`.

The V5.2 `JobOnThing` transpiler was removed. Original PUAH `Sort`, `GetClosestAndRemove`, `RemoveAt`, `CanReach`, validators, `StoreUtility`, reservations, capacity calculations and Job/queue construction remain authoritative.

## Verified DLL hashes

- V5.1 safety DLL SHA-256: `f78825f2b06291341667b917339b2a91c183738aa5e66e93cf049a48cf57e6dc`
- V5.2.1 performance DLL SHA-256: `3c6eba792e12c93da63aa7af2db45630a9ebd34e2dfb9e5ec9972b71539c56f7`

## Install

Remove/move older V5.1/V5.2 folders from `RimWorld/Mods`, install only the V5.2.1 folder, and load it after Pick Up And Haul. Do not enable V5.1 and V5.2.1 together because they use the same package ID.

If the performance layer needs to be disabled during testing, remove only `PUAHPerformanceV52.dll`; the remaining DLL returns the mod to the verified V5.1 safety behavior.
