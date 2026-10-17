# Vehicle Map Framework × RX97 Horse Cart × S.D. Bridges (RimWorld 1.6)

**Status (2026-10-17): REGRESSION STILL REPRODUCES WITH VMF v1.6.673 (user-reported); NOT FIXED / NOT RUNTIME-VERIFIED BY US.**

This folder tracks an apparent compatibility problem where the regular `RX97_HorseCart` from **RX97 Medieval Vehicle: Cammel** fails to cross a drawbridge after an up/down transition, even when the drawbridge appears lowered. This is separate from the Vehicle Map Framework colonist-bar grouping issue.

## Reproduction and evidence from the previous conversation

- Runtime: RimWorld **1.6.4871**.
- Failing vehicle: `RX97_HorseCart` (RX97 Medieval Vehicle: Cammel, footprint **4×7**).
- Comparison vehicle: `RX97_Animal_Cart` (RX97 Carts, footprint **3×7**).
- A newly constructed/lowered bridge is initially traversable; raising then lowering it can leave Horse Cart unable to pass; replacing bridge with terrain can also fail until save/reload.
- Reloading the same incident save restored passage, suggesting transient runtime reachability/region state instead of permanent terrain obstruction.
- Earlier diagnostics found **both cached and immediately calculated cell path costs passable** after lowering the bridge. This rules out a simple stale per-cell cost as the sole explanation, not a stale higher-level reachability graph.
- After removing **Vehicle Map Framework AND RX97 Carts together**, Horse Cart could cross. This is an A/B observation against the pair, not a separate isolation of either mod. User now reports determining Vehicle Map Framework is responsible. Do not reinterpret this as proving the exact method.
- A previous save had a *driver assignment but an empty occupied driver seat*. Treat vehicle operator presence as an independent test precondition: first verify the cart can move on the same side of the bridge.
- Earlier attempts did not establish a stable runtime fix; do not blindly layer another region refresh patch.

## Previous local experimental modifications (historical records; current installation NOT verified)

The archive identifies the following files in a Windows game mod directory:

- `S.D. Bridges/1.6/Assemblies/SDBridgesVehiclePathRefresh16.dll`
- `S.D. Bridges/1.6/Assemblies/SDBridgesDeferredRegionRefresh16.dll`
- `S.D. Bridges/1.6/Assemblies/SDBridgesBridgeCellRegionRefresh16.dll`
- `S.D. Bridges/1.6/Assemblies/SDBridgesVehiclePathDiagnostics16.dll` (read-only diagnostic)
- `RX97 Medieval Vehicle: Cammel/1.6/Patches/AllenCammelBridgeRegionCosts.xml`
- S.D. Bridges `sd_bridges_ThingDefs_drawbridge.xml` had experimental `pathCost=10000` changes for raised states.

These paths and modifications are history, **not evidence of currently installed state**. Verify/backup current files before removing/replacing any one of them. Avoid removing unrelated custom draw/texture/fuel patches.

## Upstream source audit

Authoritative upstream:

- Vehicle Map Framework: https://github.com/oelsart/VehicleMapFramework (branch `1.6`)
- Vehicle Framework: https://github.com/SmashPhil/Vehicle-Framework
- Author's discussion of vehicle reachability/region cache going out of sync, with reload rebuilding regions: https://github.com/SmashPhil/Vehicle-Framework/issues/125

Specific VMF source points to inspect (source at 2026-10-17, commit `77c898584`):

1. `Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_VehicleFramework.cs`: patch of `GenGridVehicles.ImpassableForVehicles` (ramps), `FloatMenuOptionProvider_OrderVehicle.VehicleCanGoto` and `PawnGotoAction` (intercepts cross-map vehicle destinations), and `MapGridOwners.PathConfig.MatchesReachability` (isolates `VehicleMapProps_Unique` placeholder configs).
2. `Source/VehicleMapFramework/Utilities/CrossMapReachabilityUtility.cs`: `CanReachVehicle` uses original VF reachability for *same map*, and custom transfer logic only for distinct maps. Do **not** assume its cross-map code is exercised by an ordinary Horse Cart crossing a bridge on a single map.
3. `Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Map.cs`: `Reachability.CanReach` prefix normally defers to the original implementation when departure, destination and current map are identical.
4. VMF has a configurable dynamic patch level; lowering this globally may break VMF's normal mapped-vehicle features. Do not treat changing levels as a proven fix.

**No exact culpable Harmony method or cache mutation has yet been demonstrated.** Treat VMF as a confirmed problem *component* per user observation, not a demonstrated line-level root cause.

## New regression report — 2026-10-17

User installed latest Vehicle Map Framework after the first source audit and reported **the same drawbridge regression persists**. Therefore upgrading to `v1.6.673` is *not* a verified workaround.

**Crucial unresolved isolation:** The historical test disabled VMF **and** RX97 Carts simultaneously. In the new test, it is not established whether RX97 Carts was independently disabled, whether an actually boarded operator could move the Horse Cart on the same side, or whether any of the old bridge refresh DLLs were active. These are concrete confounders, not grounds to deny the user reproduction.

