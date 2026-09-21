# Medieval Overhaul - Recycle Only 1.5 v1.5

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## Critical fix in v1.5
Medieval Overhaul's JobDriver_DoMending does two important things:
1. Work time is calculated from the mending recipe and missing HP.
2. On completion it directly heals the target item to full HP and does NOT call GenRecipe.MakeRecipeProducts.

That means earlier recycle-only builds could finish by repairing the item instead of recycling it.

v1.5 keeps MO's mending work path and repair-tool fuel consumption, but wraps the MO completion toil:
- Normal MO mending recipes: original MO completion runs unchanged.
- Allen_MO_RecycleApparel / Armor / Weapon:
  - MO hpHeal is skipped.
  - Recycle materials are calculated from the item's adjusted construction cost.
  - The original item is consumed/destroyed.
  - Returned materials are spawned near the worker.
  - The bill iteration is completed normally.

## Work amount
The recycle RecipeDefs keep the same base workAmount as MO mending:
  workAmount = 30

MO mending effectively uses:
  30 x missing HP

Recycle mirrors it:
  30 x current HP

So lower-durability items are faster to dismantle.

## Material return
Default maximum recovery: 50% at 100% durability.

With durability scaling enabled:
  recovery fraction = 50% x current HP / max HP

Examples:
- 100% durability -> up to 50% of eligible original materials
- 50% durability -> up to 25%
- 10% durability -> up to 5%

Intricate materials/components remain excluded by default.

## Fuel / workbench
Uses:
- DankPyon_MendingBench
- MO WorkGiver_DoMending
- MO DankPyon_DoBillMending / JobDriver_DoMending
- MO repair-tool fuel consumption via UsedThisTick()

No separate mending system is added.
