# BPC AreaRestriction OOB Fix 1.5

Fixes the crash chain:

`Verse.Area.get_Item(IntVec3)`
→ `Verse.AI.Job.IsTargetOutsideArea(...)`
→ `Verse.AI.Job.AnyTargetOutsideArea(...)`
→ `Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap`
→ `BetterPawnControl.ScheduleManager.LoadState(...)`

## Why it crashes

Vanilla `Job.IsTargetOutsideArea` checks only whether `target.Cell.IsValid`, then indexes `zone[target.Cell]`.
A valid `LocalTargetInfo` can still contain a cell outside `zone.Map`, or can retain a Thing target from another map. In those cases the Area BoolGrid index is invalid and throws `IndexOutOfRangeException`.

## Patch behavior

- Invalid target: untouched; vanilla handles it.
- In-bounds target on the same map: untouched; vanilla handles it.
- Out-of-bounds target: returns `true` ("outside area") without indexing the Area grid.
- Thing target held on another map: also returns `true`.

The surrounding vanilla setter then interrupts the invalid current job normally.

No Area data is rewritten, no arbitrary exception is swallowed, and no Better Pawn Control policy data is modified.

Build target: RimWorld 1.5 / .NET Framework 4.7.2.
