# Ideology Delete UI 1.5 v1.2

This version implements unconditional in-save deletion.

Click the trash icon on any ideoligion row. After confirmation the mod automatically:
- reassigns every pawn currently believing in that ideoligion;
- reassigns any faction using it as primary;
- removes all minor-faction references;
- cancels active rituals belonging to it;
- cleans baby ideology-exposure references;
- generates a fallback ideology if this is the last remaining ideology;
- calls vanilla `Find.IdeoManager.Remove(ideo)`.

The normal in-game Ideology screen gets the trash button. Developer mode also gets:
`Ideoligion -> Force delete ideoligion...`

The pre-game ideology configuration pages are not modified.
