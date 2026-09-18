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
        internal const string HarmonyId = "allen.coin.goldcurrency.mo.1.5.v3";
        internal static readonly Harmony Harmony = new Harmony(HarmonyId);

        static Bootstrap()
        {
            try
            {
                Harmony.PatchAll(Assembly.GetExecutingAssembly());
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Runtime.Initialize();
                    Log.Message("[Coinage Gold Currency + MO V3] Active: Coin_Gold settlement; all Coinage coins tradeable; dynamic metallic-coin smelting filters installed.");
                });
            }
            catch (Exception e) { Log.Error("[Coinage Gold Currency + MO V3] Initialization failed: " + e); }
        }
    }

    internal static class Runtime
    {
        internal static ThingDef VanillaSilver;
        internal static ThingDef GoldCoin;
        internal static readonly List<ThingDef> CoinDefs = new List<ThingDef>();
        private static bool initialized;

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            VanillaSilver = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
            GoldCoin = DefDatabase<ThingDef>.GetNamedSilentFail("Coin_Gold");
            if (VanillaSilver == null || GoldCoin == null)
            {
                Log.Error("[Coinage Gold Currency + MO V3] Required defs missing: Silver or Coin_Gold.");
                return;
            }

            CoinDefs.Clear();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (IsCoin(d)) CoinDefs.Add(d);

            InjectSmeltingFilters();
            LocalizeGeneratedCoins();
            NormalizeTraderStockGenerators();
            Log.Message("[Coinage Gold Currency + MO V3] Found " + CoinDefs.Count + " runtime Coinage coin defs; metallic=" + CoinDefs.Count(d => TryGetMetalSource(d, out _)) + ".");
        }

        internal static bool Ready { get { if (!initialized) Initialize(); return GoldCoin != null && VanillaSilver != null; } }
        internal static bool IsCoin(ThingDef d) => d != null && !d.defName.NullOrEmpty() && d.defName.StartsWith("Coin_", StringComparison.Ordinal);

        internal static bool TryGetSource(ThingDef coin, out ThingDef source)
        {
            source = null;
            if (!IsCoin(coin)) return false;
            source = DefDatabase<ThingDef>.GetNamedSilentFail(coin.defName.Substring(5));
            return source != null;
        }

        internal static bool TryGetMetalSource(ThingDef coin, out ThingDef source)
        {
            if (!TryGetSource(coin, out source)) return false;
            if (source.stuffProps?.categories == null || !source.stuffProps.categories.Contains(StuffCategoryDefOf.Metallic))
            { source = null; return false; }
            return true;
        }

        private static void InjectSmeltingFilters()
        {
            string[] names = { "Allen_SmeltPureMetalCoin10", "Allen_SmeltPureMetalCoin100", "Allen_SmeltPureMetalCoin1000", "Allen_SmeltPureMetalCoin2000" };
            int recipes = 0, coins = 0;
            foreach (string name in names)
            {
                RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail(name);
                if (r == null) continue;
                recipes++;
                foreach (ThingDef coin in CoinDefs)
                {
                    if (!TryGetMetalSource(coin, out _)) continue;
                    r.fixedIngredientFilter?.SetAllow(coin, true);
                    if (r.ingredients != null)
                        foreach (IngredientCount ing in r.ingredients) ing.filter?.SetAllow(coin, true);
                    coins++;
                }
            }
            Log.Message("[Coinage Gold Currency + MO V3] Smelting filter injection: recipes=" + recipes + ", allow entries=" + coins + ".");
        }

        private static void LocalizeGeneratedCoins()
        {
            string lang = Prefs.LangFolderName ?? "";
            if (lang.IndexOf("ChineseSimplified", StringComparison.OrdinalIgnoreCase) < 0 &&
                lang.IndexOf("简体", StringComparison.OrdinalIgnoreCase) < 0) return;

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
                        string s = source.label ?? source.defName;
                        if (s.EndsWith("锭")) s = s.Substring(0, s.Length - 1);
                        label = s + "币";
                        break;
                }
                coin.label = label;
            }
        }

        private static void NormalizeTraderStockGenerators()
        {
            FieldInfo f = AccessTools.Field(typeof(StockGenerator_SingleDef), "thingDef");
            if (f == null) return;
            foreach (TraderKindDef tk in DefDatabase<TraderKindDef>.AllDefsListForReading)
            {
                if (tk?.stockGenerators == null) continue;
                foreach (StockGenerator g in tk.stockGenerators)
                {
                    StockGenerator_SingleDef s = g as StockGenerator_SingleDef;
                    if (s == null) continue;
                    ThingDef d = f.GetValue(s) as ThingDef;
                    if (d != VanillaSilver) continue;
                    float oldV = Math.Max(0.01f, d.GetStatValueAbstract(StatDefOf.MarketValue));
                    float newV = Math.Max(0.01f, GoldCoin.GetStatValueAbstract(StatDefOf.MarketValue));
                    f.SetValue(s, GoldCoin);
                    float factor = oldV / newV;
                    s.countRange = new IntRange(Mathf.Max(0, Mathf.RoundToInt(s.countRange.min * factor)), Mathf.Max(0, Mathf.RoundToInt(s.countRange.max * factor)));
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
        public static void Postfix(ThingDef td, ref bool __result) { if (Runtime.IsCoin(td)) __result = true; }
    }

    [HarmonyPatch(typeof(Tradeable), nameof(Tradeable.IsCurrency), MethodType.Getter)]
    internal static class Tradeable_IsCurrency_Patch
    {
        public static void Postfix(Tradeable __instance, ref bool __result)
        {
            if (!Runtime.Ready || __instance == null) return;
            if (__instance.ThingDef == Runtime.GoldCoin) __result = true;
            else if (__instance.ThingDef == Runtime.VanillaSilver) __result = false;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.CurrencyTradeable), MethodType.Getter)]
    internal static class TradeDeal_CurrencyTradeable_Patch
    {
        public static void Postfix(TradeDeal __instance, ref Tradeable __result)
        {
            if (!Runtime.Ready || __instance == null || TradeSession.TradeCurrency != TradeCurrency.Silver) return;
            Tradeable gold = __instance.AllTradeables.FirstOrDefault(t => t?.ThingDef == Runtime.GoldCoin);
            if (gold != null) __result = gold;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), "AddAllTradeables")]
    internal static class TradeDeal_AddAllTradeables_Patch
    {
        public static void Postfix(TradeDeal __instance)
        {
            if (!Runtime.Ready || __instance == null || TradeSession.giftMode || TradeSession.TradeCurrency != TradeCurrency.Silver) return;
            if (__instance.AllTradeables.Any(t => t?.ThingDef == Runtime.GoldCoin)) return;
            Thing zero = ThingMaker.MakeThing(Runtime.GoldCoin); zero.stackCount = 0;
            Tradeable tr = new Tradeable(); tr.AddThing(zero, Transactor.Trader); __instance.AllTradeables.Add(tr);
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.UpdateCurrencyCount))]
    internal static class TradeDeal_UpdateCurrencyCount_Patch
    {
        public static bool Prefix(TradeDeal __instance)
        {
            if (!Runtime.Ready || __instance == null || TradeSession.giftMode || TradeSession.TradeCurrency != TradeCurrency.Silver) return true;
            Tradeable currency = __instance.AllTradeables.FirstOrDefault(t => t?.ThingDef == Runtime.GoldCoin);
            if (currency == null) return true;
            float value = 0f;
            foreach (Tradeable t in __instance.AllTradeables)
                if (t != null && t != currency && !t.IsCurrency) value += t.CurTotalCurrencyCostForSource;
            float goldV = Math.Max(0.01f, Runtime.GoldCoin.GetStatValueAbstract(StatDefOf.MarketValue));
            currency.ForceToSource(-Mathf.RoundToInt(value / goldV));
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
                    if (old == null || old.def != Runtime.VanillaSilver || old.holdingOwner == null) continue;
                    ThingOwner owner = old.holdingOwner;
                    int count = Runtime.ToGoldCount(old);
                    owner.Remove(old); old.Destroy(DestroyMode.Vanish);
                    int limit = Math.Max(1, Runtime.GoldCoin.stackLimit);
                    while (count > 0)
                    {
                        int n = Math.Min(limit, count);
                        Thing c = ThingMaker.MakeThing(Runtime.GoldCoin); c.stackCount = n;
                        if (!owner.TryAdd(c, true)) { c.Destroy(DestroyMode.Vanish); break; }
                        count -= n;
                    }
                }
            }
            catch (Exception e) { Log.Warning("[Coinage Gold Currency + MO V3] Trader currency conversion skipped: " + e.Message); }
        }
    }

    public class SpecialThingFilterWorker_PureMetalCoin : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => t != null && Runtime.TryGetMetalSource(t.def, out _);
        public override bool CanEverMatch(ThingDef def) => Runtime.TryGetMetalSource(def, out _);
    }

    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    internal static class GenRecipe_MakeRecipeProducts_Patch
    {
        public static void Postfix(RecipeDef recipeDef, List<Thing> ingredients, ref IEnumerable<Thing> __result)
        {
            if (recipeDef == null || ingredients == null || ingredients.Count == 0) return;
            int output;
            switch (recipeDef.defName)
            {
                case "Allen_SmeltPureMetalCoin10": output = 1; break;
                case "Allen_SmeltPureMetalCoin100": output = 10; break;
                case "Allen_SmeltPureMetalCoin1000": output = 100; break;
                case "Allen_SmeltPureMetalCoin2000": output = 200; break;
                default: return;
            }
            if (!Runtime.TryGetMetalSource(ingredients[0]?.def, out ThingDef source)) return;
            Thing product = ThingMaker.MakeThing(source); product.stackCount = output;
            __result = new[] { product };
        }
    }
}
