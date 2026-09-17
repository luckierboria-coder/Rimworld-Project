using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.CoinSilverCurrency15
{
    // V2 compatibility correction:
    // Coin_Gold is a normal trade good, NOT a legacy currency that should be
    // converted into Coin_Silver. Only vanilla Silver is normalized into the
    // silver-coin settlement currency.
    [HarmonyPatch(typeof(CurrencyRuntime), "IsLegacyCurrency")]
    internal static class CurrencyRuntime_IsLegacyCurrency_PreserveGold_Patch
    {
        public static void Postfix(ThingDef def, ref bool __result)
        {
            if (def != null && CurrencyRuntime.GoldCoin != null && def == CurrencyRuntime.GoldCoin)
                __result = false;
        }
    }

    // Replace the V1 stock-generator normalization so Coin_Gold generators are
    // left completely untouched. We only normalize vanilla Silver / Coin_Silver.
    [HarmonyPatch(typeof(CurrencyRuntime), "NormalizeTraderStockGenerators")]
    internal static class CurrencyRuntime_NormalizeTraderStockGenerators_PreserveGold_Patch
    {
        public static bool Prefix()
        {
            FieldInfo thingDefField = AccessTools.Field(typeof(StockGenerator_SingleDef), "thingDef");
            if (thingDefField == null)
            {
                Log.Warning("[Silver Coin Currency 1.5 V2] StockGenerator_SingleDef.thingDef was not found; Coin_Gold preservation remains active for runtime conversion.");
                return false;
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

                    // Deliberately do NOT include CurrencyRuntime.GoldCoin here.
                    // Gold coins remain ordinary merchandise and their stock
                    // generators must survive unchanged.
                    if (def != CurrencyRuntime.VanillaSilver && def != CurrencyRuntime.SilverCoin)
                        continue;

                    if (keeper == null)
                    {
                        keeper = single;
                        if (def != CurrencyRuntime.SilverCoin)
                        {
                            thingDefField.SetValue(single, CurrencyRuntime.SilverCoin);
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
                {
                    for (int i = 0; i < remove.Count; i++)
                        traderKind.stockGenerators.Remove(remove[i]);
                }
            }

            Log.Message("[Silver Coin Currency 1.5 V2] Coin_Gold preservation active: gold coins are normal trade goods and are never auto-converted to Coin_Silver.");
            return false; // skip V1 implementation
        }

        private static IntRange ScaleRange(IntRange range, ThingDef fromDef)
        {
            float fromValue = fromDef?.GetStatValueAbstract(StatDefOf.MarketValue) ?? 1f;
            float coinValue = CurrencyRuntime.SilverCoin.GetStatValueAbstract(StatDefOf.MarketValue);
            if (coinValue <= 0f) coinValue = 0.1f;
            float factor = fromValue / coinValue;
            return new IntRange(
                Mathf.Max(0, Mathf.RoundToInt(range.min * factor)),
                Mathf.Max(0, Mathf.RoundToInt(range.max * factor)));
        }
    }
}
