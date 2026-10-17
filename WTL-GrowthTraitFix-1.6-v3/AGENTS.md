# WTL Growth Trait Fix v3 — agent notes

- Target: RimWorld 1.6.4871 rev591, WTL 1.1.6.
- Current reproducer: user-provided `测试3.rws` for Effum, letter 11476, growth tier 8, traitChoiceCount 6. **Private save: do NOT upload to public GitHub.**
- WTL v1.1.6 is a Lunar component; requires complete upstream release.
- Old v1 is UNSAFE (PR #76); v2 (PR #78) installed but not reliable.
- Source `Source/GrowthTraitGuard.cs`. Only non-generic Harmony targets: PawnGenerator.GenerateTraitsFor, WTL private FilteredTraits(Pawn), ChoiceLetter_GrowthMoment.OpenLetter and TrySetChoices.
- V3 caches filtered candidate snapshot once per growth generation attempt. Ensure no semantic changes to WTL filter eligibility, vanilla conflict/degree validation, or research tech levels.
- Logs prefixes/finalizers to identify exact last phase before hang; no continuous polling.
- Once in-game reproduced, confirm absent exit markers and check any SMF render crash. If candidate gen exits cleanly, investigate UI/SMF; do not keep patching WTL blindly.
- Build/packaging must use GitHub Actions; no new local toolchain, and no game DLL uploads to public repo.
