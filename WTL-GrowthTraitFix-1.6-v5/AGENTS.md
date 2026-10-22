# Agent guidance — growth window dispatch / invisible modal

RimWorld 1.6.4871 rev591 / WTL 1.1.6 / Biotech; reproducer Effum growth letter 11476.

V4-only freeze log `Player(20261017-164634).log` proves:
- Trait/passions created, OpenLetter returned.
- Window.WindowOnGUI invoked repeatedly (>65 pairs), BUT **zero** DoWindowContents or CharacterCard calls.
- No Simply More FPS and no V3.
- Not enough evidence to say main thread deadlocked; modal dialog absorbs input.
- Inspect screenshot/dev visibility guards, Event.current.type and Window.InnerWindowOnGUI dispatch.

V5 adds bounded logging, no renderer modification. Never install alongside old growth fixes. Do not commit user save or private logs. Build using GitHub Actions, do not create local compile environment.
