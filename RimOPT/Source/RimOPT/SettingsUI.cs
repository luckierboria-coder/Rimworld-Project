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
#if RIMWORLD17
            Rect view = new Rect(0f, 0f, inRect.width - 20f, 1500f);
#else
            Rect view = new Rect(0f, 0f, inRect.width - 20f, 850f);
#endif
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var listing = new Listing_Standard();
            listing.Begin(view);
#if RIMWORLD17
            listing.Label("RimOPT 0.4.1-alpha — 1.7 工作搜索回归修复");
            listing.Label("测试版：更新配置后需完整重启。采样是观测工具，不会自动推迟任务、改变 Pawn 工作分配或抑制其他 Mod 的异常。");
            listing.CheckboxLabeled("启用低分配卡顿采样（诊断用；默认关闭）", ref Settings.PulseDiagnosticsEnabled,
                "下次启动时记录帧间隔、主线程 Update、DoSingleTick 和 JobGiver_Work 的耗时分布。仅保存少量聚合计数，正常运行不写逐帧日志。关闭后不会安装采样 Hook。");
            listing.Gap();
            listing.Label("统计：p95/p99 为分桶上界（非精确百分位），帧间隔包含 GPU/窗口调度时间；需要先开启采样并重启。");
            listing.Label(PulseProfiler.Summary());
            if (listing.ButtonText("清空当前采样数据")) PulseProfiler.Reset();
            if (listing.ButtonText("向 Player.log 输出一次采样汇总")) Log.Message("[RimOPT/1.7] manual pulse report\n" + PulseProfiler.Summary());
            listing.Gap();
            listing.Label("工作优化开关（首次默认关闭；可直接使用测试存档启用，无须先运行采样器）：");
            listing.CheckboxLabeled("搬运合堆索引（有容量上限）", ref Settings.HaulMergeIndexEnabled,
                "仅对一个工作包内重复访问的16–128件仓储组建立索引；首次两次搜索沿用原版。每包总扫描预算256件，改动过/不安全的组回退原版。");
            listing.Label("慢搜索尾部替代：1.7 暂停使用（原版 GenClosest 区域/优先级语义未确认，旧开关被忽略）。");
            // Retain rw17_workSearchTailPlans solely as a migration setting;
            // no checkbox can enable the unsafe replacement in 0.4.1.
            listing.CheckboxLabeled("可达性结果缓存（实验性）", ref Settings.ReachabilityMemoEnabled,
                "仅在一次 Work 包内权威复用结果；存在外部 Transpiler/结果修改时整项不安装，避免无效热路径负担。");
            listing.CheckboxLabeled("工作台待做账单索引", ref Settings.DoBillIndexEnabled,
                "1.7 不再把原版区域搜索转换成全局工作台候选集；仅在原生 ThingRequest 确认空集合时快速跳过。");
            listing.CheckboxLabeled("建顶候选格预筛", ref Settings.RoofPrunerEnabled,
                "排除已有屋顶的建顶候选格，后续仍由原版进行最终判定。");
            listing.CheckboxLabeled("收获候选格预筛", ref Settings.HarvestPrunerEnabled,
                "减少无效收获格扫描。未验证所有第三方植物实现，请使用测试存档。");
            listing.CheckboxLabeled("清雪／清沙候选格预筛", ref Settings.SnowPrunerEnabled,
                "排除低于阈值的候选；后续仍由原版决定是否工作。");
            listing.Gap();
            listing.Label("本次启动读取的实际优化配置：\n" + Bootstrap.AppliedConfiguration);
            listing.Label("本次启动的实际安装状态：\n" + Bootstrap.InstallationState);
            listing.Label("重要：1.7 不考虑 Performance Fish。此页不会修复 Sized Apparel、Vehicle Map Framework 等其他 Mod 的报错。");
#else
            listing.Label("RimOPT 0.1.4 — 模块开关（保存后需完全退出并重启游戏）");
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
            listing.Label("默认值保持旧版：七项均允许安装。关闭的模块在下次启动时完全不挂接对应补丁；不会留下仅做核对的空转钩子。全关时也不安装 Work 包作用域。\n实际速度优劣需要同档对照；设置开关不代表兼容性退让已解除。");
#endif
            listing.End();
            Widgets.EndScrollView();
        }
    }
}
