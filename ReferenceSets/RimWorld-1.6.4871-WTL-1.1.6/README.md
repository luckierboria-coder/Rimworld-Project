# RimWorld 1.6.4871 / WTL 1.1.6 reference set

This folder captures provenance and exact checksums for two archives the user supplied on 2026-10-17 for RimWorld 1.6 compatibility work:

- `Managed(1).zip` (107 entries; RimWorld 1.6.4871, Player.log identifies rev591)
- `3414187030(1).zip` (31 entries; World Tech Level v1.1.6).

**Actual binary archives have not been committed.** This GitHub repository is PUBLIC. The `Managed` archive contains RimWorld and Unity runtime assemblies, and therefore it must not be redistributed into the public repository. The WTL mod source is publicly available upstream and should be taken from the original author when available.

Consult [manifest.json](manifest.json) for exact ZIP SHA-256 fingerprints, reference DLL hashes, package ID and direct upstream references. Consult [AGENTS.md](AGENTS.md) before new mod development.

## Key finding

WTL 1.1.6 stores its 1.6 assembly at **`1.6/Lunar/Components/WorldTechLevel.dll`**, with a Lunar manifest, not at `1.6/Assemblies/`. A RimWorld report saying the mod "did not load any content" does not by itself prove the Lunar component is missing or inactive. Verify Lunar's component bootstrap and load order first.

## Build without re-requesting game DLLs

For ordinary patch compilation, the `Krafs.Rimworld.Ref` NuGet reference package **1.6.4871** is the preferred route (already used successfully for the `WTL-GrowthTraitFix-1.6` project, but success compiling does not imply correctness at runtime). Download WTL original source from the linked upstream repo; use this manifest to verify a binary if supplied again. Do not reconstruct game implementation details from stub reference assembly bodies.

## Important limitation

To do exact IL reverse engineering on the user's particular `Assembly-CSharp.dll`, the actual archive is required in an accessible private workspace or as an uploaded attachment. Only a hash/manifest has been preserved in this public repo; **do not claim the binaries are already hosted in GitHub**.

For permanent remote storage of the original bytes, use a private GitHub repository with authorized access and an appropriate game-license-compliant method. The connected GitHub interface currently cannot create repositories or upload binary attachments from this container.

## Safety

Never put game DLLs, decompiled copies of proprietary assemblies, saves, personal logs, or user-specific configuration in this public repository.
