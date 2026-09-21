# Medieval Overhaul - Recycle Only 1.5

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## What it does
- Adds three bills to MO's existing mending bench (DankPyon_MendingBench):
  - Recycle apparel
  - Recycle armor
  - Recycle weapon
- Does NOT add a new repair/mending system.
- Does NOT add a new workbench.
- Does NOT modify MO's existing mending recipes.
- Uses the mending bench's existing CompRefuelable, so recycling consumes MO repair tools (DankPyon_RepairTools) while work is performed.

## Work amount
Medieval Overhaul mending uses 30 work per missing HP.

Recycling uses a normalized durability-based work amount:
  600 work x current durability fraction

Examples:
- 100% durability -> 600 base work
- 50% durability -> 300 base work
- 10% durability -> 60 base work
- Minimum -> 30 base work

This avoids very high-MaxHP weapons/armor taking disproportionately long to recycle, while preserving the intended rule that lower durability is faster.

Repair-tool fuel consumption per active work tick remains exactly the mending bench's native rate, so total repair-tool usage also drops as durability falls.

## Material return
Default:
- 100% durability -> up to 50% of original non-intricate materials
- 50% durability -> up to 25%
- 10% durability -> up to 5%

Formula:
  recovered fraction = max recovery * current HP / max HP

The maximum recovery and durability scaling can be changed in Mod Settings.

Intricate materials/components are excluded by default to avoid recycling loops.

## Work / research
- Work skill: Crafting
- Bill giver work type: Smithing, matching MO's mending bench WorkGiver
- Research prerequisites follow MO's own mending recipes:
  - Apparel: DankPyon_Tailoring
  - Armor/Weapons: Smithing
