# Medieval Overhaul - Recycle Only 1.5 v1.4

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## Core integration
This version intentionally follows Medieval Overhaul's own mending implementation.

MO's mending RecipeDefs use:
  workAmount = 30

MO's JobDriver_DoMending then calculates:
  mending work = 30 x missing HP

Recycle recipes also use:
  workAmount = 30

Because MO's WorkGiver_DoMending and JobDriver_DoMending automatically handle every bill on the mending bench, this mod adjusts only RecipeDef.WorkAmountTotal for the three recycle recipes so MO's existing multiplication resolves to:
  recycle work = 30 x current HP

Examples for an item with MaxHP 100:
- 90 HP: recycle 2700 work; mend 300 work
- 50 HP: recycle 1500 work; mend 1500 work
- 20 HP: recycle 600 work; mend 2400 work
- 10 HP: recycle 300 work; mend 2700 work

Thus lower-durability items are faster to recycle, while using exactly the same 30-work-per-HP scale as MO mending.

MO's mending WorkGiver selects damaged items only (HitPoints < MaxHitPoints). v1.4 intentionally preserves that behavior instead of replacing MO's WorkGiver.

## Fuel
The job remains DankPyon_DoBillMending on DankPyon_MendingBench, so MO's native repair-tool fuel consumption, work effects, sound and bill handling remain in control.

## Material return
Default:
- 100% durability -> up to 50% of original non-intricate materials
- 50% durability -> up to 25%
- 10% durability -> up to 5%

Formula:
  recovered fraction = max recovery x current HP / max HP

The maximum recovery and durability scaling can be changed in Mod Settings.
Intricate materials/components are excluded by default.
