LTS Ammunition - Ballistics Tuning Patch 1.5

用途：
作为 LTS Ammunition - Inventory System Patch 的独立附加补丁，根据“本次实际使用的弹药”修改这一发的伤害、穿甲或伤口流血。

规则：
1. 子弹 / 炮弹：伤害 x1.5，穿甲不变。
2. 大箭头 / 大火焰箭：伤害 x1.25，穿甲不变。
3. 短箭头 / 短火焰箭：伤害 x0.9，同时穿甲 x1.1。
4. 反曲箭 / 反曲火焰箭：伤害不变、穿甲不变；由这一发造成的 Stab 伤口流血率 x1.25。
5. 其他箭矢和弩矢：不额外修改伤害、穿甲或流血。

关键行为：
- 判定依据是这一发实际消耗的 ammoDef，而不是武器类型。
- 同一把武器如果能切换不同弹药，效果会随这一发弹药切换。
- 反曲箭流血加成记录在具体伤口上，并随存档保存；换弹不会改变既有伤口的倍率。
- 不修改 LTS Inventory System 的库存、NPC 生成携弹、自动补弹或套件逻辑。

依赖：
- Harmony
- [LTS] Ammunition - Framework
- LTS Ammunition - Inventory System Patch

RimWorld 1.5
