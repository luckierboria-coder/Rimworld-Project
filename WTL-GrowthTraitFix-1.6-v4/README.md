# WTL Growth Trait Fix v4: UI Freeze Trace

**Experimental. RimWorld 1.6.4871 rev591, World Tech Level v1.1.6. Not game-tested.**

## Observed from failed v3 run on 2026-10-17

User's two uploaded text logs are IDENTICAL (SHA-256: 42e58e1360a7c35cd8b3385877c052a2884183967e5eacb5d93d25f9e14a63a7).

In the log, Effum's growth letter opened:
- OpenLetter ENTER, TrySetChoices ENTER, GenerateTraitsFor ENTER for six trait choices.
- WTL allowed 144 of 146 TraitDefs; trait generation finished in 1.56 ms.
- TrySetChoices EXIT with six trait choices and six passion choices, error=none.
- OpenLetter EXIT, error=none; **the game still froze and the dialog was not visible**.

Thus WTL / vanilla option generation *succeeded* and the immediate opening method completed. The next relevant phase is IMGUI rendering of Dialog_GrowthMomentChoices. Simply More FPS was DISABLED in this failed test (no SMF markers), so SMF alone cannot explain it.

Another anomaly: TraitDef count fluctuated 146 -> 111 -> 146 in one run. Not yet linked to the hang.

## v4 instrumentation

Retains v3 candidate caching unchanged, adding passive ENTER/EXIT logs on:
- Window.WindowOnGUI for Dialog_GrowthMomentChoices only;
- Dialog_GrowthMomentChoices.InitialSize and DoWindowContents;
- DrawPassionChoices, DrawTraitChoices, DrawBottomText;
- CharacterCardUtility.DrawCharacterCard and PawnCardSize;
- HealthCardUtility.DrawHediffListing;
- Trait.TipString.

All Harmony finalizers preserve exceptions and don't change method behavior. At most 135 extra UI logs are emitted.

## Installation and test

Disable v1/v2/v3. Install v4 alone AFTER the complete official WTL 1.1.6. Keep Simply More FPS disabled for an apples-to-apples test. Load the PRIVATE Test 3 save from a backup and click Effum's growth letter.

When it freezes, force close the game and provide the new Player.log. The last ENTER without corresponding EXIT will identify the hung method or underlying Unity GUI dispatch. If no UI ENTER appears after OpenLetter EXIT, investigate window scheduling or GUI event handling.

This is a diagnostic milestone; it does not pretend to be a validated cure. Never upload the user's save file or private logs to this public repository.

## Build

GitHub Actions only. NuGet Krafs.Rimworld.Ref 1.6.4871 / Harmony 2.3.3, net472.
