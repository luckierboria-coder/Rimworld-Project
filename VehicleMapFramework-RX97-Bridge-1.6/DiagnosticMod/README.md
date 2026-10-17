# RX97 Bridge Reachability Diagnostics v1.2 (RW 1.6.4871)

**Purpose:** read-only diagnostic tool built using public `Krafs.Rimworld.Ref 1.6.4871`. This is **NOT** a fix; it does not mutate Vehicle Framework, Vehicle Map Framework, S.D. Bridges, RX97 Carts, save data, or path costs.

## Get the game-ready DLL

Use the verified successful v1.2 GitHub Actions run at:
https://github.com/luckierboria-coder/Rimworld-Project/actions/runs/39037710552

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
3. Enable the mod, fully restart RimWorld. The log should contain **`[RX97Diag] v1.2 mod loaded from ...`**, independently confirming the DLL was loaded.
4. Select the actual `RX97_HorseCart`, ensure driver physically occupies driver role, test ground move, then raise/lower the bridge and attempt crossing **without reloading**.
5. **Primary diagnostic button:** Options → Mod Settings → **RX97 Bridge Diagnostics** → **Dump vehicle region owners**. It does NOT require Developer Mode. The older Developer Mode Debug actions entry is optional only.
6. The patched VF right-click movement check logs `[RX97Diag] Horse Cart VehicleCanGoto ACCEPTED` / `REJECTED` (capped), with further hooks on `VehiclePathFollower.StartPath` and `VehiclePathFollower.PatherFailed`. The first rejected command or pather failure automatically dumps a complete snapshot with `[RX97Diag] Snapshot BEGIN` through `END`.
7. Share `Player.log` covering the actual failure, even if the log contains no `[RX97Diag]` markers. Never save over the only good save.

If movement fails with **no REJECTED line**, it may be after the move acceptance stage (pather/path generation) or may take another order path. This result is also diagnostic: report it; do not assume the code works simply because it compiled.

## Safety and scope

The DLL registers through a public RimWorld `Mod` class and offers a **Mod Settings button** to avoid relying on RimWorld's Debug Action discovery. It hooks only the VF `FloatMenuOptionProvider_OrderVehicle.VehicleCanGoto` **postfix**, reads `AcceptanceReport`, and logs rejected Horse Cart orders; the first rejection triggers a one-time read-only snapshot. Runtime errors in this hook and the manually invoked snapshot are caught and logged. The Harmony bootstrap does not touch actual pathing outcomes. It may require adaptation to another installed VF version; compilation alone is not in-game validation.

The diagnostic is separate from the eventual narrow `RX97 Carts / VMF / VF` compatibility fix.


## Runtime findings from user Player.log (2026-10-17 13:55:44)

- RW **1.6.4871**. v1.1 loaded and initialized successfully at Player.log lines 484-485. Two user-triggered Mod Settings snapshots completed (3594-3610, 3615-3631).
- `RX97_HorseCart`: def size `(4,7)`, region owner `RX97_HorseCart`, suspended false, dirty false, grid enabled true.
- `RX97_Animal_Cart`: def size `(3,7)`, region owner `RX97_Animal_Cart`, suspended false, dirty false, grid enabled true.
- Both cart objects exist on base map. Horse Cart moves from `(72,0,138)` to `(72,0,139)` between snapshots, but no evidence it traversed bridge.
- v1.1 log contains **no `[RX97Diag] ... REJECTED`** entries. This cannot prove its path wasn't blocked; original hook was only the command `VehicleCanGoto` reject.
- Loaded relevant mods: VF, VMF, RX97 Carts, RX97 Medieval Vehicle: Cammel, RX97 Herbivore Fuel, and Vehicle Dubs Needs Fix. External effects not proven.
- Independent AllowTool `HashSet` enumeration exception at lines 3309-3318 (and unrelated XML issues) should be audited separately, **not attributed to this bridge bug without evidence**.
- The v1.2 diagnostics now also log command acceptance, `VehiclePathFollower.StartPath`, and `PatherFailed`; none of these methods changes pathing decisions. Built by GitHub Actions `39038837942` using RimWorld 1.6.4871 public references.

### What to request next

Install `v1.2` in place of the old diagnostic mod, load a **copy** of the existing test save with all relevant mods enabled; attempt the *actual failing bridge crossing* once, avoid reloading, dump snapshot in Mod Settings, provide the *new* `Player.log`. Do not request old files again.
