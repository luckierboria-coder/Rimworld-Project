# Grenade Stack Counter Fix

RimWorld 1.5 companion fix for stackable grenades and other stackable weapons.

## Problem

`RecipeWorkerCounter.CountValidThings()` counts valid map products by Thing instance (`+1`) rather than by `Thing.stackCount`. That is correct for normal non-stackable weapons, but it undercounts any weapon a mod makes stackable.

Example: 160 grenades merged into 8 stacks can appear as 8 in a Target Count bill.

## Fix

For any recipe product where:

- `ThingDef.IsWeapon == true`
- `ThingDef.stackLimit > 1`

`CountValidThings()` returns the sum of `stackCount` for things that still pass Vanilla `CountValidThing()` filtering.

This covers the Medieval Overhaul grenades previously patched as well as stackable weapons from other mods, including House Zahir's bomba pot when it is stackable.

Normal materials, apparel, non-weapons, and ordinary non-stackable weapons use Vanilla unchanged.

This mod does not itself change `stackLimit`; keep your grenade stack-limit XML changes installed.
