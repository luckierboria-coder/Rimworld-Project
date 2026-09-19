# Ideology Delete UI 1.5

For RimWorld 1.5.4063-class saves.

## Features

- Adds a trash/delete icon to every ideoligion row in the normal in-game Ideology screen.
- Adds developer action: `Ideoligion -> Delete ideoligion...`.
- Clicking delete first scans current references.
- If any faction still has the ideoligion as primary/minor, or any pawn currently believes in it, deletion is blocked and the references are listed.
- When no blocking references remain, deletion calls vanilla `Find.IdeoManager.Remove(ideo)`.
- Also clears stale baby ideology-exposure references that vanilla `IdeoManager.Remove` does not explicitly clean.

## Safety

This mod does not force-delete an ideoligion that is still owned by a faction or currently used by a pawn. Change those references first and then delete it.

The pre-game ideology configuration pages are not modified.
