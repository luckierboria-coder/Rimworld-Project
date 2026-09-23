# Medieval Overhaul - Recycle Only 1.5 v1.9

Target: RimWorld 1.5.4063 + Medieval Overhaul.

## v1.9 workAmount fix
Recycle work no longer runs MO's original DoRecipeWork_Mend init/tick logic at all.

For recycle recipes:
- initAction reads job.RecipeDef.workAmount directly
- workLeft is set directly to that value
- tickAction subtracts exactly 1 per game tick
- durability, MaxHP, material, pawn work speed and bench work speed do not affect duration
- MO mending recipes still use the original MO logic unchanged

The mod now logs the runtime workAmount values at startup and again when a recycle job begins.

Example log:
[MO Recycle Only 1.5 v1.9] Runtime Allen_MO_RecycleWeapon.workAmount = 1000
[MO Recycle Only 1.5 v1.9] Recycle start: recipe=Allen_MO_RecycleWeapon, XML workAmount=1000, actual workLeft=1000, item=...

The Mod Settings page also shows the runtime workAmount currently loaded for Apparel / Armor / Weapon. This lets you immediately verify whether the XML file you edited is actually the one RimWorld loaded.

Default Recipes_Recycle.xml remains:
  <workAmount>5000</workAmount>

Change that XML value and restart RimWorld. The displayed runtime value and actual workLeft must match it.

All previous recycle completion behavior remains:
- full-durability items allowed
- original item is consumed
- returned materials are generated
- MO hpHeal is bypassed
- MO repair-tool fuel is consumed while working
