# Vehicle Map Framework × RX97 Horse Cart × S.D. Bridges (RimWorld 1.6)

**Status (2026-10-17): RX97 Carts is a necessary trigger in user's A/B test; VMF v1.6.673 alone works. Root cause not yet proved; no patch shipped.**

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

## Read-only instrumented diagnostics built in GitHub Actions

- Source and usage: [DiagnosticMod/README.md](DiagnosticMod/README.md)
- Source entrypoint: [DiagnosticMod/Source/BridgeDiagnostics.cs](DiagnosticMod/Source/BridgeDiagnostics.cs)
- GitHub Actions workflow: [.github/workflows/rx97-bridge-diagnostic.yml](../../.github/workflows/rx97-bridge-diagnostic.yml)
- Successful RW 1.6.4871 public-reference compilation and artifact staging:
  https://github.com/luckierboria-coder/Rimworld-Project/actions/runs/39035428160
- Name: `RX97BridgeDiagnostics-RW16`. Its DLL is **diagnostic only**, not a released compatibility fix and not game-tested.
- One read-only Harmony postfix on `Vehicles.FloatMenuOptionProvider_OrderVehicle.VehicleCanGoto`, filtered by `RX97_HorseCart`, prints rejects only (up to 30). One debug action `RX97 Bridge > Dump vehicle region owners` prints group owners, region dirty flags, presence of RX97 carts, vehicle mods. No pathing effects.
- An install-ready ZIP was also packaged into this conversation with correct `Allen.RX97BridgeDiagnostics/About/About.xml` and `1.6/Assemblies/*.dll` layout and its ZIP CRC verified. No compiled DLL pushed into repository.
- Required follow-up: user's actual runtime `[RX97Diag]` Player.log, then produce root-cause-specific compatibility patch.

## Actual user-supplied RX97 Carts archive — forensic audit (2026-10-17)

Source: user upload `3797810085.zip` (2,004,982 bytes), 239 ZIP entries including a `.git/` directory. Inspected actual byte content, not upstream public `main` defaults. This is an **XML + textures** mod, **no compiled DLL**.

### Actual RW 1.6 contents

- `About/About.xml`: `RX97.MapVehiclesCarts`, `supportedVersions: 1.6`; dependencies `brrainz.harmony`, `SmashPhil.VehicleFramework`, `OELS.VehicleMapFramework`, load after the same.
- `About/LoadFolders.xml`: version 1.6 loads `1.6/` only.
- `1.6/Defs/VehicleDefs/Animal Cart/Animal_Cart_VehiclePawn.xml`: **one** vehicle def, `RX97_Animal_Cart`, parent `BaseVehiclePawnWithMap`, external `size=(3,7)`, `MoveSpeed=3`, `customTerrainCosts DisallowTerrainWithTag="Water"`; interior `VehicleMapFramework.VehicleMapProps` of `size=(2,3)`; hay fueled; correctly uses current VMF 1.6 namespace.
- `1.6/Defs/VehicleDefs/Animal Cart/Animal_Cart_Buildable.xml`: same external size `(3,7)`, build creates this def.
- `1.6/Defs/UpgradeTrees/AnimalCart_UpgradeTree.xml`: RX97_AnimalCart_UpgradeTree, optional canopy.
- XML Patches: `Early_Vehicle_Parts.xml`, `Early_Vehicle_Parts_Research.xml`, `Medieval_Overhaul.xml`, `RX97_Animal_Roles.xml`, and `Simply_Wheel_Barrow_(Vehicle)_Research.xml`. All statically inspected XML are parseable. These target **the Animal Cart blueprint/vehicle** and the RX97 trade cart research; **none directly targets `RX97_HorseCart` or S.D. Bridges**.
- `RX97_Animal_Roles.xml` operates **only if mod named `RX97 Animal Roles` is active**, adds draft animal roles and removes Animal Cart `CompProperties_FueledTravel` and horse fuel-leak reactor. This is a separate compatibility dimension requiring the actual user's active Mod list.
- No source-provided Harmony/C# assemblies in this archive; any cross-effect on Cammel is therefore more likely from **VF/VMF global initialization/pathing**, mod interactions, existing cart instances, or external compatibility patches, rather than a direct RX97 Carts Harmony patch.
- The ZIP embeds Git metadata pointing at upstream `Rangelx97/RX97_Vehicle_Map_Carts` and `main` commit `6b15171590836ed3abdef06485d9f692662c408e`. The archive's 1.6 tree **differs materially** from the public GitHub main tree, which still lists 1.5 and an Animal Cart `size=(4,8)`. The previous old-API hypothesis is **disproved for this uploaded 1.6 version**.

### Region-owner hypothesis decisively narrowed

In **VF 1.6** `MapGridOwners.PathConfig`, region sharing requires same `size = min(vehicleDef.Size.x, vehicleDef.Size.z)` **and** same default impassability and impassable thing/terrain sets. Actual RX97 Animal Cart `(3,7)` yields size **3**; Cammel Horse Cart `(4,7)` yields **4**. Thus these **two specific defs cannot directly share a region owner**. Do not produce a fictitious grid-owner isolation patch between them. Adding Animal Cart can still perturb global init/other owner groups, which remains unproven.

### The next discriminating runtime check

