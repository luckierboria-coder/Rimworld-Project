# WTL Growth Trait Fix v3 (RW 1.6.4871 / WTL 1.1.6)

**Experimental diagnostic fix. Runtime outcome NOT yet verified.** Prior v1 was unsafe and v2 did not reliably fix the user's hang.

## Reproduction evidence (private save; never commit save)

User's `测试3.rws` (~116 MB) contains **Letter_11476** / `ChoiceLetter_GrowthMoment` for **Effum**, pawn ID `Thing_Human5507657`, growth tier 8, requesting **six trait options**. Both `traitChoices` and `passionChoices` were null (lazy initialization); the pawn's only current trait is `RT_TalentPhysician`, childhood `rjw_childT`, hybrid Baseliner with multiple endogenes.

The failing `Player(20261017-142233).log` shows:
- WTL v2 installed, but no `TraitDef database was modified` warning.
- SMF 0.3.13 `Renderer failed: First top-level Repaint does not target the original client framebuffer` **before** save loading.
- No `ChoiceLetter_GrowthMoment` / `GenerateTraitsFor` stack trace before shutdown.
- Vehicle thread abort messages upon shutdown do not by themselves identify the growth cause.

RimWorld 1.6 `ChoiceLetter_GrowthMoment.TrySetChoices` calls
`PawnGenerator.GenerateTraitsFor(pawn, traitChoiceCount, null, true)` lazily.
Vanilla samples with at most `traitCount+500` attempts, while WTL replaces the
candidate list expression inside that loop with its private
`Patch_PawnGenerator.FilteredTraits(Pawn)` on each iteration. This results
in repeated filtering/enumeration, especially when choosing six valid traits.

## v3 design

- Cache original WTL's **allowed TraitDef array** only during one call to
  `PawnGenerator.GenerateTraitsFor` for a growth moment, using thread-local
  scoped state; nested calls have their own scope.
- Leave WTL's filtering and vanilla trait/degree/conflict checks intact.
- On changed `TraitDef` count only, use WTL's TraitDef-specific cache
  refresh and overrides before original WTL filter runs.
- Add phase logs at `ChoiceLetter_GrowthMoment.OpenLetter`,
  `TrySetChoices`, `GenerateTraitsFor`, and the first filtered candidate
  snapshot. Last entered but not exited phase identifies where a hang occurs.
- NEVER patch generic `TechLevelDatabase<T>.EnsureInitialized` or WTL
  research cache.
- No long-running ticks, file writes, save modifications or random seeding.

## Install / test

1. Back up the save. Remove v1 (`allen.wtl.growthtraitfix`) and v2 (`allen.wtl.growthtraitfix.v2`); **do not enable them alongside v3**.
2. Keep the authentic **complete WTL 1.1.6** installed. Place v3 after WTL.
3. First test the save with **Simply More FPS disabled**, to isolate the
   SMF 0.3.13 native framebuffer error found in the same failed-run log.
4. Load backup of `测试3.rws` and click Effum's growth letter. Keep all WTL
   filters on. Do not overwrite your only good save.
5. If it hangs, terminate the game and capture the COMPLETE `Player.log`;
   it will contain a last `[WTLGrowthV3] ... ENTER` marker if this is a
   trait-generation hang.
6. If it works, check six permitted candidate traits and Research Tree's
   medieval filtering, and then retest with SMF separately.
7. No guarantee of a true fix until consistent in-game validation.

## Build

GitHub Actions compiles against `Krafs.Rimworld.Ref 1.6.4871` using .NET Framework 4.7.2
without redistributing proprietary game DLLs.

## Source references

- [WorldTechLevel v1.1.6 upstream](https://github.com/m00nl1ght-dev/WorldTechLevel)
- [1.6-decompiled ChoiceLetter_GrowthMoment.cs](https://github.com/32bitx64bit/RW-Decompiled-1.6/blob/main/RimWorld/ChoiceLetter_GrowthMoment.cs)
- [1.6-decompiled PawnGenerator.cs](https://github.com/32bitx64bit/RW-Decompiled-1.6/blob/main/Verse/PawnGenerator.cs)
