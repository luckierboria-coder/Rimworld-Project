# Toddlers Toy Mote Hotfix 1.5

The installed Toddlers 1.5 assembly stores five optional `Mote_Toy` instances and calls `Maintain()` on every array entry. RimWorld can return `null` from `MoteMaker.MakeStaticMote`; the unguarded call then throws every tick and the pawn repeatedly starts a new `ToddlerPlayToys` job.

This patch changes only that call site to a null-safe helper. It does not catch unrelated exceptions and does not change job selection, needs, reservations, pathing, duration, or completion logic.

Evidence: current `Player.log` on 2026-10-01 recorded repeated `Pascolo` failures at `JobDriver_ToddlerPlayToys.<PlayToil>b__2_1` IL offset `0x000a3`. The installed IL maps that offset to `motesToMaintain[j].Maintain()`.
