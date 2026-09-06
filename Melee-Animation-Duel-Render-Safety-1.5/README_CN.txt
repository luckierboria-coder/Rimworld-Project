MA Duel Render Safety 1.5

用途
- 修复 Melee Animation 特殊动画渲染路径中的两个空引用薄弱点。
- 重点覆盖“主武器为远程武器、Simple Sidearms 背包内有近战副武器、进入友好决斗”这类容易稳定触发特殊渲染的场景。

已确认的背景
- Melee Animation 1.5 本身会主动检测 PeteTimesSix.SimpleSidearms。
- 当主武器不是近战武器时，MA 会继续从装备列表和 Simple Sidearms 背包里寻找近战武器，因此不需要也不应该强制把副武器切成主武器。
- 旧日志中同样的“Rendering exception when doing animation 决斗/决斗弓”曾由 PawnDrawParms.pawn 为空后触发第三方 HeadgearVisible Postfix NRE。
- MA 1.5 的 AnimRenderer.Draw 另有未判空的 ov.Weapon.DrawColor 访问。

本 Patch 只做两件事
1. 在 MA 的 MakeDrawArgs 完成后，如果 PawnDrawParms.pawn 为空，则回填当前 Pawn。
2. 仅在 AM.AnimRenderer.Draw 内，把 Thing.DrawColor 调用换成 null-safe 版本；正常武器行为不变，动画武器引用为空时使用白色 tint 并只警告一次。

不会做的事
- 不强制切换主武器/副武器。
- 不改变 Simple Sidearms 的武器管理。
- 不改变决斗选择、伤害、动画概率或 AI。
- 不修改或覆盖 Melee Animation DLL。
- 不修改或覆盖 Simple Sidearms DLL。

安装
- 作为独立 Mod 放入 RimWorld/Mods。
- 在 Harmony、Melee Animation 后加载；About.xml 也声明了 loadAfter。

测试
1. 给 Pawn 主武器装备枪，Simple Sidearms 副武器放一把可用于 MA 的刀。
2. 让该 Pawn 进入友好决斗。
3. 检查是否仍有：
   [MeleeAnim] Rendering exception when doing animation 决斗
4. 如果本 Patch 实际修复了空 PawnDrawParms 或空动画武器，会最多各记录一次对应 Warning，方便确认是哪条路径被触发。

说明
当前用户提供的 2026-09-06 片段只有 [Ref 238503A6] Duplicate stacktrace，没有原始展开堆栈。因此本 Patch 针对的是源码中已确认、且历史日志已实际命中的两个渲染空引用点；若 238503A6 属于第三个不同位置，需要拿到下一次原始未折叠堆栈再继续收窄。
