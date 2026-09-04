Grenade One-Use Patch 1.5

目标
- 将使用 RimWorld 原版手雷式投掷逻辑的武器/投掷物改为真正的一次性用品。
- 只改变“成功投掷后消耗本体 1 个”这一件事，尽量不改原 MOD 其它行为。

识别规则
- 使用 Verb_LaunchProjectile 系列；
- rangedFireRulepack = Combat_RangedFire_Thrown；
- 投掷源不是 Apparel。

行为
- 投掷成功：当前投掷物消耗 1 个。
- 投掷失败：不消耗。
- 若其它 MOD 已允许该投掷物堆叠：stackCount 减 1；本 Patch 不修改 stackLimit。
- 若只有 1 个：从持有容器/装备栏移除后销毁。

不会改
- projectile、爆炸、烟雾、眩晕、毒气等效果；
- 射程、预热、命中、AI 目标选择；
- 配方、价格、重量；
- VWE 等 Reloadable 腰带的 charges / ammoDef；
- LTS 弹药系统。

兼容
- 原版 Frag / EMP / Molotov 及继承或复制原版 Combat_RangedFire_Thrown 逻辑的 MOD 投掷物会自动生效。
- Apparel 型烟雾/闪光/毒气腰带不会被当作一次性投掷物销毁；腰带仍可使用对应实物投掷物作为 reload ammo。
- 检测到 Combat Extended 时自动停用，避免和 CE 自带的一次性手雷逻辑重复消耗。

RimWorld: 1.5.4063
