using UnityEngine;
using Verse;

namespace RimOPT
{
    public sealed class RimOptMod : Mod
    {
        internal static RimOptSettings Settings = new RimOptSettings();
        private Vector2 scroll;
        public RimOptMod(ModContentPack content) : base(content) { Settings = GetSettings<RimOptSettings>(); }
        public override string SettingsCategory() { return "RimOPT"; }
        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 20f, 850f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var listing = new Listing_Standard();
            listing.Begin(view);
            #if RIMWORLD17
            listing.Label("RimOPT 0.2.0-alpha — 1.7 预览 / 模块需逐项启用验证（更改后完全重启）");
            listing.Label("注意：1.7 版本尚未经过实机功能和 TPS 验证。默认全部关闭，设置键与 1.6 隔离。建议先用测试存档，每次仅启用一项。");
#else
            listing.Label("RimOPT 0.1.4 — 模块开关（保存后需完全退出并重启游戏）");
#endif
            listing.Label("本页只控制 RimOPT，不修改 Performance Fish。勾选表示允许安装，原有兼容性检查仍可能使模块退让。");
            listing.Gap();
            listing.CheckboxLabeled("搬运合堆索引（与 PF 合堆补丁同目标）", ref Settings.HaulMergeIndexEnabled,
                "提前排除仓储组中没有合堆伙伴的物品。PF 的 WorkGiver_MergeOptimization.JobOnThing_Patch 会使本模块按原安全规则退让；本开关不会强行绕过检查。");
            listing.CheckboxLabeled("慢工作搜索尾部优化（与 PF 最近目标搜索同方法）", ref Settings.WorkSearchTailPlansEnabled,
                "只在一次 Work 搜索已超过 16 ms、原请求为 Undefined 且其他条件满足时接管。PF 优化可区域搜索请求，两者条件不同，不是已证实的冲突。其他 transpiler/finalizer（包括诊断计时器）仍可能使本模块退让。");
            listing.Gap();
            listing.Label("以下项目没有查到与当前 PF 的直接同方法冲突，也可单独关闭以便对照：");
            listing.CheckboxLabeled("可达性结果缓存", ref Settings.ReachabilityMemoEnabled,
                "一次 Work 搜索内复用 CanReach 结果。补丁链不允许接管时仍保留原有核对行为；日志 authoritative=False 表示不能跳过原计算。PF 未提供同一层的直接替代。");
            listing.CheckboxLabeled("制作工作目标索引及无效账单预筛", ref Settings.DoBillIndexEnabled,
                "减少没有待办账单的工作台候选。PF 的制作原料搜索缓存位于后续不同阶段，通常可以共存。");
            listing.CheckboxLabeled("屋顶候选预筛", ref Settings.RoofPrunerEnabled,
                "排除已经有屋顶的建顶候选格。保留原有工作检查；若 HasJobOnCell 存在未认可补丁，则按旧规则不安装。");
            listing.CheckboxLabeled("收获候选预筛", ref Settings.HarvestPrunerEnabled,
                "预筛不可收获或未成熟植物。只针对收获，不包含播种；与当前 PF 未发现直接重叠。原有兼容检查保留。");
            listing.CheckboxLabeled("清雪／清沙候选预筛", ref Settings.SnowPrunerEnabled,
                "排除不足清理深度的候选格。这不是 ReGrowth 积雪更新的优化，不能用它替代或解决 WallSnowManager 的耗时。");
            listing.Gap();
            listing.Label("本次进程启动时读取的配置：\n" + Bootstrap.AppliedConfiguration);
            listing.Label("本次进程安装状态：\n" + Bootstrap.InstallationState);
            listing.Gap();
            #if RIMWORLD17
            listing.Label("1.7 的七项默认均关闭；仅用于逐项兼容性验证。禁用的模块不会挂载其 Harmony 补丁。\n不能凭编译成功判定性能或逻辑兼容性。");
#else
            listing.Label("默认值保持旧版：七项均允许安装。关闭的模块在下次启动时完全不挂接对应补丁；不会留下仅做核对的空转钩子。全关时也不安装 Work 包作用域。\n实际速度优劣需要同档对照；设置开关不代表兼容性退让已解除。");
#endif
            listing.End();
            Widgets.EndScrollView();
        }
    }
}
