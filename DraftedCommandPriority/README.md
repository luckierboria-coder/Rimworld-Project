# Drafted Command Priority

RimWorld 1.5 combat-control micro-mod.

## Strict drafted command priority

When a player-controlled pawn is drafted and its current job has `playerForced=true`, ordinary incoming jobs with `playerForced=false` are rejected at `Pawn_JobTracker.StartJob`.

This is intended to stop autonomous reactions such as fire/flee/ordinary AI from stealing a command the player just issued.

The guard ends automatically when the forced job ends or the pawn is undrafted. Downed pawns and pawns in a mental state are not locked by this patch. New explicit player orders are always allowed to replace the previous one.

## Melee Pawn Auto Attack

Optional setting for drafted player-controlled pawns holding a melee weapon.

When no explicit `playerForced` order is active, the pawn periodically scans for the nearest hostile pawn inside the configured radius and starts `AttackMelee` automatically.

- toggleable in Mod Settings
- radius: 1-20 cells
- default radius: 4 cells
- scans once every 15 ticks per pawn, staggered by pawn id
- cheap geometric scan first, then at most one reachability test for the selected target
- never overrides an explicit player-forced order

## Safety

- No ThinkTree replacement.
- No JobDef deletion.
- No fire mechanics changes.
- No changes to RimMT/PUAH/Butter++.
- Can be disabled instantly in Mod Settings.

V0.1 is deliberately small so combat behavior can be validated before adding per-pawn gizmos or finer exception lists.

<!-- CI retrigger: 2026-08-26 -->
