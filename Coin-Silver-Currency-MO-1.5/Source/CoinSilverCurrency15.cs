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
        internal const string HarmonyId = "allen.coin.silvercurrency.1.5";
        internal static readonly Harmony Harmony = new Harmony(HarmonyId);

        static Bootstrap()
        {
            try
            {
                Harmony.PatchAll(Assembly.GetExecutingAssembly());
                PatchCoinageTradeSessionHooks();
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    CurrencyRuntime.Initialize();
                    Log.Message("[Silver Coin Currency 1.5] Active. Trade currency = Coin_Silver; denomination 10 coins = 1 vanilla silver. MO furnace pure-metal coin remelting enabled.");
                });
            }
            catch (Exception e)
            {
                Log.Error("[Silver Coin Currency 1.5] Initialization failed: " + e);
            }
        }

        private static void PatchCoinageTradeSessionHooks()
        {
            try
            {
                MethodInfo setup = AccessTools.Method(typeof(TradeSession), nameof(TradeSession.SetupWith));
                MethodInfo close = AccessTools.Method(typeof(TradeSession), nameof(TradeSession.Close));
                HarmonyMethod postfix = new HarmonyMethod(typeof(CoinageCompatibility), nameof(CoinageCompatibility.RestoreVanillaSilverPostfix)) { priority = Priority.Last };
                if (setup != null) Harmony.Patch(setup, postfix: postfix);
                if (close != null) Harmony.Patch(close, postfix: postfix);
            }
            catch (Exception e)
            {
                Log.Warning("[Silver Coin Currency 1.5] Could not install Coinage TradeSession safety hooks: " + e.Message);
            }
        }
    }

    internal static class CurrencyRuntime
    {
        internal static ThingDef VanillaSilver;
        internal static ThingDef SilverCoin;
        internal static ThingDef GoldCoin;
        private static bool initialized;

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            VanillaSilver = DefDatabase<ThingDef>.GetNamedSilentFail("Silver");
            SilverCoin = DefDatabase<ThingDef>.GetNamedSilentFail("Coin_Silver");
            GoldCoin = DefDatabase<ThingDef>.GetNamedSilentFail("Coin_Gold");
            if (VanillaSilver == null)
            {
                Log.Error("[Silver Coin Currency 1.5] Vanilla Silver ThingDef was not found.");
                return;
            }
            if (SilverCoin == null)
            {
                Log.Error("[Silver Coin Currency 1.5] Coin_Silver was not found. Coinage (Continued) 1.5 must be active and loaded before this patch.");
                return;
            }
            ThingDefOf.Silver = VanillaSilver;
            NormalizeTraderStockGenerators();
            DisableCoinageGoldSwapBestEffort();
        }

        internal static bool Ready
        {
            get
            {
                if (!initialized) Initialize();
                return VanillaSilver != null && SilverCoin != null;
            }
        }

        internal static bool IsSilverCoin(ThingDef def) => Ready && def == SilverCoin;
        internal static bool IsLegacyCurrency(ThingDef def) => Ready && def != null && (def == VanillaSilver || (GoldCoin != null && def == GoldCoin));

        internal static int ToSilverCoinCount(Thing thing)
        {
            if (!Ready || thing == null || thing.def == null) return 0;
            float coinValue = SilverCoin.GetStatValueAbstract(StatDefOf.MarketValue);
            if (coinValue <= 0f) coinValue = 0.1f;
            float oldValue = thing.GetStatValue(StatDefOf.MarketValue);
            if (oldValue <= 0f) oldValue = thing.def.GetStatValueAbstract(StatDefOf.MarketValue);
            return Mathf.Max(0, Mathf.RoundToInt(thing.stackCount * oldValue / coinValue));
        }

        private static void NormalizeTraderStockGenerators()
        {
            FieldInfo thingDefField = AccessTools.Field(typeof(StockGenerator_SingleDef), "thingDef");
            if (thingDefField == null)
            {
                Log.Warning("[Silver Coin Currency 1.5] StockGenerator_SingleDef.thingDef was not found; runtime trade conversion remains active.");
                return;
            }
            foreach (TraderKindDef traderKind in DefDatabase<TraderKindDef>.AllDefsListForReading)
            {
                if (traderKind?.stockGenerators == null) continue;
                StockGenerator_SingleDef keeper = null;
                List<StockGenerator> remove = null;
                foreach (StockGenerator generator in traderKind.stockGenerators)
                {
                    StockGenerator_SingleDef single = generator as StockGenerator_SingleDef;
                    if (single == null) continue;
                    ThingDef def = thingDefField.GetValue(single) as ThingDef;
                    if (def != VanillaSilver && def != GoldCoin && def != SilverCoin) continue;
                    if (keeper == null)
                    {
                        keeper = single;
                        if (def != SilverCoin)
                        {
                            thingDefField.SetValue(single, SilverCoin);
                            single.countRange = ScaleRange(single.countRange, def);
                        }
                    }
                    else
                    {
                        if (remove == null) remove = new List<StockGenerator>();
                        remove.Add(single);
                    }
                }
                if (remove != null)
                    for (int i = 0; i < remove.Count; i++) traderKind.stockGenerators.Remove(remove[i]);
            }
        }

        private static IntRange ScaleRange(IntRange range, ThingDef fromDef)
        {
            float fromValue = fromDef?.GetStatValueAbstract(StatDefOf.MarketValue) ?? 1f;
            float coinValue = SilverCoin.GetStatValueAbstract(StatDefOf.MarketValue);
            if (coinValue <= 0f) coinValue = 0.1f;
            float factor = fromValue / coinValue;
            return new IntRange(Mathf.Max(0, Mathf.RoundToInt(range.min * factor)), Mathf.Max(0, Mathf.RoundToInt(range.max * factor)));
        }

        private static void DisableCoinageGoldSwapBestEffort()
        {
            try
            {
                Type currencyManager = AccessTools.TypeByName("Coinage.CurrencyManager");
                if (currencyManager == null) return;
                MethodInfo swap = AccessTools.Method(currencyManager, "SwapCurrency");
                if (swap != null)
                    Bootstrap.Harmony.Patch(swap, prefix: new HarmonyMethod(typeof(CoinageCompatibility), nameof(CoinageCompatibility.BlockCoinageSwapPrefix)) { priority = Priority.First });
                ThingDefOf.Silver = VanillaSilver;
            }
            catch (Exception e)
            {
                Log.Warning("[Silver Coin Currency 1.5] Coinage currency-swap neutralization was partial: " + e.Message);
            }
        }
    }

    internal static class CoinageCompatibility
    {
        public static bool BlockCoinageSwapPrefix()
        {
            if (CurrencyRuntime.Ready) ThingDefOf.Silver = CurrencyRuntime.VanillaSilver;
            return false;
        }
        public static void RestoreVanillaSilverPostfix()
        {
            if (CurrencyRuntime.Ready) ThingDefOf.Silver = CurrencyRuntime.VanillaSilver;
        }
    }

    [HarmonyPatch(typeof(Tradeable), nameof(Tradeable.IsCurrency), MethodType.Getter)]
    internal static class Tradeable_IsCurrency_Patch
    {
        public static void Postfix(Tradeable __instance, ref bool __result)
        {
            if (!CurrencyRuntime.Ready || __instance == null) return;
            ThingDef def = __instance.ThingDef;
            if (def == CurrencyRuntime.SilverCoin) __result = true;
            else if (def == CurrencyRuntime.VanillaSilver || (CurrencyRuntime.GoldCoin != null && def == CurrencyRuntime.GoldCoin)) __result = false;
        }
    }

    [HarmonyPatch(typeof(TraderKindDef), nameof(TraderKindDef.WillTrade), new[] { typeof(ThingDef) })]
    internal static class TraderKindDef_WillTrade_Patch
    {
        public static void Postfix(ThingDef t, ref bool __result)
        {
            if (CurrencyRuntime.IsSilverCoin(t)) __result = true;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.CurrencyTradeable), MethodType.Getter)]
    internal static class TradeDeal_CurrencyTradeable_Patch
    {
        public static void Postfix(TradeDeal __instance, ref Tradeable __result)
        {
            if (!CurrencyRuntime.Ready || TradeSession.TradeCurrency != TradeCurrency.Silver || __instance == null) return;
            Tradeable coin = __instance.AllTradeables.FirstOrDefault(t => t?.ThingDef == CurrencyRuntime.SilverCoin);
            if (coin != null) __result = coin;
        }
    }

    [HarmonyPatch(typeof(TradeDeal), "AddAllTradeables")]
    internal static class TradeDeal_AddAllTradeables_Patch
    {
        public static void Postfix(TradeDeal __instance)
        {
            if (!CurrencyRuntime.Ready || TradeSession.giftMode || TradeSession.TradeCurrency != TradeCurrency.Silver || __instance == null) return;
            if (__instance.AllTradeables.Any(t => t?.ThingDef == CurrencyRuntime.SilverCoin)) return;
            Thing zeroCoin = ThingMaker.MakeThing(CurrencyRuntime.SilverCoin);
            zeroCoin.stackCount = 0;
            Tradeable tradeable = new Tradeable();
            tradeable.AddThing(zeroCoin, Transactor.Trader);
            __instance.AllTradeables.Add(tradeable);
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.UpdateCurrencyCount))]
    internal static class TradeDeal_UpdateCurrencyCount_Patch
    {
        public static bool Prefix(TradeDeal __instance)
        {
            if (!CurrencyRuntime.Ready || __instance == null || TradeSession.giftMode || TradeSession.TradeCurrency != TradeCurrency.Silver) return true;
            Tradeable currency = __instance.AllTradeables.FirstOrDefault(t => t?.ThingDef == CurrencyRuntime.SilverCoin);
            if (currency == null) return true;
            float silverValue = 0f;
            List<Tradeable> all = __instance.AllTradeables;
            for (int i = 0; i < all.Count; i++)
            {
                Tradeable tradeable = all[i];
                if (tradeable == null || tradeable == currency || tradeable.IsCurrency) continue;
                silverValue += tradeable.CurTotalCurrencyCostForSource;
            }
            currency.ForceToSource(-Mathf.RoundToInt(silverValue * 10f));
            return false;
        }
    }

    [HarmonyPatch(typeof(TradeSession), nameof(TradeSession.SetupWith))]
    internal static class TradeSession_SetupWith_Patch
    {
        public static void Prefix(ITrader newTrader)
        {
            if (!CurrencyRuntime.Ready || newTrader == null) return;
            ThingDefOf.Silver = CurrencyRuntime.VanillaSilver;
            ConvertExistingTraderCurrency(newTrader);
        }
        public static void Postfix()
        {
            if (CurrencyRuntime.Ready) ThingDefOf.Silver = CurrencyRuntime.VanillaSilver;
        }
        private static void ConvertExistingTraderCurrency(ITrader trader)
        {
            try
            {
                List<Thing> goods = trader.Goods?.ToList();
                if (goods == null) return;
                for (int i = 0; i < goods.Count; i++)
                {
                    Thing oldThing = goods[i];
                    if (oldThing == null || !CurrencyRuntime.IsLegacyCurrency(oldThing.def)) continue;
                    ThingOwner owner = oldThing.holdingOwner;
                    if (owner == null) continue;
                    int coinCount = CurrencyRuntime.ToSilverCoinCount(oldThing);
                    if (coinCount <= 0) continue;
                    owner.Remove(oldThing);
                    oldThing.Destroy(DestroyMode.Vanish);
                    int stackLimit = Math.Max(1, CurrencyRuntime.SilverCoin.stackLimit);
                    while (coinCount > 0)
                    {
                        int stack = Math.Min(stackLimit, coinCount);
                        Thing coin = ThingMaker.MakeThing(CurrencyRuntime.SilverCoin);
                        coin.stackCount = stack;
                        if (!owner.TryAdd(coin, true))
                        {
                            coin.Destroy(DestroyMode.Vanish);
                            break;
                        }
                        coinCount -= stack;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Silver Coin Currency 1.5] Existing trader currency conversion skipped: " + e.Message);
            }
        }
    }

    [HarmonyPatch]
    internal static class StockGenerator_Currency_Postfix
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in GenTypes.AllSubclasses(typeof(StockGenerator)))
            {
                MethodInfo method = AccessTools.DeclaredMethod(type, "GenerateThings");
                if (method != null && typeof(IEnumerable<Thing>).IsAssignableFrom(method.ReturnType)) yield return method;
            }
        }
        public static void Postfix(ref IEnumerable<Thing> __result)
        {
            if (!CurrencyRuntime.Ready || __result == null) return;
            __result = Convert(__result);
        }
        private static IEnumerable<Thing> Convert(IEnumerable<Thing> source)
        {
            foreach (Thing thing in source)
            {
                if (thing == null || !CurrencyRuntime.IsLegacyCurrency(thing.def))
                {
                    yield return thing;
                    continue;
                }
                int coinCount = CurrencyRuntime.ToSilverCoinCount(thing);
                int stackLimit = Math.Max(1, CurrencyRuntime.SilverCoin.stackLimit);
                while (coinCount > 0)
                {
                    int stack = Math.Min(stackLimit, coinCount);
                    Thing coin = ThingMaker.MakeThing(CurrencyRuntime.SilverCoin);
                    coin.stackCount = stack;
                    yield return coin;
                    coinCount -= stack;
                }
            }
        }
    }

    public class SpecialThingFilterWorker_PureMetalCoin : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => t != null && CoinUtility.TryGetMetalSource(t.def, out _);
        public override bool CanEverMatch(ThingDef def) => CoinUtility.TryGetMetalSource(def, out _);
    }

    internal static class CoinUtility
    {
        internal static bool TryGetMetalSource(ThingDef coinDef, out ThingDef source)
        {
            source = null;
            if (coinDef == null || coinDef.defName.NullOrEmpty() || !coinDef.defName.StartsWith("Coin_", StringComparison.Ordinal)) return false;
            string sourceName = coinDef.defName.Substring("Coin_".Length);
            source = DefDatabase<ThingDef>.GetNamedSilentFail(sourceName);
            if (source?.stuffProps?.categories == null)
            {
                source = null;
                return false;
            }
            if (!source.stuffProps.categories.Contains(StuffCategoryDefOf.Metallic))
            {
                source = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    internal static class GenRecipe_MakeRecipeProducts_Patch
    {
        public static void Postfix(RecipeDef recipeDef, List<Thing> ingredients, ref IEnumerable<Thing> __result)
        {
            if (recipeDef?.defName != "Allen_SmeltPureMetalCoin" || ingredients == null || ingredients.Count == 0) return;
            ThingDef source;
            if (!CoinUtility.TryGetMetalSource(ingredients[0]?.def, out source) || source == null) return;
            Thing product = ThingMaker.MakeThing(source);
            product.stackCount = 1;
            __result = new[] { product };
        }
    }
}
