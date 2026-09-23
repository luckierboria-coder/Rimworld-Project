# Medieval Overhaul - Recycle Only 1.5 v1.8

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## Recycle duration
Recycle duration is controlled ONLY by the recycle RecipeDef's XML workAmount.

Default XML value:
  <workAmount>5000</workAmount>

Meaning:
- 5000 = 2 in-game hours
- 2500 = 1 in-game hour
- 1000 = 0.4 in-game hours

The C# no longer forces 5000.

The job decrements workLeft by exactly 1 per game tick, so duration is not affected by:
- durability
- material
- MaxHP
- pawn crafting/work speed
- workbench speed

## Full-durability items
Recycle bills accept 100% durability items.
MO's original mending bills remain unchanged.

## Completion behavior
Recycle completion:
- does not run MO hpHeal
- consumes/destroys the original item
- calculates returned materials
- spawns returned materials near the worker
- completes the bill normally

## Material return
Durability can still affect HOW MUCH material is returned if that setting is enabled.
It no longer affects HOW LONG recycling takes.

## Fuel / bench
Still uses:
- DankPyon_MendingBench
- MO WorkGiver_DoMending
- MO JobDriver_DoMending
- MO repair-tool fuel via UsedThisTick()
