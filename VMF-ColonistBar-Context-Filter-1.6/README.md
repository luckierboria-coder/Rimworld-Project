# VMF Colonist Bar Context Filter — RimWorld 1.6

**Purpose:** Remove the empty or redundant vehicle-map group in the top colonist bar when RX97 Animal Cart (or another VMF vehicle) is spawned at a player colony, while **preserving its original caravan presentation on the world map**.

## Inputs verified

- `VehicleMapFramework-v1.6.673.zip`: packageId `OELS.VehicleMapFramework`, 1.6 DLL present.
- `2673413930.zip`: Colonist Bar Adjuster, packageId `Syrus.ColonistBarAdjuster`, RimWorld 1.6 DLL and actual C# sources present.
- VMF source `VMF_HarmonyPatches/Patches_GUI.cs`: `Patch_ColonistBar_CheckRecacheEntries.ExcludeVehicleMaps` filters `Find.Maps` and excludes only `m==vehicle.VehicleMap` for spawned vehicles.
- CBA source `HarmonyPatches.cs`: it patches bar layout and group-frame rendering, not vehicle-group membership. Its `HideBackground` setting is global rather than per vehicle group.

## Implementation

Harmony postfix on VMF's existing `ExcludeVehicleMaps(IEnumerable<Map>)` method filters additional nested/remaining vehicle pocket maps if their owner vehicle chain is currently spawned on a player home map.

- Owner is unspawned (actual vehicle caravan): retain map/group.
- Owner still spawned on colony: filter interior pocket map/group.
- Owner spawned on non-home map: retain.
- Unknown map/VMF version: pass through unchanged and warn.
- No changes to `ColonistBar.Entries` after layout; do not interfere with CBA's placement calculation.
- No patch on `DrawGroupFrame`, pawn tick, vehicles, pathfinding, or the save format.

## Scope and validation

This is a **first runtime test build**, compiled via GitHub Actions, not yet verified inside user's full mod stack. In `Player.log`, look for `[VMFContextFilter] v1.0 active` and `Hidden colony vehicle pocket-map`. If the white box remains and no map was filtered, this indicates it is not produced by VMF's surviving pocket-map list; the next fix should inspect group frame construction rather than broadening this patch speculatively.

## Installation

Extract ready-to-install ZIP into `RimWorld/Mods` to create `Allen.VMFColonistBarContext`. Enable after Harmony, Vehicle Framework, VMF and CBA. Restart game. Test (1) cart spawned on colony, (2) actual world caravan, (3) returned to colony. Remove mod folder to revert.
