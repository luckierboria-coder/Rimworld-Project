# Drafted Command Priority

RimWorld 1.5 combat-control micro-mod.

## Behavior

When a player-controlled pawn is drafted and its current job has `playerForced=true`, ordinary incoming jobs with `playerForced=false` are rejected at `Pawn_JobTracker.StartJob`.

This is intended to stop autonomous reactions such as fire/flee/ordinary AI from stealing a command the player just issued.

The guard ends automatically when the forced job ends or the pawn is undrafted. Downed pawns and pawns in a mental state are not locked by this patch. New explicit player orders are always allowed to replace the previous one.

## Safety

- No ThinkTree replacement.
- No JobDef deletion.
- No fire mechanics changes.
- No changes to RimMT/PUAH/Butter++.
- Can be disabled instantly in Mod Settings.

V0.1 is deliberately small so combat behavior can be validated before adding per-pawn gizmos or finer exception lists.
