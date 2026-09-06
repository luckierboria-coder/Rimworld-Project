Smart Medicine StockUp Resource Fix 1.5

用途：
修复 Smart Medicine 的 Stock Up 对“非 ResourceCounter 资源”错误判断为库存不足/已暂停的问题。

原因：
RimWorld 1.5 的 ResourceCounter 只统计 CountAsResource 且 resourceReadoutPriority != Uncounted 的 ThingDef。
Smart Medicine 原版 EnoughAvailable() 却把 ResourceCounter.GetCount() 用于任意 Stock Up ThingDef，因此很多箭矢、弹药、工具、特殊物品即使仓库有大量库存，也会被判断为不足。

本 Patch 行为：
- 原版 ResourceCounter 会统计的物品：完全保留 Smart Medicine 原逻辑。
- Uncounted/非资源物品：按地图所有 Storage SlotGroup.HeldThings 的实际 stackCount 统计。
- 同时保留 Smart Medicine 原本把殖民者背包库存加入 available 的逻辑。
- LWM Deep Storage 的储存格仍属于 SlotGroup，所以会被正常统计。
- 不修改 RimWorld ResourceCounter。
- 不修改 LWM Deep Storage。
- 不修改 Smart Medicine 原 DLL。
- 不改变 Stock Up 超量卸货等其它行为；本版本只修库存充足判定。

安装：
将整个文件夹作为独立 Mod 放入 RimWorld/Mods，并在 Smart Medicine 后加载。

移除：
直接禁用/删除本 Patch 即可恢复 Smart Medicine 原行为，不会污染原 Mod 文件。
