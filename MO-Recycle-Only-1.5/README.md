# Medieval Overhaul - Recycle Only 1.5 v1.6

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## v1.6
Full-durability items can now be recycled.

Medieval Overhaul's WorkGiver_DoMending normally accepts an ingredient only when:
  ingredient.filter.Allows(t) && t.HitPoints < t.MaxHitPoints

That damaged-only rule remains unchanged for MO's original mending recipes.

For these recycle recipes only:
- Allen_MO_RecycleApparel
- Allen_MO_RecycleArmor
- Allen_MO_RecycleWeapon

the patch keeps the normal bill and ingredient filters but removes the HitPoints < MaxHitPoints requirement.

## Work amount
MO's original mending JobDriver calculates work from missing HP. That breaks for a full-durability recycle target because missing HP is zero.

v1.6 directly overrides the MO work toil after its normal initialization:
  recycle workLeft = 30 x current HP

Examples:
- 100/100 HP -> 3000 work
- 50/100 HP -> 1500 work
- 10/100 HP -> 300 work

Lower durability is therefore faster to recycle, and 100% durability is valid instead of becoming zero-work.

## Completion
Recycle completion does not run MO's hpHeal branch.

Instead it:
- Calculates returned materials from the item's adjusted construction cost.
- Applies the configured recovery fraction.
- Consumes/destroys the original item.
- Spawns returned materials near the worker.
- Completes the bill iteration.

MO's normal mending recipes still use the original completion code unchanged.

## Fuel / bench
Still uses:
- DankPyon_MendingBench
- MO WorkGiver_DoMending
- MO JobDriver_DoMending
- MO repair-tool fuel via UsedThisTick()
