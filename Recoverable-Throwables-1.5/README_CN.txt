Recoverable Throwables 1.5 v1.2
RimWorld 1.5.4063

用途
- 只处理实体投掷武器：标枪、飞斧、飞刀等。
- 每次真正发射一个 Projectile，就消耗一件真实武器。
- Projectile 命中/落地/被拦截并销毁时，把同一件真实武器放回实际终点附近。
- 手雷、燃烧瓶、EMP、烟雾/毒气等爆炸/范围投掷物不由本 Patch 处理。

V1.2 关键变化
1. 不再依赖 Verb_LaunchProjectile.TryCastShot 的 Postfix 作为主逻辑。
2. 直接以 Projectile.Launch 为权威事件，因此 Verb_Shoot 和大多数自定义发射路径也能进入处理。
3. 不再只认 Combat_RangedFire_Thrown。
4. 支持“武器/投射物名称明确表示实体投掷 + 非爆炸 Projectile”的投掷武器。
5. 已覆盖 VFE Medieval 2 的 VFEM2_ThrowingAxe：
   - 它使用 Verb_Shoot
   - defaultProjectile = VFEM2V_ThrowingAxe_Thrown
   - 本身没有 Combat_RangedFire_Thrown
6. 最后一件投出时使用 Pawn_EquipmentTracker.Remove，保证装备/Verb 通知一致。
7. 飞行中的真实武器存放在 GameComponent 的 ThingOwner 中，保存/读档可追踪。
8. Projectile 飞出地图则视为该武器丢失。

LTS Ammunition
- 按当前设计，请把这些实体投掷武器在 LTS 中设置成“不需要弹药”。
- 本 Patch 不再猜测 LTS 当前设置；实体投掷武器本身就是数量/弹药。

与 Grenade One-Use
- 必须配合新版 Grenade One-Use v1.1。
- 新版 Grenade One-Use 只消耗爆炸/范围型手雷，不再吞掉实体标枪/飞斧/飞刀。
- Recoverable Throwables 仍保留兼容保护，防止旧 Grenade One-Use 对已识别实体投掷武器二次扣除。

诊断日志
启动：
[Recoverable Throwables 1.5 v1.2] ACTIVE
[Recoverable Throwables 1.5 v1.2] eligible defs=...

每次投掷：
[Recoverable Throwables 1.5 v1.2] THROW ...

成功回收：
[Recoverable Throwables 1.5 v1.2] RECOVER ...

当前 v1.2 测试版故意使用 Warning 级别打印 THROW/RECOVER，方便确认实际生命周期。
