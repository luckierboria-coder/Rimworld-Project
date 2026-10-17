# WTL Growth Trait Cache Guard v2 — RimWorld 1.6

**Status: source/CI build candidate. Do not consider game-tested.**

## Why v1 was withdrawn

The old `WTL-GrowthTraitFix-1.6` PR #76 is unsafe. It attempted to
patch `TechLevelDatabase<TraitDef>.EnsureInitialized`, a method on a
generic type. This risked affecting other generic instantiations. The actual
user log then established the old patch **failed at startup**:
`World Tech Level internal trait database not found`.

## Critical discovery in the user's WTL installation

In supplied `3414187030(1).zip`, `WorldTechLevel.dll` and manifest
exist at `1.6/Lunar/` and both .lfc checksums validate; but the archive
does not contain `LunarLoader.dll` or `LunarFramework.dll`. The log
shows WTL types are missing at runtime, plus Research Tree cannot find
`WorldTechLevel.ResearchUtility.ShouldProjectBeVisible`.

Read the WTL Lunar bootstrap documentation and the diagnostic notes before
installing a trait patch. **A running WTL component is a hard prerequisite.**

## Narrow v2 patch

- Harmony prefix on WTL **non-generic** private
  `WorldTechLevel.Patches.Patch_PawnGenerator.FilteredTraits(Pawn)`.
- If TraitDef count changes, rebuilds only WTL's
  `TechLevelDatabase<TraitDef>` cache and reapplies WTL's configured
  overrides, then runs the **original** WTL method.
- Does not modify vanilla growth-letter methods, weight/degree/conflict logic,
  save data, `ResearchProjectDef` or other WTL filter methods.
- If WTL Lunar is not loaded, logs an explicit error and does nothing.
- Failure path deliberately preserves WTL original behavior; it never
  substitutes a forbidden/unfiltered trait pool.

**Limits:** WTL may still trigger a broad initialization for another changed
Def database. Replacing a dynamically modified TraitDef registry with a single
cache pass may not be enough if another mod continuously changes defs. It
does not guarantee children always have enough trait options.

## Test sequence

1. Back up saves and disable the old unsafe `allen.wtl.growthtraitfix`.
2. Restore a complete, authentic WTL v1.1.6 installation with functioning
   Lunar loader/framework. Verify WTL's own tech restrictions in-game.
3. Put this v2 mod *after* WTL and restart the game.
4. Verify `Player.log` reports v2 patch installed (NOT `WTL ... not loaded`).
5. Open Research Tree and confirm advanced projects are excluded as before.
6. Open Biotech child growth notice in a pre-event save, confirm trait choices
   are valid and growth selection finishes.
7. If startup or runtime errors occur, disable v2 and capture Player.log.

## Build

GitHub Actions builds with `Krafs.Rimworld.Ref` version `1.6.4871` and
`Lib.Harmony`; no local build environment nor redistribution of game DLLs.

## Upstream

WorldTechLevel v1.1.6 source:
https://github.com/m00nl1ght-dev/WorldTechLevel/
Lunar Framework bootstrap:
https://github.com/m00nl1ght-dev/LunarFramework/blob/master/Sources/LunarFramework/Bootstrap/Entrypoint.cs
