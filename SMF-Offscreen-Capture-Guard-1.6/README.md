# SMF Offscreen Capture Guard — Minimal 1.6

Target game: RimWorld 1.6.4871. Target: **the exact Simply More FPS 0.3.13 binary** whose `SimplyMoreFPS.Rendering.HybridSession` ModuleVersionId is `b5a8419b-43bc-496c-aafd-434185e80478`.

## Purpose (唯一保留的主功能)

Protect the SMF offscreen frame capture entry point. Harmony prefix on `SimplyMoreFPS.Rendering.HybridSession.Session.BeginCapturedFrame`: when `UnityEngine.RenderTexture.active != null`, return `false` immediately and set `__result=false`. When the active target is null, let SMF execute unchanged.

This is copied semantically from **BeforeCapturedFrame** in the supplied `Allen.SmfGrowthWindowGuard(1).dll`. The guard avoids entering an SMF detached capture while a non-null render target is active. It does not independently patch SMF rendering mode.

## Removed (全部不再存在)

- `ChoiceLetter_GrowthMoment.OpenLetter` and any deferred growth letter/window opening
- `Root_Play.Update` retry/redispatch
- `AfterSelectionInput` / `BeforeSelectionPublish`
- `HybridSession.SetRenderingAllowed` / `BeforeRenderingPolicy`
- `Dialog_GrowthMomentChoices`, window-stack detection, rendering toggles
- Pending growth/game/frame state and growth-specific warnings/logging

## Installation / 安装

Build output has `About/About.xml` and `1.6/Assemblies/Allen.SmfCaptureGuard.dll`.
**Remove the old `Allen.SmfGrowthWindowGuard.dll`** and its old package before enabling this one. Never load both.

This new mod **does not fix growth-window issues**. It deliberately preserves only the standalone offscreen capture protection. It becomes inactive if the SMF module fingerprint changes, so no blind patching of a different build.

## Verification / 验证

1. Start RimWorld 1.6 with target Simply More FPS build.
2. Check startup logs for exactly one `[SMF Capture Guard] Offscreen RenderTexture ownership guard installed.`.
3. No `[SMF growth guard]` log lines should appear (unless the old DLL is still installed).
4. Exercise the previously problematic offscreen rendering case and check for framebuffer/capture failures.

**Implementation source:** `Source/SmfCaptureGuard.cs`. Built with GitHub Actions; no local compiler required.
