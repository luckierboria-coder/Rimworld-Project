Recoverable Throwables 1.5 v1.5
RimWorld 1.5.4063

用途
- 只处理实体投掷武器：标枪、飞斧、飞刀等。
- 每次真正发射一个 Projectile，就消耗一件真实武器。
- Projectile 命中/落地/被拦截并销毁时，把同一件真实武器放回实际终点附近。
- 手雷、燃烧瓶、EMP、烟雾/毒气等爆炸/范围投掷物继续由 Grenade One-Use v1.1 处理。

V1.5 修复
1. 保留 v1.4 已确认成功的运行时 Projectile.Launch 动态解析。
2. 武器 ThingDef 现在是投掷武器识别的权威来源；运行时 Projectile 即使被 Ballistics/LTS 兼容层替换，只要仍是非爆炸投射物，也会继续处理。
3. 修正名称识别误判：不再用 Contains("throw")，因此 Gun_Slugthrower / flamethrower 不会被误认成投掷武器。
4. 增加 LAUNCH-SEEN 日志：只要已识别的实体投掷武器真正进入 Projectile.Launch，就先打印一条，再继续扣除。
5. 删除 v1.4 中错误的 Grenade One-Use “Patch 另一个 Patch 方法”逻辑。Grenade One-Use v1.1 本身已经排除实体投掷武器，不需要交叉 Harmony Patch。

启动成功应看到：
[Recoverable Throwables 1.5 v1.5] resolved Projectile.Launch(...)
[Recoverable Throwables 1.5 v1.5] ACTIVE bootstrap=...
[Recoverable Throwables 1.5 v1.5] eligible defs=...

每次实体投掷武器发射：
[Recoverable Throwables 1.5 v1.5] LAUNCH-SEEN ...
[Recoverable Throwables 1.5 v1.5] THROW ...

落地：
[Recoverable Throwables 1.5 v1.5] RECOVER ...

LTS Ammunition
- 请继续把这些实体投掷武器设置为“不需要弹药”。

Grenade One-Use
- 使用 v1.1。
- 手雷类一次性消耗；实体投掷武器由本 Patch 管。