Using a **copy of the save**, keep RX97 Carts and VMF enabled, but test whether horse-cart blockage occurs **before any RX97 Animal Cart instance / interior map is spawned**. If only after an Animal Cart map is instantiated, prioritize VMF map creation and base-map propagation, pathfinding dirtier invalidation and conflicts with existing bridge workarounds. If immediately on loading the extra VehicleDef, inspect VF map path-owner initialization, total owner count/dirty-rebuild scheduling and third-party Harmony patches. Also distinguish whether `RX97 Animal Roles` and the prior `Allen.RX97HerbivoreFuel16` mod are active, as those conditionally alter vehicle state.

Never test destructive changes on the only save; do not change `thingClass` or interior `VehicleMapProps` in a live save holding Animal Cart interior pawns/items.

**Status**: user's ablation isolates RX97 Carts; source audit is complete; actual runtime exception stage / root code path and fix **not yet confirmed**, no DLL patch built or claimed to be fixed.

## Confirmed differential isolation (2026-10-17, user tested)

**The critical A/B test is now complete:** retaining the updated Vehicle Map Framework while disabling **only RX97 Carts** restores `RX97_HorseCart` movement. The user also reports failure with RX97 Carts enabled.

| Runtime condition | RX97 HorseCart through drawbridge |
|---|---|
| VF + Cammel + S.D. Bridges + VMF v1.6.673; **RX97 Carts disabled** | PASS (user report) |
| Same plus **RX97 Carts enabled** | FAIL (user report) |

This supersedes the earlier unclear combined VMF+RX97 Carts removal test. It **does not** prove whether the root lies in RX97 Carts defs, an old or duplicated asset/DLL, or a shared pathing algorithm triggered by adding the vehicle defs. Do not ask the user to repeat this differential test.

### Author's publicly available content repositories (discovered now)

- RX97 Carts: https://github.com/Rangelx97/RX97_Vehicle_Map_Carts
  - `About/About.xml` declares `RX97.MapVehiclesCarts` and **supportedVersions 1.5 only**. Last visible main commit 2025-06-15, no 1.6 branch.
  - `Defs/VehicleDefs/Animal Cart/Animal_Cart_VehiclePawn.xml`: `BaseVehiclePawnWithMap`, `RX97_Animal_Cart`, visible external `size=(4,8)`, interior `size=(2,3)`, XML `Class="VehicleInteriors.VehicleMapProps"` and `VehicleInteriors.VehicleHumanPowered`. This is 1.5-era naming. Chariot (`2x5`) and Hand Cart (`2x3`) also inherit BaseVehiclePawnWithMap.
- Cammel: https://github.com/Rangelx97/RX97Rwcart
  - `yackcart/1.5/Defs/VehicleDefs/HorseCart/HorseCart_VehiclePawn.xml`: `BaseVehiclePawn`, `RX97_HorseCart`, `size=(4,7)`.
- VMF v1.6.673: https://github.com/oelsart/VehicleMapFramework/blob/1.6/1.6/Defs/VehicleDefs/BaseVehiclePawnWithMap.xml loads `VehicleMapFramework.VehiclePawnWithMap` and source `VehicleMapFramework.VehicleMapProps` exists at `Source/VehicleMapFramework/VehicleMap/VehicleMapProps.cs`.
- VF 1.6: https://github.com/SmashPhil/Vehicle-Framework/blob/v1.6.2144/Source/Vehicles/Pathing/MapGridOwners.cs compares `min(Size.x, Size.z)`, default impassable flags, and *impassable terrain and thing sets* to group region owners. The criterion does **not** by itself establish whether these exact defs share one owner. In fact RX97 Carts public XML disallows terrain tagged Water, whereas Cammel public HorseCart XML does not explicitly do that, potentially preventing a match. **Do not claim shared grids as fact.**
- Crucial historical discrepancy: older game-side diagnostic described `RX97_Animal_Cart` size **3×7**, while current public Carts source says **4×8**. This confirms public source is not a verified byte-for-byte substitute for the user's actual installed 1.6 content.

### Engineering implications

1. Scope root-cause investigation around presence of `RX97.MapVehiclesCarts` while VMF remains installed, not VMF alone.
2. Do **not** deploy a guessed `MatchesReachability` Harmony patch until comparing the **loaded 1.6 XML defs**, patch operations, game log class resolution, and the actual group owner IDs at runtime.
3. Audit any `VehicleInteriors.*` class references in the *installed* Carts files; the author's public source uses 1.5 type names despite 1.6 VMF changes. Distinguish from Steam build, which may have been updated independently.
4. A minimal targeted diagnostic should report `RX97_HorseCart` and `RX97_Animal_Cart` exact final def sizes, flags, impassable sets, region owner IDs, `VehicleReachability` result, and whether movement gets rejected before/after path generation. Instrument on one explicit player command, never on each tick.
5. Preserve Cammel and S.D. Bridges unmodified. Prefer a narrowly targeted `RX97 Carts × VF 1.6` compatibility module only after the above confirmation; the first safe action is obtaining/inspecting the **actual RX97 Carts 1.6 mod content** if not already accessible.

**No compiled patch or game runtime validation in this iteration.** The old source snapshot is only a lead, not conclusive proof of the loaded XML.

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
