using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.CoinSilverCurrency15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        internal const string HarmonyId = "allen.coin.goldcurrency.mo.1.5.v31";
        internal static readonly Harmony Harmony = new Harmony(HarmonyId);

        static Bootstrap()
        {
            try
            {
                Harmony.PatchAll(Assembly.GetExecutingAssembly());
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try
                    {
                        Runtime.Initialize();
                    }
                    catch (Exception e)
                    {
                        Log.Error("[Coinage Gold Currency + MO V3.1] Post-load initialization failed: " + e);
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error("[Coinage Gold Currency + MO V3.1] Harmony initialization failed: " + e);
            }
        }
    }

    internal static class Runtime
    {
        internal static ThingDef VanillaSilver;
        internal static ThingDef GoldCoin;
        internal static readonly List<ThingDef> CoinDefs = new List<ThingDef>();
        private static bool initialized;
        private static bool initializing;

        internal static bool Ready => initialized && VanillaSilver != null && GoldCoin != null;

        internal static void Initialize()
        {
            if (initialized || initializing) return;
            initializing = true;
            try
            {
                VanillaSilver = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
                GoldCoin = DefDatabase<ThingDef>.GetNamedSilentFail("Coin_Gold");

                if (VanillaSilver == null || GoldCoin == null)
                {
                    Log.Error("[Coinage Gold Currency + MO V3.1] Required defs missing after play-data load: Silver or Coin_Gold.");
                    return;
                }

                CoinDefs.Clear();
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (IsCoin(d) && TryGetSource(d, out _))
                        CoinDefs.Add(d);
                }

                InjectSmeltingFilters();
                LocalizeGeneratedCoins();
                NormalizeTraderStockGenerators();

                initialized = true;

                List<string> metallic = new List<string>();
                foreach (ThingDef d in CoinDefs)
                    if (TryGetMetalSource(d, out _)) metallic.Add(d.defName);

                Log.Message("[Coinage Gold Currency + MO V3.1] Active. Coin_Gold settlement; runtime coins=" +
                    CoinDefs.Count + "; metallic=" + metallic.Count + "; metallic defs=[" +
                    string.Join(", ", metallic.ToArray()) + "].");
            }
            finally
            {
                initializing = false;
            }
        }

        internal static bool IsCoin(ThingDef d)
        {
            return d != null && !d.defName.NullOrEmpty() &&
                   d.defName.StartsWith("Coin_", StringComparison.Ordinal);
        }

        internal static bool TryGetSource(ThingDef coin, out ThingDef source)
        {
            source = null;
            if (!IsCoin(coin)) return false;
            string sourceName = coin.defName.Substring(5);
            if (sourceName.NullOrEmpty()) return false;
            source = DefDatabase<ThingDef>.GetNamedSilentFail(sourceName);
            return source != null;
        }

        internal static bool TryGetMetalSource(ThingDef coin, out ThingDef source)
        {
            if (!TryGetSource(coin, out source)) return false;
            if (source.stuffProps?.categories == null ||
                !source.stuffProps.categories.Contains(StuffCategoryDefOf.Metallic))
            {
                source = null;
                return false;
            }
            return true;
        }

        private static void InjectSmeltingFilters()
        {
            string[] names =
            {
                "Allen_SmeltPureMetalCoin10",
                "Allen_SmeltPureMetalCoin100",
                "Allen_SmeltPureMetalCoin1000",
                "Allen_SmeltPureMetalCoin2000"
            };

            List<ThingDef> metalCoins = CoinDefs.Where(d => TryGetMetalSource(d, out _)).ToList();
            int recipes = 0;

            foreach (string name in names)
            {
                RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(name);
                if (recipe == null)
                {
                    Log.Error("[Coinage Gold Currency + MO V3.1] Missing recipe def: " + name);
                    continue;
                }

                recipes++;
                if (recipe.fixedIngredientFilter == null)
                    recipe.fixedIngredientFilter = new ThingFilter();

                foreach (ThingDef coin in metalCoins)
                    recipe.fixedIngredientFilter.SetAllow(coin, true);

                if (recipe.ingredients != null)
                {
                    foreach (IngredientCount ingredient in recipe.ingredients)
                    {
                        if (ingredient.filter == null)
                            ingredient.filter = new ThingFilter();

                        foreach (ThingDef coin in metalCoins)
                            ingredient.filter.SetAllow(coin, true);
                    }
                }
            }

            Log.Message("[Coinage Gold Currency + MO V3.1] Smelting filters injected: recipes=" +
                        recipes + ", metallic coin defs per recipe=" + metalCoins.Count + ".");
        }

        private static void LocalizeGeneratedCoins()
        {
            string lang = Prefs.LangFolderName ?? "";
            if (lang.IndexOf("ChineseSimplified", StringComparison.OrdinalIgnoreCase) < 0 &&
                lang.IndexOf("简体", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            foreach (ThingDef coin in CoinDefs)
            {
                if (!TryGetSource(coin, out ThingDef source)) continue;

                string label;
                switch (source.defName)
                {
                    case "Gold": label = "金币"; break;
                    case "Silver": label = "银币"; break;
                    case "Steel": label = "钢币"; break;
                    case "Plasteel": label = "塑钢币"; break;
                    case "Uranium": label = "铀币"; break;
                    case "Jade": label = "翡翠币"; break;
                    case "WoodLog": label = "木币"; break;
                    case "DankPyon_IronIngot": label = "铁币"; break;
                    case "CopperIngot": label = "铜币"; break;
                    case "TinIngot": label = "锡币"; break;
                    default:
                        string sourceLabel = source.label ?? source.defName;
                        if (sourceLabel.EndsWith("锭", StringComparison.Ordinal))
                            sourceLabel = sourceLabel.Substring(0, sourceLabel.Length - 1);
                        label = sourceLabel + "币";
                        break;
                }

                coin.label = label;
            }
        }

        private static void NormalizeTraderStockGenerators()
        {
            FieldInfo f = AccessTools.Field(typeof(StockGenerator_SingleDef), "thingDef");
            if (f == null)
            {
                Log.Warning("[Coinage Gold Currency + MO V3.1] StockGenerator_SingleDef.thingDef field not found; runtime trader conversion remains enabled.");
                return;
            }

            foreach (TraderKindDef tk in DefDatabase<TraderKindDef>.AllDefsListForReading)
            {
                if (tk?.stockGenerators == null) continue;

                foreach (StockGenerator generator in tk.stockGenerators)
                {
                    StockGenerator_SingleDef single = generator as StockGenerator_SingleDef;
                    if (single == null) continue;

                    ThingDef d = f.GetValue(single) as ThingDef;
                    if (d != VanillaSilver) continue;

                    float oldV = Math.Max(0.01f, d.GetStatValueAbstract(StatDefOf.MarketValue));
                    float newV = Math.Max(0.01f, GoldCoin.GetStatValueAbstract(StatDefOf.MarketValue));
                    float factor = oldV / newV;

                    f.SetValue(single, GoldCoin);
                    single.countRange = new IntRange(
                        Mathf.Max(0, Mathf.RoundToInt(single.countRange.min * factor)),
                        Mathf.Max(0, Mathf.RoundToInt(single.countRange.max * factor)));
                }
            }
        }

        internal static int ToGoldCount(Thing t)
        {
            if (t == null || !Ready) return 0;

            float oldV = Math.Max(0f, t.GetStatValue(StatDefOf.MarketValue));
            float goldV = Math.Max(0.01f, GoldCoin.GetStatValueAbstract(StatDefOf.MarketValue));
            return Mathf.Max(0, Mathf.RoundToInt(t.stackCount * oldV / goldV));
        }
    }

    [HarmonyPatch(typeof(TraderKindDef), nameof(TraderKindDef.WillTrade), new[] { typeof(ThingDef) })]
    internal static class TraderKindDef_WillTrade_Patch
    {
        public static void Postfix(ThingDef td, ref bool __result)
        {
            if (Runtime.IsCoin(td) && Runtime.TryGetSource(td, out _))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(Tradeable), nameof(Tradeable.IsCurrency), MethodType.Getter)]
    internal static class Tradeable_IsCurrency_Patch
    {
        public static void Postfix(Tradeable __instance, ref bool __result)
        {
            if (!Runtime.Ready || __instance == null) return;

            ThingDef d = __instance.ThingDef;
            if (Runtime.IsCoin(d))
                __result = d == Runtime.GoldCoin;
            else if (d == Runtime.VanillaSilver)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.CurrencyTradeable), MethodType.Getter)]
    internal static class TradeDeal_CurrencyTradeable_Patch
    {
        public static void Postfix(TradeDeal __instance, ref Tradeable __result)
        {
            if (!Runtime.Ready || __instance == null || TradeSession.TradeCurrency != TradeCurrency.Silver)
                return;

            Tradeable gold = __instance.AllTradeables.FirstOrDefault(t => t?.ThingDef == Runtime.GoldCoin);
            if (gold != null)
                __result = gold;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), "AddAllTradeables")]
    internal static class TradeDeal_AddAllTradeables_Patch
    {
        public static void Postfix(TradeDeal __instance)
        {
            if (!Runtime.Ready || __instance == null || TradeSession.giftMode ||
                TradeSession.TradeCurrency != TradeCurrency.Silver)
                return;

            if (__instance.AllTradeables.Any(t => t?.ThingDef == Runtime.GoldCoin))
                return;

            Thing zero = ThingMaker.MakeThing(Runtime.GoldCoin);
            zero.stackCount = 0;

            Tradeable tradeable = new Tradeable();
            tradeable.AddThing(zero, Transactor.Trader);
            __instance.AllTradeables.Add(tradeable);
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.UpdateCurrencyCount))]
    internal static class TradeDeal_UpdateCurrencyCount_Patch
    {
        public static bool Prefix(TradeDeal __instance)
        {
            if (!Runtime.Ready || __instance == null || TradeSession.giftMode ||
                TradeSession.TradeCurrency != TradeCurrency.Silver)
                return true;

            Tradeable currency = __instance.AllTradeables.FirstOrDefault(t => t?.ThingDef == Runtime.GoldCoin);
            if (currency == null)
                return true;

            float silverValue = 0f;
            foreach (Tradeable t in __instance.AllTradeables)
            {
                if (t == null || t == currency || t.IsCurrency) continue;
                silverValue += t.CurTotalCurrencyCostForSource;
            }

            float goldValue = Math.Max(0.01f, Runtime.GoldCoin.GetStatValueAbstract(StatDefOf.MarketValue));
            currency.ForceToSource(-Mathf.RoundToInt(silverValue / goldValue));
            return false;
        }
    }

    [HarmonyPatch(typeof(TradeSession), nameof(TradeSession.SetupWith))]
    internal static class TradeSession_SetupWith_Patch
    {
        public static void Prefix(ITrader newTrader)
        {
            if (!Runtime.Ready || newTrader == null) return;

            try
            {
                List<Thing> goods = newTrader.Goods?.ToList();
                if (goods == null) return;

                foreach (Thing old in goods)
                {
                    if (old == null || old.def != Runtime.VanillaSilver || old.holdingOwner == null)
                        continue;

                    ThingOwner owner = old.holdingOwner;
                    int count = Runtime.ToGoldCount(old);

                    owner.Remove(old);
                    old.Destroy(DestroyMode.Vanish);

                    int limit = Math.Max(1, Runtime.GoldCoin.stackLimit);
                    while (count > 0)
                    {
                        int n = Math.Min(limit, count);
                        Thing coin = ThingMaker.MakeThing(Runtime.GoldCoin);
                        coin.stackCount = n;

                        if (!owner.TryAdd(coin, true))
                        {
                            coin.Destroy(DestroyMode.Vanish);
                            break;
                        }

                        count -= n;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Coinage Gold Currency + MO V3.1] Trader currency conversion skipped: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    internal static class GenRecipe_MakeRecipeProducts_Patch
    {
        public static void Postfix(RecipeDef recipeDef, List<Thing> ingredients, ref IEnumerable<Thing> __result)
        {
            if (recipeDef == null || ingredients == null || ingredients.Count == 0)
                return;

            int outputCount;
            switch (recipeDef.defName)
            {
                case "Allen_SmeltPureMetalCoin10": outputCount = 1; break;
                case "Allen_SmeltPureMetalCoin100": outputCount = 10; break;
                case "Allen_SmeltPureMetalCoin1000": outputCount = 100; break;
                case "Allen_SmeltPureMetalCoin2000": outputCount = 200; break;
                default: return;
            }

            ThingDef source = null;
            foreach (Thing ingredient in ingredients)
            {
                if (!Runtime.TryGetMetalSource(ingredient?.def, out ThingDef current))
                    return;

                if (source == null)
                    source = current;
                else if (source != current)
                {
                    Log.Error("[Coinage Gold Currency + MO V3.1] Mixed source metals reached a no-mixing smelt recipe; refusing dynamic output.");
                    return;
                }
            }

            if (source == null) return;

            int remaining = outputCount;
            int stackLimit = Math.Max(1, source.stackLimit);
            List<Thing> products = new List<Thing>();

            while (remaining > 0)
            {
                int n = Math.Min(stackLimit, remaining);
                Thing product = ThingMaker.MakeThing(source);
                product.stackCount = n;
                products.Add(product);
                remaining -= n;
            }

            __result = products;
        }
    }
}
