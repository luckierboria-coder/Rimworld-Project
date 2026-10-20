# Drafted Command Priority

RimWorld 1.5 combat-control micro-mod.

## Strict drafted command priority

A player-issued drafted command starts a command gate. While that command chain is still active, ordinary autonomous AI jobs are not allowed to take over.

The gate is released only after the pawn has actually finished the player's command, has no queued player-forced orders, and has returned to true drafted idle / `Wait_Combat` (or has no current job). This prevents autonomous fire/flee/combat logic from interrupting a retreat or other explicit movement command halfway through.

New explicit player orders always remain authoritative and refresh the gate. Undrafted, downed, or mental-state pawns are not locked.

Priority is therefore:

`player command > autonomous melee attack / ordinary drafted AI`

## Melee Pawn Auto Attack

Optional feature for drafted player-controlled pawns holding a melee weapon.

- globally enabled/disabled in Mod Settings
- each drafted melee pawn gets an individual Fire-at-will-style toggle gizmo
- the gizmo uses the vanilla melee attack icon and the same hotkey slot as vanilla Fire at will
- radius: 1-20 cells
- default radius: 4 cells
- scans once every 15 ticks per pawn, staggered by pawn id
- cheap geometric scan first, then at most one reachability test for the selected target
- auto attack may start only from true drafted idle / `Wait_Combat`
- it never interrupts movement, rescue, hauling, attack, or any other already-running job
- active or queued player commands always suppress auto attack

The per-pawn toggle reuses the pawn's drafter `FireAtWill` state, so it behaves like vanilla Fire at will and resets on a fresh draft in the same way.

## Safety

- No ThinkTree replacement.
- No JobDef deletion.
- No fire mechanics changes.
- No changes to RimMT/PUAH/Butter++.
- Can be disabled instantly in Mod Settings.

V0.1 remains deliberately small so combat behavior can be validated before adding more combat AI policies.

<!-- CI retrigger: 2026-08-26 -->
