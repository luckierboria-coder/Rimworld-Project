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
            listing.Label("Recycle duration is controlled only by each recycle RecipeDef's workAmount. Durability, material, MaxHP, pawn work speed and bench speed do not affect duration.");
            RecipeDef apparel = DefDatabase<RecipeDef>.GetNamedSilentFail("Allen_MO_RecycleApparel");
            RecipeDef armor = DefDatabase<RecipeDef>.GetNamedSilentFail("Allen_MO_RecycleArmor");
            RecipeDef weapon = DefDatabase<RecipeDef>.GetNamedSilentFail("Allen_MO_RecycleWeapon");
            listing.Label("Runtime workAmount — Apparel: " + (apparel != null ? apparel.workAmount.ToString("0.##") : "missing") +
                " | Armor: " + (armor != null ? armor.workAmount.ToString("0.##") : "missing") +
                " | Weapon: " + (weapon != null ? weapon.workAmount.ToString("0.##") : "missing"));

            listing.End();
        }
    }

    internal static class RecycleUtility
    {
        internal const float FallbackRecycleTicks = 5000f;

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
                Log.Error("[MO Recycle Only 1.5 v1.9] Recycle completion had no valid target item.");
                actor.jobs.EndCurrentJob(JobCondition.Incompletable, true, true);
                return;
            }

            List<Thing> products = BuildProducts(item);
            List<Thing> ingredients = new List<Thing> { item };

            // Mirror MO's normal mending XP award because recycle completion
            // bypasses MO's original finish initAction.
            if (job.RecipeDef.workSkill != null && actor.skills != null)
            {
                FieldInfo ticksField = AccessTools.Field(actor.jobs.curDriver.GetType(), "ticksSpentDoingRecipeWork");
                int ticksSpent = ticksField != null ? (int)ticksField.GetValue(actor.jobs.curDriver) : 0;
                float xp = ticksSpent * 0.1f * job.RecipeDef.workSkillLearnFactor;
                actor.skills.GetSkill(job.RecipeDef.workSkill).Learn(xp, false, false);
            }

            try
            {
                job.RecipeDef.Worker.ConsumeIngredient(item, job.RecipeDef, actor.Map);
            }
            catch (Exception ex)
            {
                Log.Error("[MO Recycle Only 1.5 v1.9] Failed consuming recycled item " + item.ToStringSafe() + ": " + ex);
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
                Log.Warning("[MO Recycle Only 1.5 v1.9] Bill completion notification warning: " + ex.GetType().Name + ": " + ex.Message);
            }

            foreach (Thing product in products)
            {
                if (product == null || product.Destroyed)
                    continue;

                if (!GenPlace.TryPlaceThing(product, actor.Position, actor.Map, ThingPlaceMode.Near))
                    Log.Error("[MO Recycle Only 1.5 v1.9] Could not place recycled product " + product.ToStringSafe() + " near " + actor.Position);
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
            Log.Message("[MO Recycle Only 1.5 v1.9] loaded. Recycle work toil bypasses MO work initialization and reads RecipeDef.workAmount directly.");
            foreach (string defName in new[] { "Allen_MO_RecycleApparel", "Allen_MO_RecycleArmor", "Allen_MO_RecycleWeapon" })
            {
                RecipeDef def = DefDatabase<RecipeDef>.GetNamedSilentFail(defName);
                if (def != null)
                    Log.Message("[MO Recycle Only 1.5 v1.9] Runtime " + defName + ".workAmount = " + def.workAmount);
            }
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

    // MO's mending toil normally scales work by missing HP and pawn/table work
    // speed. Recycle ignores all of that and uses RecipeDef.workAmount directly.
    //
    // We preserve MO's bill notifications, repair-tool fuel use, comfort and
    // completion hand-off, but decrement recycle workLeft by exactly 1 per game
    // tick. Therefore XML workAmount is the sole duration control.
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
            Action originalTick = toil.tickAction;

            Type driverType = AccessTools.TypeByName("MedievalOverhaul.JobDriver_DoMending");
            FieldInfo workLeftField = driverType == null ? null : AccessTools.Field(driverType, "workLeft");
            FieldInfo ticksSpentField = driverType == null ? null : AccessTools.Field(driverType, "ticksSpentDoingRecipeWork");
            FieldInfo billStartTickField = driverType == null ? null : AccessTools.Field(driverType, "billStartTick");

            toil.initAction = delegate
            {
                Pawn actor = toil.actor;
                Job job = actor?.jobs?.curJob;

                if (job == null || !RecycleUtility.IsRecycleRecipe(job.RecipeDef))
                {
                    originalInit?.Invoke();
                    return;
                }

                object driver = actor.jobs.curDriver;
                if (driver == null)
                    return;

                float configuredWork = job.RecipeDef != null && job.RecipeDef.workAmount > 0f
                    ? job.RecipeDef.workAmount
                    : RecycleUtility.FallbackRecycleTicks;

                workLeftField?.SetValue(driver, configuredWork);
                ticksSpentField?.SetValue(driver, 0);
                billStartTickField?.SetValue(driver, Find.TickManager.TicksGame);
                job.bill.Notify_BillWorkStarted(actor);

                Thing item = job.GetTarget(TargetIndex.B).Thing;
                Log.Message("[MO Recycle Only 1.5 v1.9] Recycle start: recipe=" +
                    job.RecipeDef.defName +
                    ", XML workAmount=" + job.RecipeDef.workAmount +
                    ", actual workLeft=" + configuredWork +
                    ", item=" + item.ToStringSafe());
            };

            toil.tickAction = delegate
            {
                Pawn actor = toil.actor;
                Job job = actor?.jobs?.curJob;

                if (job == null || !RecycleUtility.IsRecycleRecipe(job.RecipeDef))
                {
                    originalTick?.Invoke();
                    return;
                }

                object driver = actor.jobs.curDriver;
                Thing item = job.GetTarget(TargetIndex.B).Thing;
                if (driver == null || item == null || item.Destroyed)
                {
                    actor.jobs.EndCurrentJob(JobCondition.Incompletable, true, true);
                    return;
                }

                int ticksSpent = ticksSpentField != null ? (int)ticksSpentField.GetValue(driver) : 0;
                ticksSpentField?.SetValue(driver, ticksSpent + 1);

                job.bill.Notify_PawnDidWork(actor);

                IBillGiverWithTickAction tickGiver =
                    job.GetTarget(TargetIndex.A).Thing as IBillGiverWithTickAction;
                tickGiver?.UsedThisTick();

                float configuredWork = job.RecipeDef != null && job.RecipeDef.workAmount > 0f
                    ? job.RecipeDef.workAmount
                    : RecycleUtility.FallbackRecycleTicks;

                float workLeft = workLeftField != null
                    ? (float)workLeftField.GetValue(driver)
                    : configuredWork;

                workLeft -= 1f;
                workLeftField?.SetValue(driver, workLeft);

                actor.GainComfortFromCellIfPossible(true);

                if (workLeft <= 0f)
                {
                    job.bill.Notify_BillWorkFinished(actor);
                    actor.jobs.curDriver.ReadyForNextToil();
                }
            };
        }
    }

    // MO's own progress bar denominator is based on missing HP, so it is wrong
    // for recycle jobs. Replace only the recycle progress getter and use the
    // active RecipeDef.workAmount as the denominator.
    [HarmonyPatch(typeof(ToilEffects), nameof(ToilEffects.WithProgressBar),
        new Type[] { typeof(Toil), typeof(TargetIndex), typeof(Func<float>), typeof(bool), typeof(float), typeof(bool) })]
    internal static class Patch_ToilEffects_WithProgressBar
    {
        private static void Prefix(Toil toil, ref Func<float> progressGetter)
        {
            if (toil == null || toil.debugName != "DoRecipeWork_Mend")
                return;

            Func<float> original = progressGetter;

            progressGetter = delegate
            {
                Pawn actor = toil.actor;
                Job job = actor?.jobs?.curJob;
                if (job != null && RecycleUtility.IsRecycleRecipe(job.RecipeDef))
                {
                    object driver = actor.jobs.curDriver;
                    if (driver != null)
                    {
                        FieldInfo workLeftField = AccessTools.Field(driver.GetType(), "workLeft");
                        if (workLeftField != null)
                        {
                            float workLeft = (float)workLeftField.GetValue(driver);
                            float configuredWork = job.RecipeDef != null && job.RecipeDef.workAmount > 0f
                                ? job.RecipeDef.workAmount
                                : RecycleUtility.FallbackRecycleTicks;
                            return Mathf.Clamp01(1f - workLeft / configuredWork);
                        }
                    }
                    return 0f;
                }

                return original != null ? original() : 0f;
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
