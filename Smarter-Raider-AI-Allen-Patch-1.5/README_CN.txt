Smarter Raider AI - Allen Patch 1.5

这是独立 Patch Mod，不覆盖、不修改原 Smarter Raider AI 的 PogoAI.dll。

安装：
1. 保留原 Smarter Raider AI 不动。
2. 将本 Patch 文件夹作为一个独立 Mod 放进 RimWorld/Mods。
3. 在 Mod 列表中启用 Smarter Raider AI 和本 Patch；本 Patch 会自动声明 loadAfter pogo.ai。

包含：
- 可操纵炮塔只有实际有人操纵时才计入 SRAI LOS 威胁。
- 敌对 AssaultColony 持续 30/60/90 秒后，LOS 风险成本按原 Path Selection Cost 的 35/45、25/45、15/45 比例逐级降低。
- 修正 AITrashBuildingsDistant 调用 SRAI AISapper 时丢弃 Prefix 返回值的问题。
- Mount 过渡期间不插入 SRAI Follow/Attack/Mine。
- Follow/AttackMelee/Mine 目标合法性与可达性检查。
- 相同 JobDef + 相同目标的当前任务不重复创建。
- 同一 SRAI job 在 10 ticks 内连续生成 3 次时，触发 30 ticks 原版回退，打断 10-jobs-in-10-ticks job storm。
- 不 Patch 全局 Pawn_JobTracker。

卸载：
直接禁用/删除本 Patch 即可。原 Smarter Raider AI 文件无需恢复，因为从未被覆盖。
