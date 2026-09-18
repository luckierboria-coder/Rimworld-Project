Grenade One-Use Patch 1.5 v1.1
RimWorld 1.5.4063

职责
- 只负责一次性手雷/爆炸/范围投掷物。
- 成功投掷后消耗 1 件实体物品。

V1.1 变化
- 不再因为 Combat_RangedFire_Thrown 就无条件消耗。
- 只有 Projectile_Explosive 或 explosionRadius > 0 的投射物才直接判定为手雷类。
- 对 Grenade / Molotov / Dynamite / Bomb / Explosive / Smoke / EMP / Gas 等无近战工具的一次性物品保留名称兜底。
- 标枪、飞斧、飞刀等非爆炸实体投掷武器明确排除，交给 Recoverable Throwables 1.5 v1.2。

因此：
手雷 -> Grenade One-Use -> 投出后消失
实体投掷武器 -> Recoverable Throwables -> 投出后离手 -> 落地可回收
