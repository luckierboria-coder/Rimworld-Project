using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

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
            listing.Label("Recycle work follows Medieval Overhaul mending timing: 30 work per remaining HP. Recycle completion destroys the item and returns materials instead of repairing it.");

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

        internal static List<Thing> BuildProducts(Thing item)
        {
            if (item == null)
                return new List<Thing>();

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
                return new List<Thing>();

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

        internal static void FinishRecycle(Pawn actor, Job job)
        {
            if (actor == null || job?.bill == null)
                return;

            Thing item = job.GetTarget(TargetIndex.B).Thing;
            if (item == null || item.Destroyed)
            {
                Log.Error("[MO Recycle Only 1.5 v1.6] Recycle completion had no valid target item.");
                actor.jobs.EndCurrentJob(JobCondition.Incompletable, true, true);
                return;
            }

            List<Thing> products = BuildProducts(item);
            List<Thing> ingredients = new List<Thing> { item };

            try
            {
                job.RecipeDef.Worker.ConsumeIngredient(item, job.RecipeDef, actor.Map);
            }
            catch (Exception ex)
            {
                Log.Error("[MO Recycle Only 1.5 v1.6] Failed consuming recycled item " + item.ToStringSafe() + ": " + ex);
                actor.jobs.EndCurrentJob(JobCondition.Errored, true, true);
                return;
            }

            job.placedThings = null;

            try
            {
                job.bill.Notify_IterationCompleted(actor, ingredients);
                RecordsUtility.Notify_BillDone(actor, products);
            }
            catch (Exception ex)
            {
                Log.Warning("[MO Recycle Only 1.5 v1.6] Bill completion notification warning: " + ex.GetType().Name + ": " + ex.Message);
            }

            foreach (Thing product in products)
            {
                if (product == null || product.Destroyed)
                    continue;

                if (!GenPlace.TryPlaceThing(product, actor.Position, actor.Map, ThingPlaceMode.Near))
                    Log.Error("[MO Recycle Only 1.5 v1.6] Could not place recycled product " + product.ToStringSafe() + " near " + actor.Position);
            }

            actor.Map?.resourceCounter?.UpdateResourceCounts();
            actor.jobs.EndCurrentJob(JobCondition.Succeeded, true, true);
        }
    }

    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("allen.mo.recycleonly15").PatchAll();
            Log.Message("[MO Recycle Only 1.5 v1.6] loaded. Recycle accepts full-durability items; work = 30 x current HP; completion produces materials.");
        }
    }

    // Keep the XML/RecipeDef base identical to MO mending.
    // Actual recycle workLeft is set directly in the MO work toil below,
    // so full-durability items do not collapse to zero work when missingHP=0.
    [HarmonyPatch(typeof(RecipeDef), nameof(RecipeDef.WorkAmountTotal))]
    internal static class Patch_RecipeDef_WorkAmountTotal
    {
        private static void Postfix(RecipeDef __instance, Thing thing, ref float __result)
        {
            if (!RecycleUtility.IsRecycleRecipe(__instance))
                return;

            __result = RecycleUtility.WorkPerHp;
        }
    }

    // MO WorkGiver_DoMending normally rejects anything at full HP:
    //   ingredient.filter.Allows(t) && t.HitPoints < t.MaxHitPoints
    //
    // That rule is correct for mending but wrong for recycling. For recycle
    // recipes only, preserve all bill/ingredient filters while removing the
    // damaged-only requirement.
    [HarmonyPatch]
    internal static class Patch_MO_WorkGiver_IsUsableIngredient
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MedievalOverhaul.WorkGiver_DoMending");
            return type == null ? null : AccessTools.Method(type, "IsUsableIngredient");
        }

        private static bool Prefix(Thing t, Bill bill, ref bool __result)
        {
            if (bill == null || !RecycleUtility.IsRecycleRecipe(bill.recipe))
                return true;

            if (t == null || !bill.IsFixedOrAllowedIngredient(t))
            {
                __result = false;
                return false;
            }

            foreach (IngredientCount ingredient in bill.recipe.ingredients)
            {
                if (ingredient.filter.Allows(t))
                {
                    __result = true;
                    return false;
                }
            }

            __result = false;
            return false;
        }
    }

    // MO initializes its work toil with:
    //   bill.GetWorkAmount(item) * (MaxHP - HitPoints)
    //
    // For full-durability recycle targets that becomes zero. Wrap the returned
    // toil and override workLeft after MO's own init has run:
    //   recycle workLeft = 30 * current HP
    //
    // This is valid for every durability, including 100%.
    [HarmonyPatch]
    internal static class Patch_MO_DoRecipeWork_Mend
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MedievalOverhaul.JobDriver_DoMending");
            return type == null ? null : AccessTools.Method(type, "DoRecipeWork_Mend");
        }

        private static void Postfix(ref Toil __result)
        {
            if (__result == null)
                return;

            Toil toil = __result;
            Action originalInit = toil.initAction;

            toil.initAction = delegate
            {
                originalInit?.Invoke();

                Pawn actor = toil.actor;
                Job job = actor?.jobs?.curJob;
                if (job == null || !RecycleUtility.IsRecycleRecipe(job.RecipeDef))
                    return;

                Thing item = job.GetTarget(TargetIndex.B).Thing;
                if (item == null)
                    return;

                object driver = actor.jobs.curDriver;
                if (driver == null)
                    return;

                FieldInfo workLeftField = AccessTools.Field(driver.GetType(), "workLeft");
                if (workLeftField != null)
                    workLeftField.SetValue(driver, RecycleUtility.WorkPerHp * Math.Max(1, item.HitPoints));
            };
        }
    }

    // Medieval Overhaul's FinishRecipeAndStartStoringProduct_Mend always heals
    // Target B to full and never calls GenRecipe.MakeRecipeProducts. Wrap the
    // returned toil's initAction: recycle recipes take our completion path,
    // normal MO mending recipes run the original initAction unchanged.
    [HarmonyPatch]
    internal static class Patch_MO_FinishRecipeAndStartStoringProduct_Mend
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("MedievalOverhaul.JobDriver_DoMending");
            return type == null ? null : AccessTools.Method(type, "FinishRecipeAndStartStoringProduct_Mend");
        }

        private static void Postfix(ref Toil __result)
        {
            if (__result == null)
                return;

            Toil toil = __result;
            Action original = toil.initAction;

            toil.initAction = delegate
            {
                Pawn actor = toil.actor;
                Job job = actor?.jobs?.curJob;

                if (job != null && RecycleUtility.IsRecycleRecipe(job.RecipeDef))
                {
                    RecycleUtility.FinishRecycle(actor, job);
                    return;
                }

                original?.Invoke();
            };
        }
    }

    // Kept as a fallback for any non-MO bill path that may execute these recipes.
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
            __result = item == null
                ? Enumerable.Empty<Thing>()
                : RecycleUtility.BuildProducts(item);
        }
    }
}
