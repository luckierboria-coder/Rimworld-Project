# BPC AreaRestriction OOB Fix 1.5

Fixes the crash chain:

`Verse.Area.get_Item(IntVec3)`
→ `Verse.AI.Job.IsTargetOutsideArea(...)`
→ `Verse.AI.Job.AnyTargetOutsideArea(...)`
→ `Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap`
→ `BetterPawnControl.ScheduleManager.LoadState(...)`

Vanilla only checks whether the target cell is valid before indexing the Area grid. This patch adds the missing map-boundary/cross-map guard.

- Invalid target: vanilla behavior.
- In-bounds same-map target: vanilla behavior.
- Out-of-bounds target: treated as outside the allowed area.
- Cross-map Thing target: treated as outside the allowed area.

No Area data is rewritten and no arbitrary exception is swallowed.
