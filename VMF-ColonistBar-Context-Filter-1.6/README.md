# VMF Colonist Bar Context Filter v1.2

## Confirmed user runtime evidence
- v1.0: VMF ExcludeVehicleMaps helper patched, never called.
- v1.1: direct Find.Maps call-site transpiler **compiled and installed with `IL map-site matched=True`**, but no `vanilla recache map filter invoked` in user `20261017-161959` log. Empty frame remains.
- RimWorld 1.6 ColonistBar.CheckRecacheEntries adds `new Entry(null, map, num)` when a map has no free colonists.
- Colonist Bar Adjuster mainly patches DrawGroupFrame and layout calculation; the source does not itself generate the blank entry.

## v1.2 design
Patches **ColonistBarDrawLocsFinder.CalculateDrawLocs(List<Vector2>, out float, int)** *Prefix*, invoked immediately before layout computation. This is independent of both the optional VMF transpiler and the Find.Maps injection.

- Examine the already-created `ColonistBar.cachedEntries`.
- Remove **only** `Entry.pawn == null` and `Entry.map.Parent` inheriting `VehicleMapFramework.MapParent_Vehicle`, and whose owner vehicle is `Spawned` ultimately on a player home map.
- Never remove entries with real pawns or entries for actual world-map caravans (which have map=null, or vehicle is unspawned).
- Re-compact entry group numbers using RimWorld's own `ColonistBar.Entry` constructor, so group reorder/click callbacks use correct group IDs.
- Update `cachedReorderableGroups` and `groupsCount` before the layout is calculated.
- Request one bar recache on startup after the map is loaded.
- No render-loop patches or vehicle/pathfinding/save-state modifications.

## Expected logging
`[VMFContextFilter] v1.2 active`, `LAYOUT hook invoked`, `empty entry group=...`, then if matched `REMOVED blank colony vehicle group`. The first sample logs all empty map groups for source tracing. If the match fails, don't broaden blindly: inspect actual `parentType`/group.

## Compatibility & risk
Compiled in GitHub Actions against Krafs RimWorld 1.6 references; not yet runtime verified in user's mod stack. Back up save; remove the independent mod folder to revert. Exact uploaded mods: VMF 1.6.673, CBA 2673413930. Source branch `fix/vmf-colonistbar-context-rw16`.
