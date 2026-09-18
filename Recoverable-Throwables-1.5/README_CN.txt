Recoverable Throwables 1.5
RimWorld 1.5.4063

用途
- 只处理“实体投掷武器”，例如标枪、飞斧、飞刀。
- 解决投掷武器本体能无限投掷的问题。
- 不负责手雷、燃烧瓶、EMP、烟雾/毒气等一次性爆炸投掷物。

核心规则
1. 识别使用 Combat_RangedFire_Thrown 的投射武器。
2. 爆炸 Projectile / explosionRadius > 0 的投掷物明确排除。
3. 若该武器在 LTS Ammunition 中仍被设置为需要弹药，本 Patch 不接管，避免“双重消耗”。
4. 成功投掷后，从当前装备堆叠中真实移除 1 件。
5. stackCount > 1：装备栏保留剩余数量。
6. stackCount = 1：最后一件投出后，装备栏清空。
7. 真实投出的那一件和飞行中的 Projectile 绑定；Projectile 命中、落地或被拦截销毁时，在实际位置附近重新掉出。
8. Projectile 飞出地图：该投掷物视为丢失。
9. 支持中途存档：飞行中的投掷物本体和 Projectile 绑定关系写入存档。
10. 使用 Thing.SplitOff(1)，尽量保留原物的 Stuff、品质、耐久和各 Comp 的拆分状态。
11. NPC 同样适用；敌人投出的武器回收后默认保持禁用（forbidden）。

与 Grenade One-Use 兼容
- 如果检测到 allen.grenade.oneuse，本 Patch 会阻止它把“可回收实体投掷武器”再次当成手雷消耗。
- 爆炸类手雷仍由 Grenade One-Use 正常处理。
- 两个 Patch 可以同时启用，不应发生双重扣除。

与 LTS Ammunition
- 推荐把标枪、飞斧、飞刀等实体投掷武器设置为“不需要弹药”。
- 如果漏掉某把武器、LTS 仍认为它需要弹药，本 Patch 会跳过该武器，不会额外消耗武器本体。
- 启动日志会打印当前识别到的 eligible defs，便于核对。

配套
- GrenadeStackCounterFix / Generic Stackable Weapons 可以继续使用，用于堆叠与 Bill 数量统计。
