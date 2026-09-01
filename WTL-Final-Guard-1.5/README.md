# World Tech Level - Final Guard (RimWorld 1.5)

Compatibility patch for **World Tech Level**. It does not replace WTL and does not maintain a second tech-level database; it reuses WTL's own calculated `MinRequiredTechLevel`, faction exclusions, overrides, alternatives and filter switches.

## Guard points

- Late `PawnGenerator.GeneratePawn` guard, including a late PawnKind replacement pass.
- Final pawn sanitation after generation and gear generation.
- Non-player pawn sanitation immediately before `GenSpawn.Spawn`, catching mods that add equipment or implants after pawn generation.
- Weapons, apparel, inventory/possessions: replace with WTL alternatives when possible, otherwise remove.
- Prosthetics / implants: remove over-tech artificial Hediffs using their associated ThingDef tech level.
- Traits and xenotypes: final re-check against WTL's tech classification.
- `ThingSetMaker` results: final filter for generated loot/rewards/general item sets.
- Trader stock generators: lazy result wrapper filters anything injected above the faction's WTL limit.
- Late Quest and Incident gates.

The patch is event-driven. It does **not** scan pawns or maps every tick.

## 中文

这是 World Tech Level 的最终兜底兼容补丁，不另建科技等级数据库，而是直接复用 WTL 自己的科技等级判定、Override、阵营排除和替代品逻辑。

重点用于堵住其他 Mod 在 WTL 原过滤流程结束后又注入高科技内容的情况，例如袭击者生成完成后被追加仿生腿、枪械、护甲或库存物品。

补丁不进行 Tick 扫描，只在 Pawn/物品集/商队库存/任务事件等生成出口执行一次检查。
