Recoverable Throwables 1.5 v1.4
RimWorld 1.5.4063

用途
- 只处理实体投掷武器：标枪、飞斧、飞刀等。
- 每次真正发射一个 Projectile，就消耗一件真实武器。
- Projectile 命中/落地/被拦截并销毁时，把同一件真实武器放回实际终点附近。
- 手雷、燃烧瓶、EMP、烟雾/毒气等爆炸/范围投掷物不由本 Patch 处理。

V1.4 修复
- V1.2/V1.3 的核心故障不是“识别不到武器”，而是 Harmony 启动阶段找不到写死的 Projectile.Launch 重载：
  Undefined target method for patch method Patch_Projectile_Launch_Recoverable::Postfix
- V1.4 删除对 Projectile.Launch 参数表的硬编码。
- 启动时从游戏实际加载的 Verse.Projectile 类型中枚举所有 Launch 方法。
- 选择参数最完整的真实 Launch 重载，再由 Harmony 直接 Patch 该 MethodBase。
- Postfix 使用 __originalMethod + __args 读取 launcher / equipment，不依赖编译期参数签名。

启动成功时应看到：
[Recoverable Throwables 1.5 v1.4] resolved Projectile.Launch(...)
[Recoverable Throwables 1.5 v1.4] ACTIVE bootstrap=...
[Recoverable Throwables 1.5 v1.4] eligible defs=...

投掷成功：
[Recoverable Throwables 1.5 v1.4] THROW ...

落地回收：
[Recoverable Throwables 1.5 v1.4] RECOVER ...

LTS Ammunition
- 请继续把实体投掷武器设置为“不需要弹药”。

Grenade One-Use
- 继续使用 v1.1。
- 爆炸类手雷由 Grenade One-Use 管。
- 实体投掷武器由本 Patch 管。
