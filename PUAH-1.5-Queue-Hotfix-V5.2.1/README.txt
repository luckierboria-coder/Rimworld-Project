PUAH 1.5 Queue Hotfix V5.2.1
================================

INSTALL
1. Exit RimWorld completely.
2. Remove / move the old V5.1 or rejected V5.2 mod folder out of RimWorld\Mods.
3. Put PUAH_1.5_QueueHotfix_V5.2.1 directly under RimWorld\Mods.
4. Enable only "PUAH 1.5 Queue Hotfix V5.2.1" after Pick Up And Haul.
5. Do NOT enable V5.1 and V5.2.1 simultaneously; they use the same packageId.

ARCHITECTURE
- PUAHQueueHotfix.dll = verified V5.1 safety layer, unchanged.
  SHA-256: f78825f2b06291341667b917339b2a91c183738aa5e66e93cf049a48cf57e6dc
- PUAHPerformanceV52.dll = V5.2.1 standalone performance layer.
  SHA-256: 3c6eba792e12c93da63aa7af2db45630a9ebd34e2dfb9e5ec9972b71539c56f7

V5.2.1 PERFORMANCE SCOPE
- Patches only PickUpAndHaul.WorkGiver_HaulToInventory.FindClosestThing.
- Uses an exact 16x16 spatial index for PUAH's private multi-haul candidate list.
- Does NOT transpile JobOnThing.
- Does NOT replace CanReach, validator, StoreUtility, reservations, capacity, RemoveAt, or Job/queue construction.
- If any indexed state is inconsistent, the call falls back to original PUAH linear FindClosestThing.

EXPECTED STARTUP LOG
[PUAH 1.5 Queue Hotfix V5.1] Applied HaulToInventory queue repair + reservation guards.
[PUAH 1.5 Queue Hotfix V5.2.1] Safe performance layer active: PUAH FindClosestThing uses an exact 16x16 spatial index. JobOnThing is NOT transpiled; original PUAH Sort/CanReach/validator/RemoveAt flow remains authoritative. V5.1 queue safety DLL is unchanged.

中文
- V5.2.1 是一个 Mod，不需要另外同时启用 V5.1。
- 包内第一颗 DLL 就是你已经验证过的 V5.1 防崩层，未重新编译、未修改。
- 第二颗 DLL 只优化 PUAH 自己 FindClosestThing 的重复线性扫描。
- V5.2 里导致 MissingFieldException 的 JobOnThing transpiler 已完全移除。
- 如果性能 DLL 有问题，删除 PUAHPerformanceV52.dll 后即回到纯 V5.1 行为。
