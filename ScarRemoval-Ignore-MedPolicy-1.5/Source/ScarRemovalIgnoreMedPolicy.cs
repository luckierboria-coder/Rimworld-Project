using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ScarRemovalIgnoreMedPolicy15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony("allen.scarremoval.ignoremedpolicy.removescar.v1");

                MethodInfo tryFindIngredients = AccessTools.Method(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients");
                MethodInfo getMedicalCare = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.GetMedicalCareCategory), new[] { typeof(Thing) });
                MethodInfo canDoRecipeWithRestriction = AccessTools.Method(typeof(HealthCardUtility), "CanDoRecipeWithMedicineRestriction");

                if (tryFindIngredients == null || getMedicalCare == null || canDoRecipeWithRestriction == null)
                {
                    Log.Error("[Scar Removal Ignore MedPolicy] Required RimWorld 1.5 method lookup failed; patch not installed.");
                    return;
                }

                harmony.Patch(
                    tryFindIngredients,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(IngredientSearchScopePatch), nameof(IngredientSearchScopePatch.Prefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(IngredientSearchScopePatch), nameof(IngredientSearchScopePatch.Finalizer))));

                harmony.Patch(
                    getMedicalCare,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GetMedicalCareCategoryPatch), nameof(GetMedicalCareCategoryPatch.Postfix))));

                harmony.Patch(
                    canDoRecipeWithRestriction,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CanDoRecipeWithMedicineRestrictionPatch), nameof(CanDoRecipeWithMedicineRestrictionPatch.Prefix))));

                Log.Message("[Scar Removal Ignore MedPolicy] Active for SSR_RemoveScar only. Medicine policy is ignored for scheduling warning and surgery ingredient search.");
            }
            catch (Exception ex)
            {
                Log.Error("[Scar Removal Ignore MedPolicy] Failed to install: " + ex);
            }
        }
    }

    internal static class ScarRemovalPolicyContext
    {
        private const string TargetRecipe = "SSR_RemoveScar";

        [ThreadStatic]
        private static int ignoreDepth;

        internal static bool Active => ignoreDepth > 0;

        internal static bool IsTargetRecipe(RecipeDef recipe)
        {
            return recipe != null && recipe.defName == TargetRecipe;
        }

        internal static bool IsTargetBill(Bill bill)
        {
            return bill != null && IsTargetRecipe(bill.recipe);
        }

        internal static void Enter()
        {
            ignoreDepth++;
        }

        internal static void Exit()
        {
            if (ignoreDepth > 0)
                ignoreDepth--;
            else
                ignoreDepth = 0;
        }
    }

    internal static class IngredientSearchScopePatch
    {
        public static void Prefix(Bill __0, out bool __state)
        {
            __state = ScarRemovalPolicyContext.IsTargetBill(__0);
            if (__state)
                ScarRemovalPolicyContext.Enter();
        }

        public static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
                ScarRemovalPolicyContext.Exit();
            return __exception;
        }
    }

    internal static class GetMedicalCareCategoryPatch
    {
        public static void Postfix(ref MedicalCareCategory __result)
        {
            if (ScarRemovalPolicyContext.Active)
                __result = MedicalCareCategory.Best;
        }
    }

    internal static class CanDoRecipeWithMedicineRestrictionPatch
    {
        public static bool Prefix(RecipeDef __1, ref bool __result)
        {
            if (!ScarRemovalPolicyContext.IsTargetRecipe(__1))
                return true;

            __result = true;
            return false;
        }
    }
}