Minimal *next* A/B case: VMF `v1.6.673` ON + Cammel `RX97_HorseCart` ON + S.D. Bridges ON, RX97 Carts OFF, with operator already **physically occupying the driver seat**. Use a copy of the current save; do not save over the original with missing mod state. Move the cart one tile before the bridge on the same side (establish driver/motion), then lower / raise / lower and cross WITHOUT reload. This single contrast separates VMF-only regression from RX97-Carts-dependent load-order or def-group effects.

Current upstream source review:
- `Source/VehicleMapFramework/Utilities/CrossMapReachabilityUtility.cs` explicitly delegates **same-map** `CanReachVehicle` to VF `VehicleReachability`; do not assume cross-map algorithm is at fault.
- `Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_Map.cs` `Reachability.CanReach` prefix generally delegates normal same-map checks to vanilla.
- `Source/VehicleMapFramework/VMF_HarmonyPatches/Patches_VehicleFramework.cs` changes `MapGridOwners.PathConfig.MatchesReachability` for `VehicleMapProps_Unique` placeholder defs; VF's `MapGridOwners` shares `RegionData` between equivalent def groups. A changed group owner is a **candidate** to instrument, not a confirmed cause.
- VF `VehicleRegionDirtyer.NotifyWalkabilityChanged` does dirty adjacent **no-region** cells as well as existing regions in upstream develop code; previous simplistic claim 'it only dirties cells already having regions' is NOT valid as a general description of this source version.
- Earlier in-game diagnostic recorded both cached and immediate cell costs passable, but did not capture the exact rejection stage (operator check / float menu / region reachability / path generation).
- No updated live Player.log / exact installed DLL hashes have been made available during this continuation. Avoid shipping a speculative fix.

Next diagnostic artifact once isolated: conditional one-shot logging at `FloatMenuOptionProvider_OrderVehicle.VehicleCanGoto`, original `VehicleReachability.CanReachVehicle`, and final `VehiclePathFinder` for a single `RX97_HorseCart` crossing attempt, plus `VehiclePathingSystem.GridOwners.GetOwner(vehicleDef)` and region dirty state. Avoid per-tick logs or changing pathfinding during measurement.

## Upstream updates / best next action

Official upstream GitHub releases:

- `v1.6.562`, 2026-09-14: https://github.com/oelsart/VehicleMapFramework/releases/tag/v1.6.562
- `v1.6.673`, **2026-10-17**: https://github.com/oelsart/VehicleMapFramework/releases/tag/v1.6.673

The comparison contains **118 commits** and edits to cross-map traversals, reachability and VF integration. It has NOT been shown to fix this drawbridge failure. The 2026-10-12 commit `bbc3d9fb3` invalidates `CrossMapReachabilityCache` on *mapped vehicle* AllowEnter/AllowExit toggles; it should **not** be misrepresented as directly repairing S.D. Bridges.

Recommended controlled matrix on **copied saves / cloned mod config** (no important-save overwrites):

| Run | Mods | Expectation / diagnostic intent |
| --- | --- | --- |
| A | Vehicle Framework + Cammel + S.D. Bridges, WITHOUT Vehicle Map Framework / RX97 Carts | Historical working baseline |
| B | Add Vehicle Map Framework `v1.6.673`, RX97 Carts still OFF | Re-test isolated VMF effect after official update |
| C | Add RX97 Carts after B | Detect whether map-vehicle population or this content mod is a second factor |

Before runs, remove/disable only **experimental bridge-region workarounds** (after backups, with original S.D. Bridges and Cammel XML restored to a known matched version), to isolate current upstream behavior.

Reproduction within each run: driver **actually boarded**, cart moves one tile on same side, fresh bridge down (crossable), raise → lower → allow at least 300 unpaused ticks → attempt passage, no reload; save a separate incident snapshot and capture `Player.log` *before* reloading. Record direction, vehicle position/rotation, bridge cells, and whether a *reachability precheck* rejects movement or *path generation* finds no path.

## Criteria for implementation

Only when B still fails and a stack trace or targeted instrumentation identifies the exact interface:

- Prefer **narrow VMF–VF compatibility guard** for plain `VehiclePawn`/specifically `RX97_HorseCart` and single-map move orders, leaving `VehiclePawnWithMap` behavior intact.
- Distinguish: bad float-menu target-map routing vs false `VehicleReachability` vs path generation vs operator/occupancy issues.
- Avoid global path-cache clearing each tick and avoid speculative blocking values on bridge XML.
- Build via the user's connected **GitHub** setup, not a new local compilation environment.
- Keep source/configs/backup notes, and do not call static compilation a successful game-runtime fix.

## Environment availability

Only historical chat XML and public GitHub repositories were accessible when writing this audit. The actual Windows `D:\Rimworld\...` installation, live `Player.log`, current ModsConfig and saves were **not accessed or modified** during this investigation.
