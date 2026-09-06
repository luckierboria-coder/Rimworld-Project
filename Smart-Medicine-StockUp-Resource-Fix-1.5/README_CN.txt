Smart Medicine StockUp Resource Fix 1.5

用途：
1. 修复 Smart Medicine 的 Stock Up 对“非 ResourceCounter 资源”错误判断为库存不足/已暂停的问题。
2. 扩展 Stock Up 选择界面，使所有可搬运 ThingDef 都能加入携带方案，而不再只显示药品。
3. 增加物品大类选择和搜索框。

库存判定修复：
RimWorld 1.5 的 ResourceCounter 只统计 CountAsResource 且 resourceReadoutPriority != Uncounted 的 ThingDef。
Smart Medicine 原版 EnoughAvailable() 却把 ResourceCounter.GetCount() 用于任意 Stock Up ThingDef，因此很多箭矢、弹药、工具、特殊物品即使仓库有大量库存，也会被判断为不足。

本 Patch 对库存判定的行为：
- 原版 ResourceCounter 会统计的物品：完全保留 Smart Medicine 原逻辑。
- Uncounted/非资源物品：按地图所有 Storage SlotGroup.HeldThings 的实际 stackCount 统计。
- 同时保留 Smart Medicine 原本把殖民者背包库存加入 available 的逻辑。
- LWM Deep Storage 的储存格仍属于 SlotGroup，所以会被正常统计。

Stock Up UI 扩展：
- 所有 EverHaulable 物品均可加入 Stock Up 携带方案。
- 顶部增加“分类”按钮，点击后选择物品大类：
  * 药物
  * 弹药
  * 武器
  * 食物
  * 服装
  * 原料/资源
  * 工具/杂项
  * 全部
- 弹药优先按 ThingCategoryDef 分类树识别，并对常见 ammo/arrow/bolt/bullet/shell 等命名以及 LTS 弹药做兼容兜底。
- 顶部增加搜索框；搜索只作用于当前分类，同时匹配本地化物品名称和 defName。
- 原 Smart Medicine 的勾选、携带数量、复制/粘贴方案、补货逻辑保持不变。

不修改：
- 不修改 RimWorld ResourceCounter。
- 不修改 LWM Deep Storage。
- 不修改/覆盖 Smart Medicine 原 DLL。
- 不改变 Stock Up 超量卸货等其它行为。

安装：
将整个文件夹作为独立 Mod 放入 RimWorld/Mods，并在 Smart Medicine 后加载。

移除：
直接禁用/删除本 Patch 即可恢复 Smart Medicine 原行为，不会污染原 Mod 文件。
