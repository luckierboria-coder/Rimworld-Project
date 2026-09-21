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

## Material return
Default:
- 100% durability -> up to 50% of original non-intricate materials
- 50% durability -> up to 25%
- 10% durability -> up to 5%

Formula:
  recovered fraction = max recovery * current HP / max HP

The maximum recovery and durability scaling can be changed in Mod Settings.

Intricate materials/components are excluded by default to avoid recycling loops.

## Work
- Apparel: 1200 work
- Armor: 1500 work
- Weapons: 1500 work
- Work skill: Crafting
- Bill giver work type: Smithing, matching MO's mending bench WorkGiver
- Research prerequisites follow MO's own mending recipes:
  - Apparel: DankPyon_Tailoring
  - Armor/Weapons: Smithing
