LTS Ammunition - Inventory System Patch (RimWorld 1.5.4063)

核心规则：
1. 套件不再储存弹药。LTS 的 Bag / Count / MaxCount / ChosenAmmo 补弹系统被绕过，套件弹药 Gizmo 隐藏。
2. 小/中/大套件仅作为背包，分别提供 +5 / +10 / +15 CarryingCapacity。
3. 所有 Pawn 的武器只使用 Inventory 中兼容的 LTS 弹药，并真实消耗；特殊 bulletDef / burstCount 保留。
4. 旧存档套件中已有弹药会在首次弹药检查时迁移到 Pawn Inventory，不直接删除。
5. 自动补弹只在当前手持需弹药武器且 Inventory 兼容弹药为 0 时触发。
6. 自动补弹只考虑 20 格内、Pawn 能直接看到、可到达且可预留的兼容弹药。
7. 一次自动补弹重量上限 = Pawn CarryingCapacity 的 1%；只取当前目标弹药堆，不连续搜多堆凑满。
8. NPC 生成时初始弹药重量目标 = CarryingCapacity 的 5%，并做 ±15% 随机浮动（约 4.25%~5.75%）。
9. NPC 搜弹触发做 0~60 ticks 随机错峰；失败后 180 ticks 再检查，避免大规模战斗同帧寻路尖峰。
10. Ammo Pack 的 Mass 做合理化修正；用户指定的 AmmoMedieval = 1 kg 保持不变。

本 MOD 是独立 Patch，不修改原 LTS MOD 或 Ammo Pack 文件。
