# Uploaded 1.6 archive comparison — RX97 Carts vs RX97 Medieval Vehicle: Cammel

Date 2026-10-17. These are the **actual user-supplied archives**, not the public 1.5 GitHub snapshots.

| Input | SHA256 | Archive |
| --- | --- | --- |
| RX97 Carts | `0f76869613f19109a3bfff88a87cdd8e4d77b923d33da641a5c336ef64ddc138` | `3797810085.zip` |
| RX97 Medieval Vehicle: Cammel | `81ac6511fbf8331c21a68ca052782873fe184a55ff0232c5f5550af47ef6133b` | `3040815967.zip` |

## Definitive structural conflicts (two overlapping defs)

Both RimWorld **1.6** variants define **identical namespace/type and identical defName**:

| Type | defName | RX97 Carts source | Cammel source |
| --- | --- | --- | --- |
| ResearchProjectDef | `RX97_TradeCarts` | `1.6/Defs/ResearchDefs/ResearchProjectCart.xml` | `1.6/Defs/ResearchDefs/ResearchProjectCart.xml` |
| ResearchTabDef | `RX97_Vehicles` | `1.6/Defs/ResearchDefs/ResearchTabs.xml` | `1.6/Defs/ResearchDefs/ResearchTabs.xml` |

The ResearchTab XMLs have exactly the same content. The ResearchProject XMLs differ in spelling of two descriptive rules, but define the **same Def name**. This is a real content collision. Static comparison found **no other DefName collision** across the two `1.6/Defs` directories: RX97 Carts has 5 named defs and Cammel 8.

This **does not establish** that duplicate research definitions cause vehicle/bridge pathing to fail. Do not claim they are the root cause; there is no pathing method or bridge Def referenced in either research XML.

### Reversible isolated XML repair (test)

User received `RX97-Carts-1.6-Cammel-Dedup-TEST.zip`, SHA256:
`36a37f9b56357213e145f4ed535d0d876db33693e8556a631a70b6140a00e5f1`

Derived from the installed RX97 Carts ZIP, preserving all vehicle definitions, textures, patch files and `packageId=RX97.MapVehiclesCarts` but omitting the two duplicate 1.6 research XML files. The RX97 Carts `About/About.xml` additionally requires `RX97.Medieval.Cart.Cammel` and loads after it. When both mods enabled, the blueprint keeps referencing `RX97_TradeCarts`, now supplied by Cammel.

**Do NOT enable this together with the original RX97 Carts, as both share packageId.** Back up original mod directory and use as a replacement **only with Cammel enabled**. It is only a confirmed **research Def compatibility cleanup**, not a proven drawbridge fix.

## Actual vehicle pathing definitions

| Property | RX97 Carts: `RX97_Animal_Cart` | Cammel: `RX97_HorseCart` |
| --- | --- | --- |
| ParentName | `BaseVehiclePawnWithMap` | `BaseVehiclePawn` |
| size | `(3,7)` | `(4,7)` |
| customTerrainCosts | `DisallowTerrainWithTag="Water"` | none explicit |
| VMF mod extension | `VehicleMapFramework.VehicleMapProps`, interior `(2,3)` | none |
| customThingCosts | none explicit | populated **by custom Cammel local bridge patch** |
| Rider extension | optional `RX97 Animal Roles` patch | none in this ZIP |

Horse and Animal Cart cannot directly share the same VF PathConfig owner because VF `min(Size.x,Size.z)` differs (4 vs 3), consistent with in-game v1.2 logs (each owns its own region grid).

The uploaded Cammel ZIP includes **non-upstream/custom** file:
`1.6/Patches/AllenCammelBridgeRegionCosts.xml`; when `[sd] Bridges (Continued)` is present, it adds impassable `customThingCosts=10000` for the four `sd_bridges_*drawbridge_up` building defs to `RX97_HorseCart`, `RX97_YackCart` and `RX97_Cammel`. It also explicitly adds `Column=10000` if no prior customThingCosts exists. This file is important to the *actual pathing setup*. Its presence alone cannot explain the RX97 Carts on/off A/B because it remains in both runs.

## Verified third-party bridge lifecycle and VF behavior

Source read from `emipa606/SDBridges` main:

- `Building_sd_bridges_drawbridge_{up,down}.SpawnDrawbridge` calls `this.DestroyedOrNull()` and `GenSpawn.Spawn(new thing,...)` to **replace the building type** every toggle.
- `Building_sd_bridges_drawbridge_down.SpawnSetup` calls `SetTerrain(Position)` **and** `SetTerrain(BridgeCell)`; up setup calls `SetTerrain(Position)`. These methods replace underlying water/mud terrain with `sd_bridges_fake*` terrain. `FakeWaterBase` is `passability=PassThroughOnly`.
- Up and down buildings are `passability=Standable`, but their defs differ in size (e.g. quad up `(4,1)` vs down `(4,2)`) and Cammel's custom path cost explicitly blocks *up* buildings.
- VF `PathingHelper.RegisterRegionEffecter` registers impassable thing defs by vehicle. Spawn/despawn propagates per-owner `RecalculatePerceivedPathCostUnderRect`, dirty notifications and reachability cache clear, while `VehiclePathGrid.RecalculatePerceivedPathCostAt` dirties on changes to walkable status.
- These are precisely the mechanisms expected to change across raise/lower and possibly stay stale under mod interactions, consistent with the observed *reload fixes*; however this code inspection **does not prove which runtime notification is missing/mishandled**.

## Important unexcluded direct runtime occupancy interaction

In the user's v1.2 `Player(20261017-141537).log`, at the final snapshot:
- RX97 Animal Cart `(3,7)` center is `(82,0,145)`
- RX97 Horse Cart `(4,7)` center is `(82,0,138)`

They are **exactly seven cells apart** in z, hence their nominal length-7 north/south rectangles, if both facing north/south, abut with no free buffer cell; their *actual rotations* and bridge location were **not logged**. This is an alternative concrete interaction that disabling RX97 Carts could remove. User's A/B isolation proves **RX97 Carts enabled** is required for that saved-world reproduction, but does not distinguish the cart **Def registration** from an **instantiated RX97 Animal Cart** physically affecting VF occupancy/collision. The cart instance was present in the logged run and the Horse Cart motion from `x=82` to `x=72` on `z=138` was logged. No evidence yet identifies whether the Animal Cart physically interfered with the path or whether a bridge grid was stale.

**Conclusion:** Two real duplicate Def collisions found and cleaned in a test package; no static direct XML patch from RX97 Carts modifies Horse Cart/bridge; runtime bridge defect is *not yet proven root-caused*. A correct compatibility fix must not modify broad VF traversal logic until the next exact failure cause is established.
