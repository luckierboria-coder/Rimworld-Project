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
Actual work is controlled directly at RimWorld's Bill.GetWorkAmount(thing) call used by JobDriver_DoBill.

Default:
- 100% durability -> 120 base work
- 50% durability -> 60 base work
- 25% durability or below -> 30 base work minimum

The full-durability value is configurable in Mod Settings from 30 to 600.

This is intentionally much faster than v1.1/v1.2. It no longer scales with absolute HP/MaxHP, and actual workLeft is overridden directly rather than relying only on RecipeDef.WorkAmountTotal.

For reference, Medieval Overhaul mending is 30 work per missing HP. Thus default full-durability recycling is equivalent to repairing only 4 HP; 50% durability is equivalent to repairing 2 HP.

Repair-tool fuel consumption per active work tick remains the mending bench's native rate, so total fuel use scales with the shortened work time.

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
