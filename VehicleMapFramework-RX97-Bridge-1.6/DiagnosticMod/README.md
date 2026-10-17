# RX97 Bridge Reachability Diagnostics (RW 1.6.4871)

**Purpose:** read-only diagnostic tool built using public `Krafs.Rimworld.Ref 1.6.4871`. This is **NOT** a fix; it does not mutate Vehicle Framework, Vehicle Map Framework, S.D. Bridges, RX97 Carts, save data, or path costs.

## Get the game-ready DLL

Use the latest successful GitHub Actions run at:
https://github.com/luckierboria-coder/Rimworld-Project/actions/workflows/rx97-bridge-diagnostic.yml

Download the artifact named `RX97BridgeDiagnostics-RW16`. Extract it so the game sees:

```text
RimWorld/Mods/Allen.RX97BridgeDiagnostics/
  About/About.xml
  1.6/Assemblies/Allen.RX97BridgeDiagnostics.dll
```

Do not copy the source folder or put the DLL under RX97 Carts/S.D. Bridges.

## Reproduce and capture

1. Backup the save. Enable Vehicle Framework, VMF v1.6.673, Cammel, S.D. Bridges, **RX97 Carts**, and this diagnostic mod.
2. Fully restart the game and load the test save. Do not install the older competing bridge-refresh DLLs for this isolated test if safely removed in a separate backup.
3. Enable developer mode, select the actual `RX97_HorseCart`; ensure a driver physically occupies the driver role.
4. Attempt a short ordinary-ground move (positive control). Raise/lower the bridge and attempt the same cross-bridge movement **without reloading**.
5. The diagnostic patch logs a message beginning `[RX97Diag] Horse Cart movement REJECTED` when the VF right-click movement accept/reject method denies `RX97_HorseCart`. Only failed checks are logged (max 30).
6. In Developer Mode > Debug actions, locate category `RX97 Bridge` and click `Dump vehicle region owners` once when the crossing fails. It emits `[RX97Diag] Snapshot BEGIN` through `END`, including both vehicles' loaded sizes, `MapGridOwners` group owners, suspended/dirty/enabled status, active RX97/Vehicle mods, and present cart instances.
7. Share `Player.log` covering the actual failure and `[RX97Diag]` lines. Do not save over the only good save.

If movement fails with **no REJECTED line**, it may be after the move acceptance stage (pather/path generation) or may take another order path. This result is also diagnostic: report it; do not assume the code works simply because it compiled.

## Safety and scope

The DLL hooks only the VF `FloatMenuOptionProvider_OrderVehicle.VehicleCanGoto` **postfix**, reads `AcceptanceReport`, and logs rejected Horse Cart orders. Runtime errors in this hook and the manually invoked snapshot are caught and logged. The Harmony bootstrap does not touch actual pathing outcomes. It may require adaptation to another installed VF version; compilation alone is not in-game validation.

The diagnostic is separate from the eventual narrow `RX97 Carts / VMF / VF` compatibility fix.
