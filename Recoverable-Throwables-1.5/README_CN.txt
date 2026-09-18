Recoverable Throwables 1.5 v2.0 Stable
RimWorld 1.5.4063

目标
- 把实体投掷武器从“无限投掷的远程武器”改成真正有限、可回收的物品。
- 标枪、飞斧、飞刀等本体就是弹药。
- 手雷、燃烧瓶、EMP、烟雾/毒气等爆炸/范围投掷物继续由 Grenade One-Use v1.1 处理。

稳定版功能
1. 可回收
- 每次真正发射 Projectile 时，从当前装备堆叠中消耗 1 件真实武器。
- Projectile 命中、落地或被拦截并销毁时，把同一件真实武器放回终点附近。
- Projectile 飞出地图：该武器视为丢失。
- 飞行途中保存/读档由 GameComponent + ThingOwner 保存实体物品。

2. 可堆叠
- 所有被识别为实体投掷武器的 ThingDef，stackLimit 至少提升到 10。
- 不改手雷类物品。
- 已有更高 stackLimit 的 Def 不会被降低。
- 堆叠仍遵守 RimWorld 原本的 Stuff / Quality / HP / Comp 兼容规则。

3. Pawn 生成携带多件
- PawnGenerator 生成装备后，如果 Primary 是实体投掷武器且仍是单件，则自动扩展为一组。
- 轻型武器（质量 <= 0.75）：4–6 件。
- 普通武器：3–5 件。
- 重型武器（质量 > 2.0）：2–4 件。
- 数量不会超过该武器 stackLimit。
- 如果别的 MOD 已经给这把武器设置了 stackCount > 1，本 MOD 不覆盖。
- 只影响新生成/重新生成装备的 Pawn，不会凭空给现有 Pawn 补充投掷武器。

4. 兼容
- LTS Ammunition：请继续把实体投掷武器设置为“不需要弹药”。
- Grenade One-Use v1.1：继续保留；它只负责手雷类一次性投掷物。
- GrenadeStackCounterFix / Generic Stackable Weapons：可以继续使用，用于 Bill 对 stackable weapon 的数量统计。

当前已验证实体投掷武器包括：
- Pila
- pphhyy_Barbarian_RustedJavalin
- pphhyy_Barbarian_RustedThrowingAxe
- VFEC_Javelin
- VFEM2_ThrowingAxe

日志
稳定版不再为每次成功 THROW / RECOVER 刷 Warning。
启动时只打印：
- Projectile.Launch 解析结果
- active 状态
- eligible 数量 / stackLimit / defs
异常和兼容失败仍打印 Error。

核心规则
玩家与 NPC 使用同一套逻辑。
