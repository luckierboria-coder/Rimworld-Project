PUAH 1.5 Queue Hotfix V5.2

Install AFTER Pick Up And Haul. Remove/replace V5.1; V5.1 and V5.2 use the same packageId and must not both be enabled.

V5.2 keeps all V5.1 safety behavior:
- repairs paired HaulToInventory targetQueueA/countQueue entries
- removes count <= 0 and invalid/despawned paired entries
- trims unmatched queue tails
- keeps reservation-stage crash guards
- successful repairs remain silent

V5.2 performance changes (PUAH-specific only):
1. JobOnThing private haulable-list pre-sort is bypassed. PUAH immediately performs moving-center nearest searches afterward, so the O(n log n) pre-sort is redundant for that phase.
2. WorkGiver_HaulToInventory.FindClosestThing is accelerated with an exact 16x16 spatial index attached to PUAH's private List<Thing>.
3. PUAH's original GetClosestAndRemove remains in control of RemoveAt, Spawned/maxDistance handling, map.reachability.CanReach and Validator.
4. StoreUtility, Pawn reservation state, Job creation and targetQueue construction are not moved or replaced.
5. Unexpected list mutations cause an index rebuild; any unresolved inconsistency falls back to PUAH's original linear FindClosestThing for that call.

Expected startup logs:
[PUAH 1.5 Queue Hotfix V5.2] Performance layer active: ...
[PUAH 1.5 Queue Hotfix V5.2] Applied HaulToInventory queue repair + reservation guards + performance layer.

中文：
V5.2 不把 PUAH 逻辑塞进 RimMT，也不修改 RimWorld 通用 JobGiver。它只优化 PUAH 自己的多目标搬运搜索：去掉 JobOnThing 里的冗余全表 Sort，并用空间索引替代 FindClosestThing 的重复全表距离扫描。寻路可达性、Validator、仓储选择、预留与 Job 队列仍按原逻辑执行；异常情况自动回退原 PUAH 搜索。
