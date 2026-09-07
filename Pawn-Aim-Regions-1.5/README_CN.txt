Pawn Aim Regions 1.5

功能：
- 玩家阵营 Humanlike Pawn：每个小人单独拥有“瞄准：头部/躯干/下身”Gizmo。
- 玩家 Pawn 默认：躯干。
- NPC Humanlike Pawn：首次需要该设置时随机并固定，权重：头部35%、躯干45%、下身20%。设置随存档保存。
- 如果 NPC 被招募为玩家 Pawn，则切换为玩家默认躯干；如果玩家 Pawn 日后重新成为 NPC，则重新按 NPC 权重随机一次。
- 近战与远程共用同一套命中部位选择。

身体区域：
- 头部：FullHead 及其子部位，例如头、颅骨、脑、眼、耳、鼻、下颌。
- 躯干：Torso 组，但骨盆归入下身。
- 下身：骨盆/腰部、腿以及腿的子部位（脚、脚趾等）。
- 对缺少标准 Humanlike 部位组的异种族，自动回退到 Top/Middle/Bottom。

兼容原则：
- 不修改武器伤害、护甲、穿甲、射速或命中率。
- 区域内部仍沿用原版 coverageAbs × damageDef hitChanceFactor 权重随机。
- 若别的系统已经明确限制 BodyPartHeight，则不覆盖。
- 若 DamageInfo 已经明确指定 HitPart，原版不会进入本 Mod 的选择函数，因此保持不变。
- 仅 Pawn 对敌对 Pawn 的伤害会使用该偏好；环境伤害不受影响。

版本：RimWorld 1.5 / 编译参考 1.5.4063
依赖：Harmony
