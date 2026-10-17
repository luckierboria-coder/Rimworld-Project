# VMF Colonist Bar Context Filter — RimWorld 1.6 (v1.1)

## Bug in v1.0 (verified from user runtime Player log)

- `[VMFContextFilter] v1.0 active` appeared, but no `Hidden colony vehicle pocket-map` message appeared, and the empty portrait-bar group remained.
- v1.0 hooked the optional VMF helper `Patch_ColonistBar_CheckRecacheEntries.ExcludeVehicleMaps`; its transpiler is annotated VMF `PatchLevel(Level.Sensitive)` and is not always installed, so a successful Harmony patch of the helper does **not** guarantee that the helper is called.
- Decompiled **RimWorld 1.6** `ColonistBar.CheckRecacheEntries` loops through `Find.Maps` and unconditionally adds `Entry(null, map, num)` for empty-map groups (the `if (!tmpPawns.Any())` branch). This creates exactly the kind of blank group the user sees.

## v1.1 correction

- Hook **vanilla `ColonistBar.CheckRecacheEntries` directly** with a small Harmony transpiler.
- Inject our `ExcludeColonyVehicleMaps(IEnumerable<Map>)` immediately after its `Find.Maps` getter.
- Filter only `VehicleMapFramework.MapParent_Vehicle` interior maps whose owner vehicle chain is currently `Spawned` on an `IsPlayerHome` map.
- Preserve all other maps and the vanilla world's `Find.WorldObjects.Caravans` path, so the real world caravan groups remain visible.
- Never touch Colonist Bar Adjuster layout, per-frame UI rendering, pawn ticks, vehicles, saves, or pathfinding.
- `[VMFContextFilter] v1.1 ... IL map-site matched=True` means the transpiler found `Find.Maps`; `v1.1 vanilla recache map filter invoked` confirms actual execution; `v1.1 hidden colony vehicle map` confirms removal.
- Unknown owners, unspawned vehicles and non-colony vehicles always pass through unchanged.

## Limitations and next step

This is a **compile-verified first runtime test of v1.1**, not yet verified in the user's game. If the bar still appears while direct map filter is invoked, the remaining group may have a non-`MapParent_Vehicle` parent; need an entry dump distinguishing `pawn == null`, `entry.map` and `entry.group`. Do not blindly delete all `Entry(null,...)` placeholders because normal empty maps also use them.

## Installation

Extract the ZIP under `Mods` to create `Allen.VMFColonistBarContext`; remove v1.0 first. Keep Harmony, VF, VMF, Colonist Bar Adjuster and RX97 Animal Cart enabled. Fully restart RimWorld, test colony, actual world caravan and return to colony. Uninstall by deleting only the independent mod folder.
