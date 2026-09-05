# Processor Framework Hotfix 1.5 v0.1.3 – Reservation Safe

Independent patch mod for RimWorld 1.5.4063.

## Fixes
- Removes the unsafe positive-result cache that could reuse the same final Ingredient Thing across different pawns for up to 15 ticks.
- Uses live `ListerThings.ThingsOfDef` buckets as the ingredient index, restricted to the processor's currently enabled ingredient defs.
- Revalidates current pawn forbidden state, carry capacity, processor `SpaceLeftFor`, ingredient reservation, and reachability every lookup.
- Removes negative-result caching; newly available ingredients are visible immediately.
- Aligns WorkGiver processor reservation admission with `JobDriver_FillProcessor.TryMakePreToilReservations`: maxPawns is 1, not 10.
- Adds a final `JobOnThing` reservation guard so stale jobs are discarded before `JobDriver_FillProcessor` starts.

## Install
Replace the old `Processor Framework Hotfix 1.5` folder with this version. Load after Harmony and `[SYR] Processor Framework`.

Expected startup log:
`[Processor Framework Hotfix 1.5 v0.1.3 Reservation Safe] Active.`

Build target: RimWorld 1.5.4063 rev1072.
