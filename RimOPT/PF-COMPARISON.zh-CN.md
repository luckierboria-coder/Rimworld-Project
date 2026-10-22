# RimOPT 0.1.4 与 Performance Fish 的功能选择

本表基于当前安装的 PF 0.6.2.0 反编译代码和 RimOPT 0.1.3/0.1.4 源码。0.1.4 只增加模块开关，不更改算法和安全避让，不替用户关闭 PF 项目，也不自动启用当前已停用的 RimOPT。这里的优先建议是代码与现有瓶颈的判断，不是已完成的两模组性能对照。

| RimOPT 开关 | PF 对应项／关系 | 实际区别与选择建议 |
|---|---|---|
| 搬运合堆索引 | `JobSystem.WorkGiver_MergeOptimization.JobOnThing_Patch`；同为 `WorkGiver_Merge.JobOnThing` | PF 只在物品不属于储存组时清除失效候选；RimOPT 为当前工作包建立仓储组合堆伙伴索引，提前排除不可能合堆的对象。当前日志里约 34–44 ms 的合堆搜索，RimOPT 的设计更对症，但尚无启用后的实测证明。PF 此项开启时，RimOPT 保持原规则退让。要比较，应只关闭 PF 此项、开启 RimOPT 此项并重启，不是关闭整个 PF。其他补丁仍可能使索引退让。 |
| 慢工作搜索尾部优化 | `JobSystem.GenClosestPatches.ClosestThingReachablePatch`；同为 `GenClosest.ClosestThingReachable` | 不是已确认互斥：PF 针对可区域搜索的 request 调整搜索方式；RimOPT 接管要求原 request 已为 Undefined，且工作包已超过 16 ms 等条件成立。实际接管条件不同，通常无需二选一。若以稳定和覆盖面为先，可保留 PF、单独关闭 RimOPT 尾部路径做基线。若要试当前慢搜索，再单独开启 RimOPT 对照，不宣称必然更快。 |
| 可达性结果缓存 | 当前 PF 没有找到同层直接替代 | PF 的组件／属性缓存不是这项 CanReach 结果复用。旧日志 RimOPT 显示 `authoritative=False`：维护、核对缓存但不能省略原计算。对那种补丁链，建议优先关闭 RimOPT 此项，避免额外开销；这不是“让 PF 接管同一功能”。 |
| 制作目标索引及无效账单预筛 | PF 优化制作原料搜索，阶段不同 | RimOPT 先减少无待办账单的工作台候选；PF 在后续寻找制作原料时优化。没有确认直接冲突，可以并用。不应该为开启 RimOPT 这项而关闭 PF 的制作原料缓存。 |
| 屋顶候选预筛 | 未发现当前 PF 直接对应 | RimOPT 排除已有屋顶的建顶候选。这里不是 PF/RimOPT 竞争项，可独立选择。 |
| 收获候选预筛 | 未发现当前 PF 直接对应 | 仅针对收获，不包含播种。不能用它解释或解决全部 GrowerSow 耗时。 |
| 清雪／清沙候选预筛 | 未发现当前 PF 直接对应 | 排除深度不足的清理候选，不是 ReGrowth 的 WallSnowManager 更新优化，也不是 GC 优化。 |

## 诊断模组与安全退让

- WorkTrace 给 HasJobOnCell 加的计时钩子，会被原候选预筛安全检查视为外来补丁，因而可能让屋顶／收获／清雪预筛不安装。
- StallTrace 给 GenClosest 加的 finalizer，会使 RimOPT 的尾部路径按旧规则不接管。开关勾选不意味着消除该限制。
- CanReach 上的诊断 finalizer 或其他模组 transpiler，会使可达性缓存不能权威复用结果。
- 本次没有增加“忽略所有外来补丁”开关，也没有静默将计时器加入白名单。需要实测时，应把“优化模块是否实际接管”作为单独验收条件。

## 范围

Processor Ingredient Search 专项补丁处理 ProcessorFramework 的嵌套原料验证；RimOPT 尾部路径只接受原 JobGiver_Work 验证器，不是该专项补丁的替代。当前日志已经验证 Processor 专项补丁将这部分可达性调用降至约 182 次，而 RimOPT 合堆效果仍待另行测试。
