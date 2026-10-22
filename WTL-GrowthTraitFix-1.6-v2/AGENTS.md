# Agent notes — WTL Growth Trait Fix v2
- This project targets **RimWorld 1.6.4871** / WorldTechLevel **1.1.6**.
- Do not reuse or reinstall v1 (`allen.wtl.growthtraitfix`, PR #76); user reported a major research tree regression and log showed v1 WTL type resolution failed.
- WTL is a Lunar Framework component (loaded from `1.6/Lunar/Components/WorldTechLevel.dll`). Verify Lunar loader bootstrap before patching.
- Do **NOT** patch `TechLevelDatabase<T>.EnsureInitialized` (generic method). Only patch the **non-generic WTL FilteredTraits(Pawn)** entrypoint as in `Source/GrowthTraitCacheGuard.cs`.
- Preserve WTL filter result, trait degrees, existing trait conflicts, player tech level and override options.
- Do not touch any research methods; Research Tree error is a diagnostic for WTL not loaded, not permission to bypass filtration.
- No user/game DLLs, logs, saves or configs in the PUBLIC repository. Provenance manifest is at `ReferenceSets/RimWorld-1.6.4871-WTL-1.1.6/`.
- Source/CI compilation alone is not enough to mark this patch as proven. Must validate game behavior on the user's actual WTL, Medieval, Biotech, CE, Research Tree mod list.
- GitHub Actions only; do not set up a parallel local compiler without request.
