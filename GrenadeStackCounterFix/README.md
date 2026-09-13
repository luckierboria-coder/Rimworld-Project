# Grenade Stack Counter Fix

RimWorld 1.5 companion fix for stackable medieval grenades.

## Problem

`RecipeWorkerCounter.CountValidThings()` counts valid map products by Thing instance (`+1`) rather than by `Thing.stackCount`. That works for normal non-stackable weapons, but undercounts these grenades after they are made stackable.

Example: 160 grenades merged into 8 stacks can appear as 8 in a Target Count bill.

## Fix

For only these product defs:

- `DankPyon_Weapon_PotFire`
- `DankPyon_Weapon_PotFlash`
- `DankPyon_Weapon_PotFlashSmoke`
- `DankPyon_Weapon_AcidFlask`

`CountValidThings()` returns the sum of `stackCount` for things that still pass Vanilla `CountValidThing()` filtering.

Everything else uses Vanilla unchanged.

This mod does not itself change `stackLimit`; keep the grenade XML stack-limit change (`10`) installed.
