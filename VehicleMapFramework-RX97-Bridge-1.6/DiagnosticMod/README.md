# RX97 Bridge Reachability Diagnostics v1.1 (RW 1.6.4871)

**Purpose:** read-only diagnostic tool built using public `Krafs.Rimworld.Ref 1.6.4871`. This is **NOT** a fix; it does not mutate Vehicle Framework, Vehicle Map Framework, S.D. Bridges, RX97 Carts, save data, or path costs.

## Get the game-ready DLL

Use the verified successful v1.1 GitHub Actions run at:
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
3. Enable the mod, fully restart RimWorld. The log should contain **`[RX97Diag] v1.1 mod loaded from ...`**, independently confirming the DLL was loaded.
4. Select the actual `RX97_HorseCart`, ensure driver physically occupies driver role, test ground move, then raise/lower the bridge and attempt crossing **without reloading**.
5. **Primary diagnostic button:** Options → Mod Settings → **RX97 Bridge Diagnostics** → **Dump vehicle region owners**. It does NOT require Developer Mode. The older Developer Mode Debug actions entry is optional only.
6. The patched VF right-click movement check logs `[RX97Diag] Horse Cart movement REJECTED` for up to 30 rejected moves; **the first rejection automatically dumps a complete snapshot** with `[RX97Diag] Snapshot BEGIN` through `END`.
7. Share `Player.log` covering the actual failure, even if the log contains no `[RX97Diag]` markers. Never save over the only good save.

If movement fails with **no REJECTED line**, it may be after the move acceptance stage (pather/path generation) or may take another order path. This result is also diagnostic: report it; do not assume the code works simply because it compiled.

## Safety and scope

The DLL registers through a public RimWorld `Mod` class and offers a **Mod Settings button** to avoid relying on RimWorld's Debug Action discovery. It hooks only the VF `FloatMenuOptionProvider_OrderVehicle.VehicleCanGoto` **postfix**, reads `AcceptanceReport`, and logs rejected Horse Cart orders; the first rejection triggers a one-time read-only snapshot. Runtime errors in this hook and the manually invoked snapshot are caught and logged. The Harmony bootstrap does not touch actual pathing outcomes. It may require adaptation to another installed VF version; compilation alone is not in-game validation.

The diagnostic is separate from the eventual narrow `RX97 Carts / VMF / VF` compatibility fix.
