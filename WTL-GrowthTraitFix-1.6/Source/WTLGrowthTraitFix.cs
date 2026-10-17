using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WTLGrowthTraitFix
{
    /// <summary>
    /// WTL 1.1.6 invalidates the cache for every Def type whenever the number
    /// of TraitDefs changes. During child growth this can cause a large or
    /// reentrant rebuild inside OnGUI. Rebuild only the closed TraitDef cache.
    /// WTL's existing GenerateTraitsFor transpiler continues to enforce the
    /// player's exact tech-level and per-trait override rules.
    /// </summary>
    public sealed class GrowthTraitFixMod : Mod
    {
        private const string HarmonyId = "allen.wtl.growthtraitfix";
        private static FieldInfo traitLevels;
        private static MethodInfo rebuildTraits;
        private static MethodInfo applyOverrides;
        private static bool rebuilding;
        private static int failedCount = -1;
        private static int lastEmptyPoolWarnCount = -1;

        public GrowthTraitFixMod(ModContentPack content) : base(content)
        {
            try
            {
                var openDb = AccessTools.TypeByName("WorldTechLevel.TechLevelDatabase`1");
                if (openDb == null)
                {
                    Log.Error("[WTLGrowthTraitFix] World Tech Level internal trait database not found. Check WTL v1.1.6.");
                    return;
                }

                var db = openDb.MakeGenericType(typeof(TraitDef));
                traitLevels = AccessTools.Field(db, "Levels");
                rebuildTraits = AccessTools.Method(db, "Initialize");
                applyOverrides = AccessTools.Method(db, "ApplyOverrides");
                var ensure = AccessTools.Method(db, "EnsureInitialized");
                var pawnPatch = AccessTools.TypeByName("WorldTechLevel.Patches.Patch_PawnGenerator");
                var filteredTraits = pawnPatch == null ? null : AccessTools.Method(pawnPatch, "FilteredTraits");

                if (traitLevels == null || rebuildTraits == null || applyOverrides == null ||
                    ensure == null || filteredTraits == null)
                {
                    Log.Error("[WTLGrowthTraitFix] Required WTL 1.1.6 method or field missing; leaving WTL unmodified.");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    ensure,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(GrowthTraitFixMod), nameof(EnsureTraitsInitializedPrefix))));
                harmony.Patch(
                    filteredTraits,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(GrowthTraitFixMod), nameof(SnapshotAllowedTraitsPostfix))));

                Log.Message("[WTLGrowthTraitFix] WTL v1.1.6 TraitDef cache guard and candidate snapshot enabled.");
            }
            catch (Exception ex)
            {
                Log.Error("[WTLGrowthTraitFix] Could not install Harmony patches: " + ex);
            }
        }

        /// <summary>
        /// A postfix on WTL's private filtered-traits method forces its lazy
        /// Where(...) enumerator into a stable snapshot before vanilla chooses
        /// child growth traits. This does not alter which defs WTL allows.
        /// </summary>
        private static void SnapshotAllowedTraitsPostfix(
            Pawn pawn, ref IEnumerable<TraitDef> __result)
        {
            if (__result == null)
                return;

            // The vanilla generator, including its restrictions on conflicting
            // traits and trait degrees, continues to do the actual selection.
            var snapshot = __result.ToArray();
            __result = snapshot;

            if (snapshot.Length == 0)
            {
                var count = DefDatabase<TraitDef>.AllDefsListForReading.Count;
                if (lastEmptyPoolWarnCount != count)
                {
                    lastEmptyPoolWarnCount = count;
                    Log.Warning("[WTLGrowthTraitFix] WTL produced an empty allowed-trait pool. " +
                                "No compliant replacement exists; check WTL trait overrides.");
                }
            }
        }

        /// <summary>
        /// Replace the original EnsureInitialized() *for TraitDef only*.
        /// WTL's original check invokes DefTechLevels.Initialize(), rebuilding
        /// all research/items/quests/etc. Rebuild just TraitDef and its overrides.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        private static bool EnsureTraitsInitializedPrefix()
        {
            // Nested callbacks during a rebuild must not start a second rebuild.
            if (rebuilding)
                return false;

            try
            {
                var previous = traitLevels.GetValue(null) as Array;
                var count = DefDatabase<TraitDef>.AllDefsListForReading.Count;

                // The initial empty cache is populated by WTL's normal Init().
                if (previous == null || previous.Length == 0 || previous.Length == count)
                    return false;

                // If a broken mod keeps mutating the DB, do not invoke a global
                // rebuild or spin recursively inside child growth's GUI path.
                if (failedCount == count)
                    return false;

                rebuilding = true;
                try
                {
                    // Initialize(Func<TraitDef, TechLevel> func = null).
                    // TraitDefs have no base evaluator in WTL: overrides supply
                    // classifications. Pass the optional null argument explicitly.
                    rebuildTraits.Invoke(null, new object[] { null });
                    applyOverrides.Invoke(null, null);

                    var updated = traitLevels.GetValue(null) as Array;
                    if (updated == null || updated.Length != count)
                    {
                        failedCount = count;
                        Log.Warning("[WTLGrowthTraitFix] TraitDef count changed again during local rebuild; " +
                                    "not falling back to WTL's global initialization.");
                    }
                    else
                    {
                        failedCount = -1;
                        Log.Message("[WTLGrowthTraitFix] Refreshed only TraitDef tech-level cache (" +
                                    previous.Length + " -> " + count + ").");
                    }
                }
                catch (Exception ex)
                {
                    failedCount = count;
                    var root = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    Log.Error("[WTLGrowthTraitFix] TraitDef-only cache refresh failed: " + root);
                }
                finally
                {
                    rebuilding = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[WTLGrowthTraitFix] TraitDef cache guard error: " + ex);
            }

            // Do not run WTL's whole-database rebuild on this code path.
            return false;
        }
    }
}
