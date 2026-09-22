# Medieval Overhaul - Recycle Only 1.5 v1.7

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## Fixed recycle time
Every recycle job now takes exactly:

  5000 game ticks = 2 in-game hours

This applies to:
- Recycle apparel
- Recycle armor
- Recycle weapon

Duration is independent of:
- current durability
- maximum HP
- material
- item type
- pawn crafting speed
- workbench speed

Medieval Overhaul's own mending recipes are unchanged.

## Full-durability items
Recycle bills still accept 100% durability items.
MO's original mending bills still require damaged items.

## Completion behavior
Recycle completion:
- does NOT run MO's hpHeal code
- destroys/consumes the original item
- calculates returned materials
- spawns returned materials near the worker
- completes the bill normally

## Material return
Time is fixed, but material return still follows the existing recovery settings.

Default:
- maximum recovery at 100% durability: 50%
- durability scaling enabled
- intricate materials/components excluded

So durability may change HOW MUCH material is returned, but no longer changes HOW LONG recycling takes.

## Fuel / bench
Still uses:
- DankPyon_MendingBench
- MO WorkGiver_DoMending
- MO JobDriver_DoMending
- MO repair-tool fuel via UsedThisTick()

Repair-tool fuel is therefore consumed for the entire fixed 5000-tick recycle duration.
