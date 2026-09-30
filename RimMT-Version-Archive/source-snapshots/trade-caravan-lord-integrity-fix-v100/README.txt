Trade Caravan Lord Integrity Fix 1.5 v1.0.0

Scope:
- Rejects already-owned, dead, destroyed, despawned, or wrong-map pawns before a TradeWithColony Lord is created.
- Requires at least one valid pawn with TraderKind.
- Sends newly spawned non-traders off-map if an incident supplied no trader.
- Removes an existing empty trader-less TradeWithColony Lord before its next Lord tick.
- Converts a non-empty trader-less TradeWithColony Lord to ExitMapBest so its pawns are not stranded.

This mod does not change trader stock, incident frequency, old AssistColony Lords, or RimMT job-search code.
