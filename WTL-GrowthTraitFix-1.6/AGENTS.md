# WTL Growth Trait Fix — agent handoff

- **Goal:** preserve WorldTechLevel v1.1.6 Trait filtering in RimWorld 1.6 while
  avoiding the whole-database rebuild on `TraitDef` changes triggered by a
  Biotech child growth letter.
- **Relevant upstream source:**
  `Sources/WorldTechLevel/Patches/Patch_PawnGenerator.cs`,
  `Sources/WorldTechLevel/TechLevelDatabase.cs`,
  `Sources/WorldTechLevel/DefTechLevels.cs`
  at `m00nl1ght-dev/WorldTechLevel`, commit
  `5b71598d9c272c0a6b662374dbde247de4d242db`.
- **Entrypoint:** `Source/WTLGrowthTraitFix.cs`, constructor of
  `GrowthTraitFixMod : Verse.Mod`.
- **Patches:** prefix on closed generic
  `WorldTechLevel.TechLevelDatabase<TraitDef>.EnsureInitialized`;
  postfix on private `WorldTechLevel.Patches.Patch_PawnGenerator.FilteredTraits`.
- **Safety:** do not globally disable WTL, do not mutate `TraitDef`
  lists, do not invent fallback forbidden traits, do not patch
  `PawnGenerator.GenerateTraitsFor` with another transpiler.
- **Build:** GitHub Actions workflow `.github/workflows/build-wtl-growth-trait-fix.yml`;
  .NET Framework 4.7.2 / RimWorld 1.6 ref package. No local build setup or
  game DLL redistribution.
- **Testing:** check compile workflow logs first. If compiled, reproduce the
  child's growth selection on a save backup. Compare filtered candidates
  against WTL overrides and check that there is no
  `TraitDef database was modified` warning or broad reinitialization.
- **Known limitation:** WTL can still be reinitialized by changes to
  **other** def databases. This patch targets only TraitDef; opening the
  growth screen with other mod conflicts can still hang.
- **Licensing:** keep source original to this compatibility mod; upstream WTL
  is CC BY-NC-SA and should not be copied into this project wholesale.
