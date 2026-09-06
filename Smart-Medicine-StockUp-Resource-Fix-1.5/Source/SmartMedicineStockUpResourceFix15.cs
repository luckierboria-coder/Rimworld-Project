using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.SmartMedicineStockUpResourceFix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                new Harmony("allen.smartmedicine.stockup.resourcefix15").PatchAll();
                Log.Message("[Smart Medicine StockUp Resource Fix 1.5] Active. Uncounted/non-resource Stock Up items use real stored Thing stacks; categorized all-item picker and search are enabled.");
            }
            catch (Exception ex)
            {
                Log.Error("[Smart Medicine StockUp Resource Fix 1.5] Failed to install: " + ex);
            }
        }
    }

    [HarmonyPatch]
    public static class EnoughAvailablePatch
    {
        private static readonly Type StockUpUtilityType = AccessTools.TypeByName("SmartMedicine.StockUpUtility");
        private static readonly Type SmartMedicineModType = AccessTools.TypeByName("SmartMedicine.Mod");
        private static readonly MethodInfo StockUpCountMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "StockUpCount", new[] { typeof(Pawn), typeof(ThingDef) });
        private static readonly MethodInfo HasItemCountMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "HasItemCount", new[] { typeof(Pawn), typeof(ThingDef) });
        private static readonly FieldInfo SettingsField = SmartMedicineModType == null ? null : AccessTools.Field(SmartMedicineModType, "settings");

        public static MethodBase TargetMethod()
        {
            return StockUpUtilityType == null
                ? null
                : AccessTools.Method(StockUpUtilityType, "EnoughAvailable", new[] { typeof(ThingDef), typeof(Map) });
        }

        public static bool Prepare()
        {
            if (TargetMethod() == null || StockUpCountMethod == null || HasItemCountMethod == null || SettingsField == null)
            {
                Log.Error("[Smart Medicine StockUp Resource Fix 1.5] Smart Medicine availability API not found; availability patch not applied.");
                return false;
            }
            return true;
        }

        public static bool Prefix(ThingDef thingDef, Map map, ref bool __result)
        {
            if (thingDef == null || map == null)
                return true;

            // Preserve Smart Medicine's original ResourceCounter path for defs RimWorld intentionally counts.
            if (thingDef.CountAsResource && thingDef.resourceReadoutPriority != ResourceCountPriority.Uncounted)
                return true;

            object settings = SettingsField.GetValue(null);
            if (settings == null)
                return true;

            FieldInfo stockUpEnoughField = AccessTools.Field(settings.GetType(), "stockUpEnough");
            if (stockUpEnoughField == null)
                return true;

            float enough = (float)stockUpEnoughField.GetValue(settings);
            if (enough == 0f)
            {
                __result = true;
                return false;
            }

            long available = 0L;

            // Match ResourceCounter's stored-resource semantics without requiring CountAsResource.
            // LWM Deep Storage contents remain in normal storage SlotGroups, so HeldThings includes them.
            var groups = map.haulDestinationManager?.AllGroupsListForReading;
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    SlotGroup group = groups[i];
                    if (group == null)
                        continue;

                    foreach (Thing heldThing in group.HeldThings)
                    {
                        if (heldThing == null)
                            continue;

                        Thing countedThing = heldThing.GetInnerIfMinified();
                        if (countedThing != null && countedThing.def == thingDef && !countedThing.IsNotFresh())
                            available += countedThing.stackCount;
                    }
                }
            }

            long requested = 0L;
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn == null || pawn.inventory == null)
                    continue;

                requested += (int)StockUpCountMethod.Invoke(null, new object[] { pawn, thingDef });
                available += (int)HasItemCountMethod.Invoke(null, new object[] { pawn, thingDef });
            }

            __result = available >= requested * (double)enough;
            return false;
        }
    }

    public enum StockCategory
    {
        Medicine,
        Ammo,
        Weapons,
        Food,
        Apparel,
        Resources,
        Misc,
        All
    }

    public sealed class StockUiState
    {
        public StockCategory Category = StockCategory.Medicine;
        public string Search = string.Empty;
    }

    [HarmonyPatch]
    public static class DialogStockUpUiPatch
    {
        private static readonly Type DialogType = AccessTools.TypeByName("SmartMedicine.Dialog_StockUp");
        private static readonly Type StockUpUtilityType = AccessTools.TypeByName("SmartMedicine.StockUpUtility");

        private static readonly FieldInfo PawnField = DialogType == null ? null : AccessTools.Field(DialogType, "pawn");
        private static readonly FieldInfo TitleField = DialogType == null ? null : AccessTools.Field(DialogType, "title");
        private static readonly FieldInfo ScrollPositionField = DialogType == null ? null : AccessTools.Field(DialogType, "scrollPosition");
        private static readonly FieldInfo ScrollViewHeightField = DialogType == null ? null : AccessTools.Field(DialogType, "scrollViewHeight");

        private static readonly MethodInfo StockingUpOnMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "StockingUpOn", new[] { typeof(Pawn), typeof(ThingDef) });
        private static readonly MethodInfo SetStockCountMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "SetStockCount", new[] { typeof(Pawn), typeof(ThingDef), typeof(int) });
        private static readonly MethodInfo StockUpStopMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "StockUpStop", new[] { typeof(Pawn), typeof(ThingDef) });
        private static readonly MethodInfo StockUpCountMethod = StockUpUtilityType == null ? null : AccessTools.Method(StockUpUtilityType, "StockUpCount", new[] { typeof(Pawn), typeof(ThingDef) });

        private static readonly ConditionalWeakTable<object, StockUiState> States = new ConditionalWeakTable<object, StockUiState>();
        private static readonly Dictionary<StockCategory, List<ThingDef>> CategoryCache = new Dictionary<StockCategory, List<ThingDef>>();

        public static MethodBase TargetMethod()
        {
            return DialogType == null ? null : AccessTools.Method(DialogType, "DoWindowContents", new[] { typeof(Rect) });
        }

        public static bool Prepare()
        {
            bool ok = TargetMethod() != null && PawnField != null && TitleField != null && ScrollPositionField != null && ScrollViewHeightField != null
                && StockingUpOnMethod != null && SetStockCountMethod != null && StockUpStopMethod != null && StockUpCountMethod != null;
            if (!ok)
                Log.Error("[Smart Medicine StockUp Resource Fix 1.5] Smart Medicine Stock Up dialog API not found; categorized picker not applied.");
            return ok;
        }

        public static bool Prefix(object __instance, Rect inRect)
        {
            Pawn pawn = PawnField.GetValue(__instance) as Pawn;
            if (pawn == null)
                return true;

            StockUiState state = States.GetOrCreateValue(__instance);
            string title = TitleField.GetValue(__instance) as string ?? "Stock Up";
            Vector2 scrollPosition = (Vector2)ScrollPositionField.GetValue(__instance);
            float scrollViewHeight = (float)ScrollViewHeightField.GetValue(__instance);

            Text.Font = GameFont.Medium;
            Rect titleRect = new Rect(inRect.x, inRect.y, inRect.width, Text.LineHeight * 1.5f);
            Widgets.Label(titleRect, title);
            Text.Font = GameFont.Small;

            float controlsY = titleRect.yMax + 4f;
            float controlsHeight = 30f;
            Rect categoryRect = new Rect(inRect.x, controlsY, 138f, controlsHeight);
            Rect searchRect = new Rect(categoryRect.xMax + 8f, controlsY, inRect.width - categoryRect.width - 8f, controlsHeight);

            if (Widgets.ButtonText(categoryRect, "分类：" + CategoryName(state.Category) + " ▼"))
                OpenCategoryMenu(state, ref scrollPosition);

            string beforeSearch = state.Search;
            state.Search = Widgets.TextField(searchRect, state.Search ?? string.Empty);
            if (!string.Equals(beforeSearch, state.Search, StringComparison.Ordinal))
                scrollPosition = Vector2.zero;

            Rect botRect = new Rect(inRect.x, controlsY + controlsHeight + 6f, inRect.width, inRect.yMax - (controlsY + controlsHeight + 6f));
            GUI.color = Color.white;

            List<ThingDef> visibleDefs = GetVisibleDefs(state.Category, state.Search);
            float iconSize = 48f;
            float cellWidth = 92f;
            int columns = Math.Max(1, Mathf.FloorToInt((botRect.width - 18f) / cellWidth));
            int rows = (visibleDefs.Count + columns - 1) / columns;
            float wantedHeight = Math.Max(botRect.height, rows * iconSize + 4f);
            if (Event.current.type == EventType.Layout)
                scrollViewHeight = wantedHeight;

            Rect outRect = new Rect(0f, 0f, botRect.width, botRect.height);
            Rect viewRect = new Rect(0f, 0f, botRect.width - 16f, Math.Max(scrollViewHeight, wantedHeight));

            Widgets.BeginGroup(botRect);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            for (int index = 0; index < visibleDefs.Count; index++)
            {
                ThingDef td = visibleDefs[index];
                int row = index / columns;
                int col = index % columns;
                Rect cell = new Rect(col * cellWidth, row * iconSize, cellWidth, iconSize);
                DrawThingCell(pawn, td, cell);
            }

            Widgets.EndScrollView();
            Widgets.EndGroup();

            ScrollPositionField.SetValue(__instance, scrollPosition);
            ScrollViewHeightField.SetValue(__instance, scrollViewHeight);

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            return false;
        }

        private static void DrawThingCell(Pawn pawn, ThingDef td, Rect cell)
        {
            Rect iconRect = new Rect(cell.x + 2f, cell.y + 2f, 42f, 42f);
            Rect countRect = new Rect(cell.x + 46f, cell.y + 14f, cell.width - 48f, 24f);
            Rect checkRect = new Rect(iconRect.xMax - 15f, iconRect.y, 15f, 15f);

            if (Mouse.IsOver(cell))
                Widgets.DrawHighlight(cell);

            if (td.graphicData != null)
                Widgets.ThingIcon(iconRect, td);
            else
                Widgets.Label(iconRect, "?");

            TooltipHandler.TipRegion(cell, td.LabelCap + "\n" + td.defName);

            bool enabled = (bool)StockingUpOnMethod.Invoke(null, new object[] { pawn, td });
            Widgets.DrawTextureFitted(checkRect, enabled ? Widgets.CheckboxOnTex : Widgets.CheckboxOffTex, 1f);

            if (Widgets.ButtonInvisible(iconRect))
            {
                if (!enabled)
                {
                    SetStockCountMethod.Invoke(null, new object[] { pawn, td, 1 });
                    enabled = true;
                }
                else
                {
                    StockUpStopMethod.Invoke(null, new object[] { pawn, td });
                    enabled = false;
                }
            }

            if (enabled)
            {
                int count = (int)StockUpCountMethod.Invoke(null, new object[] { pawn, td });
                string buffer = count.ToString();
                Widgets.TextFieldNumeric(countRect, ref count, ref buffer, 0, 9999);
                SetStockCountMethod.Invoke(null, new object[] { pawn, td, count });
            }
        }

        private static void OpenCategoryMenu(StockUiState state, ref Vector2 scrollPosition)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (StockCategory category in Enum.GetValues(typeof(StockCategory)))
            {
                StockCategory captured = category;
                options.Add(new FloatMenuOption(CategoryName(captured), delegate
                {
                    state.Category = captured;
                    state.Search = string.Empty;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
            scrollPosition = Vector2.zero;
        }

        private static string CategoryName(StockCategory category)
        {
            switch (category)
            {
                case StockCategory.Medicine: return "药物";
                case StockCategory.Ammo: return "弹药";
                case StockCategory.Weapons: return "武器";
                case StockCategory.Food: return "食物";
                case StockCategory.Apparel: return "服装";
                case StockCategory.Resources: return "原料/资源";
                case StockCategory.Misc: return "工具/杂项";
                case StockCategory.All: return "全部";
                default: return category.ToString();
            }
        }

        private static List<ThingDef> GetVisibleDefs(StockCategory category, string search)
        {
            if (!CategoryCache.TryGetValue(category, out List<ThingDef> defs))
            {
                defs = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(t => t != null && t.EverHaulable && MatchesCategory(t, category))
                    .OrderBy(t => t.LabelCap.ToString(), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                CategoryCache[category] = defs;
            }

            if (string.IsNullOrWhiteSpace(search))
                return defs;

            string needle = search.Trim();
            return defs.Where(td => ContainsIgnoreCase(td.LabelCap.ToString(), needle) || ContainsIgnoreCase(td.defName, needle)).ToList();
        }

        private static bool MatchesCategory(ThingDef td, StockCategory category)
        {
            if (category == StockCategory.All)
                return true;

            StockCategory actual = Classify(td);
            return actual == category;
        }

        private static StockCategory Classify(ThingDef td)
        {
            if (td.IsDrug || td.IsMedicine)
                return StockCategory.Medicine;

            if (td.IsWeapon)
                return StockCategory.Weapons;

            if (IsAmmo(td))
                return StockCategory.Ammo;

            if (td.ingestible != null)
                return StockCategory.Food;

            if (td.apparel != null)
                return StockCategory.Apparel;

            if (td.CountAsResource || td.resourceReadoutPriority != ResourceCountPriority.Uncounted)
                return StockCategory.Resources;

            return StockCategory.Misc;
        }

        private static bool IsAmmo(ThingDef td)
        {
            if (td.thingCategories != null)
            {
                for (int i = 0; i < td.thingCategories.Count; i++)
                {
                    ThingCategoryDef category = td.thingCategories[i];
                    int guard = 0;
                    while (category != null && guard++ < 32)
                    {
                        if (LooksLikeAmmo(category.defName) || LooksLikeAmmo(category.label))
                            return true;
                        category = category.parent;
                    }
                }
            }

            string id = td.defName ?? string.Empty;
            if (id.StartsWith("LTS_", StringComparison.OrdinalIgnoreCase))
            {
                if (LooksLikeAmmo(id))
                    return true;
            }

            return ContainsAny(id, "ammo", "ammunition", "arrow", "bolt", "bullet", "cartridge", "musketball", "cannonball", "shotshell");
        }

        private static bool LooksLikeAmmo(string text)
        {
            return ContainsAny(text, "ammo", "ammunition", "弹药", "箭矢", "箭", "弩矢", "bullet", "cartridge", "shell", "projectile", "musketball", "cannonball", "arrow", "bolt");
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            for (int i = 0; i < needles.Length; i++)
            {
                if (text.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool ContainsIgnoreCase(string text, string needle)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }
}
