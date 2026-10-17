# WTL Growth Trait Fix v5 — Unity IMGUI dispatch diagnostic

**Status: experimental source/CI test. No in-game verification yet.**

## Root cause has not been proven

Analysis of user's **V4-only** freeze log `Player(20261017-164634).log` from 2026-10-17:

- Only V4 was installed; V3 absent.
- Effum's `ChoiceLetter_GrowthMoment`: 6 of 6 trait/passions selected, `WTL permitted 144/146 TraitDefs`, `GenerateTraitsFor EXIT` at 1.39 ms; `TrySetChoices EXIT`, `OpenLetter EXIT` all normal.
- `Dialog.InitialSize` returned normally.
- More than 65 consecutive `Window.WindowOnGUI ENTER/EXIT error=none` pairs occurred for the growth window with **ZERO** `Dialog.DoWindowContents` and no role/trait UI callbacks.
- The game may LOOK frozen because a modal window absorbs input and pauses the sim, despite continuous Unity GUI callbacks. It is NOT proven that the main thread deadlocked.
- Earlier V4+V3 success is invalid due to overlapping Harmony patches; do not enable them together.
- The unmatched last entry at log budget limit is expected and does not prove a deadlock.
- No Simply More FPS in this frozen run. SmashTools ThreadAbort is at process shutdown.

## What v5 adds

V5 keeps v4 candidate generation unchanged but adds **sparse bounded** observations at the Unity window-dispatch level:
- `Event.current.type` (Layout, Repaint, Used, etc.)
- Growth dialog `windowRect` dimensions, id and entry count;
- `Find.UIRoot.screenshotMode.Active`, `Window.drawInScreenshotMode`, `onlyDrawInDevMode`, `Prefs.DevMode`;
- `Window.InnerWindowOnGUI` (the Unity `GUI.Window` callback), with ENTER/EXIT;
- Existing `Dialog.DoWindowContents` logs remain.

No renderer bypass, force-show or GUI state modifications; the diagnostic is intended to distinguish skip guards versus never-invoked Unity `GUI.Window` callbacks versus body rendering. Logs are bounded.

## Installation and test

1. Turn OFF all V1/V2/V3/V4 growth fixes and enable V5 alone after full WTL v1.1.6.
2. Keep Simply More FPS disabled for the comparison.
3. Use a backup of `测试3.rws` with Effum's growth notification.
4. Click notification once; if the modal invisible window still blocks the game, exit without overwriting the save and submit complete `Player.log`.
5. Look for `[WTLGrowthV5/UI] WindowOnGUI ENTER #1 ... screenshot=... rect=(...)`, and `InnerWindowOnGUI ENTER` / `Dialog.DoWindowContents ENTER` to identify where the window disappears.

## Safety

Never commit the user's saves/logs to this public repo. No changes to WTL research eligibility, trait IDs/selection or save serialization. The compile is executed only in GitHub Actions using reference assemblies 1.6.4871.
