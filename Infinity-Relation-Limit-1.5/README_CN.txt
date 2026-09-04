Infinity Relation Limit Patch - RimWorld 1.5

用途：
给 Infinity Faction Relationship (Continued) 增加可调的阵营关系上下限。

默认：
下限 -250
上限 +250

例子：
245 + 30 => 250
-240 - 50 => -250

功能：
1. Mod 设置里可以分别修改上下限。
2. 每次 goodwill 变化完成后钳制最终值。
3. 读取阵营关系时也会兜底钳制。
4. 存档载入完成时会扫描现有阵营关系，把已越界数值压回设置范围。

加载顺序：
Harmony
Infinity Faction Relationship (Continued)
Infinity Relation Limit Patch

这是独立 Patch，不修改原 MOD。
