# Ideology Delete UI 1.5 v1.1

For RimWorld 1.5.4063-class saves.

## Features

- Adds a trash/delete icon to every ideoligion row in the normal in-game Ideology screen.
- Adds developer action: `Ideoligion -> Delete ideoligion...`.
- Primary faction references block deletion.
- Current pawn believers block deletion.
- Minor faction references do **not** block deletion; they are detached automatically at deletion time.
- After references are safe, deletion calls vanilla `Find.IdeoManager.Remove(ideo)`.
- Also clears stale baby ideology-exposure references that vanilla `IdeoManager.Remove` does not explicitly clean.

## v1.1 change

A faction keeping an ideoligion only as a minor ideology is treated as stale/non-owning bookkeeping. The delete operation removes that minor entry automatically before invoking vanilla removal.

The pre-game ideology configuration pages are not modified.
