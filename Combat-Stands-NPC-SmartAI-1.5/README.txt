Combat Stands - NPC Smart AI V1.0 (RimWorld 1.5.4063)

Requires:
- Combat Stands (Seti.Combat.Stands)

Behavior:
- Non-player humanlike pawns only. Player-controlled pawns are untouched.
- Evaluates at a low frequency (once per 120 game ticks per map).
- Requires a real hostile target and a current melee attack verb/job context.
- Never activates a stance for ranged combat because every Combat Stands stance applies ShootingAccuracyPawn -10.
- Uses health, local ally/enemy pressure, distance, target health, target sharp armor, own armor and melee skill to score available stances.
- Endure: defensive/wounded pressure response.
- Swing: general healthy one-on-one offense.
- Berserker: chase/finish behavior when healthy and not outnumbered.
- Precise: armored/tough target response.
- Tower: emergency hold-ground response when badly wounded or heavily pressured.
- Fencer: high-skill mobility/precision response.
- Does not replace ThinkTrees, jobs, pathfinding, reservations, combat verbs or ability cooldown logic.
- Final cast remains governed by vanilla Ability.CanQueueCast / QueueCastingJob and Combat Stands' own comps.

Diagnostics:
- Startup emits one [Combat Stands NPC Smart AI] line.
- The first 80 actual stance choices per map are logged with score/HP/target/distance/local battle counts to make validation easy without permanent log spam.

Install:
1. Keep Combat Stands enabled.
2. Put this mod below Combat Stands.
3. Restart RimWorld.

Remove:
Safe to remove; this mod adds no persistent custom save data.
