PUAH 1.5 Queue Hotfix V5.2.1

IMPORTANT
- V5.2.1 keeps the already-tested V5.1 queue-safety DLL unchanged:
  1.5\Assemblies\PUAHQueueHotfix.dll
  SHA-256: f78825f2b06291341667b917339b2a91c183738aa5e66e93cf049a48cf57e6dc
- The performance experiment is isolated in a second DLL:
  1.5\Assemblies\PUAHPerformanceV52.dll
- V5.2.1 does NOT transpile PickUpAndHaul.WorkGiver_HaulToInventory.JobOnThing.

WHY V5.2.1 EXISTS
The first V5.2 test DLL was rebuilt against an inaccurate GitHub CI Assembly-CSharp stub. That produced a bad Verse.AI.Job.def member reference and caused MissingFieldException after entering a real RimWorld 1.5.4063 save. V5.2.1 discards that combined DLL design.

V5.1 SAFETY BEHAVIOR (UNCHANGED)
- Paired targetQueueA/countQueue entries with count <= 0 are removed together.
- Invalid/destroyed/despawned queued Things are removed together with their count entry.
- Unmatched queue tails are trimmed.
- Valid paired entries continue through the original PUAH HaulToInventory job.
- If no safe paired targets remain, the bad job is rejected rather than allowed to crash.
- A second guard runs before TryMakePreToilReservations.
- Successful repairs remain silent to avoid Warning/StackTrace stutter.

V5.2.1 PERFORMANCE BEHAVIOR
- Only patches PUAH's private WorkGiver_HaulToInventory.FindClosestThing method.
- Uses an exact 16x16 spatial index to avoid repeated full-list distance scans during multi-haul target collection.
- Preserves the original PUAH JobOnThing sort.
- Preserves original PUAH GetClosestAndRemove / RemoveAt behavior.
- Preserves original CanReach, validator, StoreUtility, reservation, capacity and queue-building logic.
- If the index state does not match the live List, it rebuilds. If validation still fails, that one call falls back to PUAH's original FindClosestThing implementation.

EXPECTED STARTUP LOGS
[PUAH 1.5 Queue Hotfix V5.1] Applied HaulToInventory queue repair + reservation guards.
[PUAH 1.5 Queue Hotfix V5.2.1] Safe performance layer active: PUAH FindClosestThing uses an exact 16x16 spatial index. JobOnThing is NOT transpiled; original PUAH Sort/CanReach/validator/RemoveAt flow remains authoritative. V5.1 queue safety DLL is unchanged.

中文说明
- V5.2.1 不重新编译 V5.1 的防崩逻辑，直接保留已经实机验证过的 V5.1 DLL。
- 新增独立 PUAHPerformanceV52.dll，只优化 PUAH 自己的 FindClosestThing 多目标搬运最近物搜索。
- 不再修改 JobOnThing IL，不跳过 JobOnThing 的原始 Sort。
- CanReach、Validator、StoreUtility、Reservation、容量判断、targetQueue 构造全部保持原逻辑。
- 索引状态异常时先重建；仍不能确认时，本次调用直接回退 PUAH 原始线性 FindClosestThing。

INSTALL
1. Exit RimWorld completely.
2. Remove the V5.2 test folder. Do not leave V5.2 and V5.2.1 together.
3. Put the V5.2.1 folder under RimWorld\Mods.
4. Enable it after Pick Up And Haul.
5. Existing saves are supported; make a backup before first test.
