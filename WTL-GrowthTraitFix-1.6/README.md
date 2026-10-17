# WTL Growth Trait Fix (RimWorld 1.6)

A minimal compatibility patch for **World Tech Level v1.1.6**, specifically for
**Biotech child growth trait choices** failing to open when WTL detects that
`TraitDef` registrations changed.

## Diagnosis

In WTL 1.1.6, `Patch_PawnGenerator.FilteredTraits(Pawn)` feeds a tech-filtered
`TraitDef` list to the vanilla `PawnGenerator.GenerateTraitsFor` method.
WTL already handles replacement correctly at the **candidate pool** level:
the vanilla growth logic chooses from permitted traits. A separate
`GainTrait` or random-trait replacement patch would interfere with vanilla
conflict and degree handling.

The problematic call chain was:

```text
ChoiceLetter_GrowthMoment.TrySetChoices
 -> PawnGenerator.GenerateTraitsFor
 -> WorldTechLevel.Patches.Patch_PawnGenerator.FilteredTraits
 -> TechLevelDatabase<TraitDef>.EnsureInitialized
 -> DefTechLevels.Initialize  [rebuilds every Def database]
```

This patch changes the last step to:

```text
 -> TechLevelDatabase<TraitDef>.Initialize(null)
 -> TechLevelDatabase<TraitDef>.ApplyOverrides()
```

It also snapshots the lazy WTL result once, before vanilla builds trait choices.

## Scope and invariants

- WTL's **Traits** filter remains enabled and authoritative.
- WTL tech-level classifications and the user's custom overrides are preserved.
- WTL's original `GenerateTraitsFor` transpiler and vanilla candidate-selection,
  conflicting-trait checks and trait degrees are not replaced.
- Existing pawn traits and saves are not modified.
- Only WTL's **closed `TechLevelDatabase<TraitDef>`** cache guard is patched.
- No periodic tick, world scanning or background processing is used.

**Limitations:** If no permitted trait exists, the patch logs a warning and
does not invent a forbidden replacement. Any mod continually modifying
`TraitDef` at runtime can still be incompatible. This is a targeted fix, not
a claim that every growth-screen incompatibility is solved.

## Install / test

1. Back up the save, and use World Tech Level **v1.1.6** with RimWorld **1.6**.
2. Install Harmony and World Tech Level.
3. Install this mod and load it **after WTL**.
4. Leave WTL > Traits filtering **enabled**.
5. Load a save immediately before a child's growth letter, click the letter,
   and confirm the growth selection screen opens and all proposed traits are
   permitted by WTL's current level and overrides.
6. Repeat with a colony of mixed xenotypes and another run without the mod.
7. Examine `Player.log` for `WTLGrowthTraitFix` messages.

If the screen still hangs, provide the complete Player.log; another mod's
transpiler on `GenerateTraitsFor` may be responsible.

## Build

GitHub Actions uses `Krafs.Rimworld.Ref` for **RimWorld 1.6.4871** and
`Lib.Harmony` from NuGet. No game DLL or local build environment is committed.

From a .NET SDK shell:

```sh
dotnet build Source/WTLGrowthTraitFix.csproj -c Release
```

The Action stages `About/About.xml` and the compiled DLL into an installable
mod folder and uploads it as a ZIP artifact.

## Upstream

Source reviewed at WTL 1.1.6 tag/commit
[5b71598](https://github.com/m00nl1ght-dev/WorldTechLevel/commit/5b71598d9c272c0a6b662374dbde247de4d242db).

This is a **separate compatibility mod**, not a fork or redistribution of WTL.
WTL remains the required dependency.

## Status

Source and CI packaging scaffold; the fix must be compile-checked by the
GitHub workflow and behavior-tested in a RimWorld 1.6 + Biotech game.
