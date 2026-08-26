PUAH 1.5 Queue Hotfix V5.1

INSTALL / UPGRADE FROM V4
1. Exit RimWorld completely.
2. Delete or move the old PUAH_1.5_QueueHotfix_Buildable_v4 folder out of RimWorld\Mods.
   V4 and V5 use the SAME packageId and must not both be installed.
3. Put this whole V5 folder directly under RimWorld\Mods.
4. Double-click START_BUILD.cmd.
5. If SUCCESS appears, 1.5\Assemblies\PUAHQueueHotfix.dll was created.
6. Enable "PUAH 1.5 Queue Hotfix V5" AFTER Pick Up And Haul.
7. Existing saves are supported; make a backup before first test.

V5.1 behavior
- If targetQueueA/countQueue contain a paired entry with count <= 0, V5 removes BOTH entries at that index.
- If a queued Thing is invalid, destroyed, despawned, or otherwise unusable, V5 removes BOTH paired entries.
- If targetQueueA/countQueue have unmatched tail entries, V5 trims only the unmatched tail.
- If valid paired entries remain, the original PUAH HaulToInventory job continues normally.
- If no valid entries remain, V5 rejects the job instead of allowing PUAH to crash.
- A second repair/guard runs immediately before TryMakePreToilReservations in case world state changed after job creation.

IMPORTANT SAFETY CHOICE
V5 NEVER changes count=0 to count=1. It does not guess the intended haul amount.
It only removes queue entries PUAH has already calculated as non-positive or unusable.

Expected startup log:
[PUAH 1.5 Queue Hotfix V5.1] Applied HaulToInventory queue repair + reservation guards.

Successful queue repairs are intentionally SILENT in V5.1. This avoids the full stack trace produced by Log.Warning for every repaired haul job.

An unrecoverable queue may still log once per pawn/stage:
[PUAH 1.5 Queue Hotfix V5.1] Rejected unrecoverable HaulToInventory job ...
That means no safe paired targets remained, so the job was discarded instead of crashing.


中文说明（V5.1）
- 保留 V5 的 count<=0 队列元素同步删除、自修复和 reservation 防崩。
- 成功修复时完全静默，不再每个 Job 输出 Warning 和完整调用栈。
- 无法修复、必须丢弃的任务仍保留一次性警告，但不再按每个 Job 生成唯一日志。
- 目的：避免 PUAH 高频坏队列在正常 Tick 中制造日志 I/O / StackTrace 微卡。
