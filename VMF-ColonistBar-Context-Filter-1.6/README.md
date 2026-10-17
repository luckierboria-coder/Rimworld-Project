## Actual in-game validation (2026-10-17 16:57:38 Player log)

**Colony UI bug fixed in user's RimWorld 1.6.4871 mod stack.** User explicitly confirmed that the erroneous white/empty top-bar group is gone.

From the real log (not merely GitHub compilation):

- `[VMFContextFilter] v1.2 active`
- `LAYOUT hook invoked; entries=61 groupsCount=2`
- `empty entry group=1 map=Map-160 parentType=VehicleMapFramework.MapParent_Vehicle colonyVehicleMap=True`
- `REMOVED blank colony vehicle group=1 map=Map-160`
- `recached: entries=60 groups=1 suppressed=1`
- Subsequent recaches also consistently remove the same empty group.

**Scope of verification:** colony-side hiding confirmed. The second half of acceptance criteria (actual world-map vehicle caravan remains visible, and correct return transition) was not reported/tested by user in this message and remains to be checked.

**Other log note:** after the first group recache an undiagnosed `NullReferenceException [Ref 5ECF8F58]` is logged as a duplicate stacktrace, with no original stack trace in supplied text. Its cause cannot be attributed to this patch on this evidence; do not claim clean game log or causality.

**Mod dependency when replacing Cammel:** The separately created `RX97-Carts-1.6-Cammel-Dedup-TEST` build removes its own research defs and expressly depends on Cammel. If uninstalling Cammel, revert to the *original, unmodified* RX97 Carts (`3797810085.zip`), otherwise the dedup version's dependencies/blueprints are incomplete.

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
