# Agents — RimWorld 1.6.4871 WTL 1.1.6 reference provenance

## Current task context
- User is running RimWorld 1.6.4871 with Biotech, Medieval Overhaul, CE, WTL v1.1.6 and many mods.
- Prior `WTL-GrowthTraitFix-1.6` PR #76 is **unsafe and deprecated**: user reported research-tree regression. Never redistribute or install that build.
- Problem to solve: retain WTL trait filtering in `ChoiceLetter_GrowthMoment` and allow permitted choices, without changing research filtering.

## Assets / provenance
- Details and hashes: `manifest.json`.
- Uploaded file titles: `Managed(1).zip`; `3414187030(1).zip` (2026-10-17).
- Public upstream WTL source: https://github.com/m00nl1ght-dev/WorldTechLevel
- WTL 1.6 binary in Lunar component path: `1.6/Lunar/Components/WorldTechLevel.dll`.
- GitHub repo does **not** contain either ZIP or game DLL, only metadata. Never silently pretend binaries are in this repository.
- For compile against 1.6, use NuGet `Krafs.Rimworld.Ref` 1.6.4871; actual IL inspection needs originals.
- Do not use old RimWorld 1.5 managed DLLs for 1.6 work.

## Developer guidelines
- Use the connected GitHub environment and existing CI rather than creating an uncontrolled local build system.
- Avoid Harmony prefixes on closed instances of generic types without auditing shared generic-code behavior.
- Verify that LunarFramework has loaded WTL components before attempting reflection patching. This implementation is not a conventional /Assemblies mod.
- Do not assume that `did not load any content` indicates missing WTL assembly when it loads via Lunar.
- In addition to trait growth, test research visibility with Research Tree; log in previous attempt indicated a mismatch with the WTL `ResearchUtility.ShouldProjectBeVisible` adapter.
- Keep mod changes separate from resource manifests and keep proprietary game assemblies out of public GitHub.
- Test actual functionality in game; compilation alone does not establish behavior. Provide a directly accessible install ZIP when valid, never link to untested builds as finished artifacts.

## Copyright
`Managed` contains game-engine/product DLLs and must not be committed to PUBLIC GitHub. WTL original mod is under its included CC BY-NC-SA 4.0 license; cite original author and comply with license. Use private, permissioned storage for non-public assets.
