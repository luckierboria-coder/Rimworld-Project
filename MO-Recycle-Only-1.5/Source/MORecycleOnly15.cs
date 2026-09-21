using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MORecycleOnly15
{
    public sealed class MORecycleSettings : ModSettings
    {
        public float maxRecovery = 0.50f;
        public bool durabilityAffectsYield = true;
        public bool recoverIntricateMaterials = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref maxRecovery, "maxRecovery", 0.50f);
            Scribe_Values.Look(ref durabilityAffectsYield, "durabilityAffectsYield", true);
            Scribe_Values.Look(ref recoverIntricateMaterials, "recoverIntricateMaterials", false);
        }
    }

    public sealed class MORecycleMod : Mod
    {
        internal static MORecycleSettings Settings;

        public MORecycleMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MORecycleSettings>();
        }

        public override string SettingsCategory() => "MO Recycle Only 1.5";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var s = Settings ?? (Settings = new MORecycleSettings());
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("Maximum material recovery at 100% durability: " + Mathf.RoundToInt(s.maxRecovery * 100f) + "%");
            s.maxRecovery = listing.Slider(s.maxRecovery, 0.10f, 0.80f);

            listing.CheckboxLabeled(
                "Durability affects recovered materials",
                ref s.durabilityAffectsYield,
                "Enabled: recovered fraction = maximum recovery x current item durability. Default: 100% HP -> 50%, 50% HP -> 25%, 10% HP -> 5%.");

            listing.CheckboxLabeled(
                "Allow recovery of intricate materials/components",
                ref s.recoverIntricateMaterials,
                "Disabled by default to prevent recycling equipment back into components or other intricate manufactured parts.");

            listing.Gap();
            listing.Label("Recycle work uses Medieval Overhaul's own mending work path: 30 work per remaining HP. Repair tools are consumed by the mending bench's native fuel system.");

            listing.End();
        }
    }

    internal static class RecycleUtility
    {
        internal const float WorkPerHp = 30f;

        private static readonly HashSet<string> RecycleRecipes = new HashSet<string>
        {
            "Allen_MO_RecycleApparel",
            "Allen_MO_RecycleArmor",
            "Allen_MO_RecycleWeapon"
        };

        internal static bool IsRecycleRecipe(RecipeDef recipe)
        {
            return recipe != null && RecycleRecipes.Contains(recipe.defName);
        }
    }

    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("allen.mo.recycleonly15").PatchAll();
            Log.Message("[MO Recycle Only 1.5 v1.4] loaded. MO mending path retained; recycle actual work = 30 x current HP.");
        }
    }

    // Medieval Overhaul's JobDriver_DoMending initializes workLeft as:
    //
    //   bill.GetWorkAmount(item) * (MaxHP - HitPoints)
    //
    // and Bill.GetWorkAmount delegates to RecipeDef.WorkAmountTotal(item).
    //
    // For the original mending recipes WorkAmountTotal is simply 30, yielding:
    //   30 * missingHP
    //
    // Recycling should use the exact same 30-work-per-HP scale in reverse:
    //   30 * currentHP
    //
    // Therefore for recycle recipes we return:
    //   30 * currentHP / missingHP
    //
    // MO then multiplies by missingHP, producing exactly 30 * currentHP.
    // This also keeps MO's own progress-bar denominator consistent with workLeft.
    [HarmonyPatch(typeof(RecipeDef), nameof(RecipeDef.WorkAmountTotal))]
    internal static class Patch_RecipeDef_WorkAmountTotal
    {
        private static void Postfix(RecipeDef __instance, Thing thing, ref float __result)
        {
            if (!RecycleUtility.IsRecycleRecipe(__instance) || thing == null)
                return;

            int currentHp = Math.Max(1, thing.HitPoints);
            int missingHp = thing.MaxHitPoints - thing.HitPoints;

            // MO's WorkGiver_DoMending intentionally selects damaged items only,
            // so missingHp should normally be > 0. Keep the XML base value as a
            // safe fallback if another mod force-runs a full-durability item.
            if (missingHp <= 0)
            {
                __result = RecycleUtility.WorkPerHp;
                return;
            }

            __result = RecycleUtility.WorkPerHp * currentHp / missingHp;
        }
    }

    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    internal static class Patch_GenRecipe_MakeRecipeProducts
    {
        private static void Postfix(
            RecipeDef recipeDef,
            List<Thing> ingredients,
            ref IEnumerable<Thing> __result)
        {
            if (!RecycleUtility.IsRecycleRecipe(recipeDef))
                return;

            Thing item = ingredients?.FirstOrDefault(t => t != null && !t.Destroyed);
            if (item == null)
            {
                __result = Enumerable.Empty<Thing>();
                return;
            }

            __result = BuildProducts(item);
        }

        private static IEnumerable<Thing> BuildProducts(Thing item)
        {
            var settings = MORecycleMod.Settings ?? new MORecycleSettings();

            float recovery = Mathf.Clamp(settings.maxRecovery, 0f, 1f);
            if (settings.durabilityAffectsYield && item.MaxHitPoints > 0)
                recovery *= Mathf.Clamp01((float)item.HitPoints / item.MaxHitPoints);

            List<ThingDefCountClass> costs = null;
            try
            {
                costs = item.def.CostListAdjusted(item.Stuff);
            }
            catch (Exception ex)
            {
                Log.Warning("[MO Recycle Only 1.5] Could not read adjusted cost list for " + item.ToStringSafe() + ": " + ex.GetType().Name);
            }

            if (costs.NullOrEmpty() && !item.def.smeltProducts.NullOrEmpty())
                costs = item.def.smeltProducts;

            if (costs.NullOrEmpty() || recovery <= 0f)
                return Enumerable.Empty<Thing>();

            var totals = new Dictionary<ThingDef, int>();

            foreach (ThingDefCountClass entry in costs)
            {
                if (entry?.thingDef == null || entry.count <= 0)
                    continue;

                if (!settings.recoverIntricateMaterials && entry.thingDef.intricate)
                    continue;

                int count = Mathf.FloorToInt(entry.count * recovery + 0.0001f);
                if (count <= 0)
                    continue;

                if (totals.TryGetValue(entry.thingDef, out int existing))
                    totals[entry.thingDef] = existing + count;
                else
                    totals.Add(entry.thingDef, count);
            }

            var products = new List<Thing>();
            foreach (KeyValuePair<ThingDef, int> pair in totals)
            {
                int remaining = pair.Value;
                int stackLimit = Math.Max(1, pair.Key.stackLimit);

                while (remaining > 0)
                {
                    Thing product = ThingMaker.MakeThing(pair.Key);
                    product.stackCount = Math.Min(remaining, stackLimit);
                    remaining -= product.stackCount;
                    products.Add(product);
                }
            }

            return products;
        }
    }
}
