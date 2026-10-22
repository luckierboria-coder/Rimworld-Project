using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WTLGrowthTraitFixV2
{
    /// <summary>
    /// WorldTechLevel 1.1.6 uses a lazy TraitDef filter in
    /// Patch_PawnGenerator.FilteredTraits(Pawn). When the number of TraitDefs
    /// changes, the normal TechLevelDatabase<TraitDef>.EnsureInitialized()
    /// invokes DefTechLevels.Initialize() for *all* defs, including research.
    ///
    /// Intercept ONLY the non-generic FilteredTraits call site and refresh
    /// ONLY the TraitDef cache before letting WTL run its original filtering.
    /// Never patch a generic method: its body may be shared across T.
    /// </summary>
    public sealed class GrowthTraitCacheGuardMod : Mod
    {
        private const string PatchId = "allen.wtl.growthtraitfix.v2";
        private static FieldInfo levelsField;
        private static MethodInfo initializeMethod;
        private static MethodInfo applyOverridesMethod;
        private static bool rebuilding;
        private static int lastFailedCount = -1;

        public GrowthTraitCacheGuardMod(ModContentPack content) : base(content)
        {
            // WTL is a Lunar component, not a normal mod assembly.
            // Its assembly is typically loaded after normal mod constructors.
            // Resolve target methods after play-data loading instead.
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                var patchClass = AccessTools.TypeByName("WorldTechLevel.Patches.Patch_PawnGenerator");
                var target = patchClass == null ? null : AccessTools.Method(patchClass, "FilteredTraits");
                var genericDatabase = AccessTools.TypeByName("WorldTechLevel.TechLevelDatabase`1");
                var researchApi = AccessTools.TypeByName("WorldTechLevel.ResearchUtility");
                var visible = researchApi == null ? null : AccessTools.Method(researchApi, "ShouldProjectBeVisible");

                if (target == null || genericDatabase == null || visible == null)
                {
                    Log.Error("[WTLGrowthTraitFixV2] WTL 1.1.6 Lunar component is not loaded. " +
                              "Fix WTL's LunarLoader/LunarFramework installation first. " +
                              "Patch NOT installed; research filters NOT changed.");
                    return;
                }

                var traitCache = genericDatabase.MakeGenericType(typeof(TraitDef));
                levelsField = AccessTools.Field(traitCache, "Levels");
                initializeMethod = AccessTools.Method(traitCache, "Initialize");
                applyOverridesMethod = AccessTools.Method(traitCache, "ApplyOverrides");

                if (levelsField == null || initializeMethod == null || applyOverridesMethod == null)
                {
                    Log.Error("[WTLGrowthTraitFixV2] WTL TraitDef cache signature mismatch. Patch NOT installed.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(GrowthTraitCacheGuardMod), nameof(BeforeFilterTraits));
                new Harmony(PatchId).Patch(target, prefix: new HarmonyMethod(prefix)
                {
                    priority = Priority.First
                });

                Log.Message("[WTLGrowthTraitFixV2] Installed narrow WTL FilteredTraits(Pawn) guard. " +
                            "Research, incidents and other Def caches are untouched.");
            }
            catch (Exception ex)
            {
                Log.Error("[WTLGrowthTraitFixV2] Patch installation failed: " + ex);
            }
        }

        private static void BeforeFilterTraits(Pawn pawn)
        {
            if (rebuilding || pawn == null)
                return;

            try
            {
                var oldLevels = levelsField.GetValue(null) as Array;
                var liveCount = DefDatabase<TraitDef>.AllDefsListForReading.Count;

                // WTL itself performs first-time initialization.
                if (oldLevels == null || oldLevels.Length == 0 || oldLevels.Length == liveCount)
                    return;

                if (lastFailedCount == liveCount)
                    return;

                rebuilding = true;
                try
                {
                    // WTL 1.1.6 initializes TraitDef levels without a base
                    // evaluator and applies the user's configured overrides.
                    initializeMethod.Invoke(null, new object[] { null });
                    applyOverridesMethod.Invoke(null, null);

                    var newLevels = levelsField.GetValue(null) as Array;
                    if (newLevels == null || newLevels.Length != liveCount)
                    {
                        lastFailedCount = liveCount;
                        Log.Warning("[WTLGrowthTraitFixV2] TraitDef changed again during refresh. " +
                                    "WTL will decide how to handle the original filter.");
                        return;
                    }

                    lastFailedCount = -1;
                    Log.Message("[WTLGrowthTraitFixV2] Refreshed WTL TraitDef cache only: " +
                                oldLevels.Length + " -> " + liveCount +
                                ". WTL's original trait filtering will now run.");
                }
                catch (TargetInvocationException ex)
                {
                    lastFailedCount = liveCount;
                    Log.Error("[WTLGrowthTraitFixV2] WTL TraitDef-only cache refresh failed: " +
                              (ex.InnerException ?? ex).ToString());
                }
                catch (Exception ex)
                {
                    lastFailedCount = liveCount;
                    Log.Error("[WTLGrowthTraitFixV2] WTL TraitDef-only cache refresh failed: " + ex);
                }
                finally
                {
                    rebuilding = false;
                }
            }
            catch (Exception ex)
            {
                // Fail open to WTL's ORIGINAL filtering, never supply an
                // unfiltered trait pool or bypass research.
                Log.Error("[WTLGrowthTraitFixV2] Guard failed; keeping WTL original behavior: " + ex);
            }
        }
    }
}
