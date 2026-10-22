using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WTLGrowthTraitFixV3
{
    /// <summary>
    /// Reuse WTL's *original* eligible TraitDefs for one bounded
    /// PawnGenerator.GenerateTraitsFor invocation. No changes to eligibility,
    /// selection probabilities, vanilla conflict checks, or world tech limits.
    /// </summary>
    public sealed class GrowthTraitGuardMod : Mod
    {
        private const string PatchId = "allen.wtl.growthtraitfix.v3";
        private static FieldInfo levelsField;
        private static MethodInfo initLevels;
        private static MethodInfo applyOverrides;
        private static bool rebuilding;
        private static int failedCount = -1;

        [ThreadStatic] private static Scope activeScope;

        private sealed class Scope
        {
            internal readonly Scope Parent;
            internal readonly Pawn Pawn;
            internal readonly int Count;
            internal readonly long Start;
            internal TraitDef[] Candidates;
            internal int FilterCalls;
            internal bool CacheRebuilt;
            internal Scope(Scope parent, Pawn pawn, int count)
            {
                Parent = parent;
                Pawn = pawn;
                Count = count;
                Start = Stopwatch.GetTimestamp();
            }
        }

        public GrowthTraitGuardMod(ModContentPack content) : base(content)
        {
            // Lunar components are loaded after ordinary Mod constructors.
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                var filterClass = AccessTools.TypeByName("WorldTechLevel.Patches.Patch_PawnGenerator");
                var filter = filterClass == null ? null : AccessTools.Method(filterClass, "FilteredTraits");
                var generic = AccessTools.TypeByName("WorldTechLevel.TechLevelDatabase`1");
                var research = AccessTools.TypeByName("WorldTechLevel.ResearchUtility");
                var researchMethod = research == null ? null : AccessTools.Method(research, "ShouldProjectBeVisible");

                if (filter == null || generic == null || researchMethod == null)
                {
                    Log.Error("[WTLGrowthV3] WTL Lunar component not loaded or version incompatible. " +
                              "NO patches installed; check complete WTL 1.1.6 installation.");
                    return;
                }

                var db = generic.MakeGenericType(typeof(TraitDef));
                levelsField = AccessTools.Field(db, "Levels");
                initLevels = AccessTools.Method(db, "Initialize");
                applyOverrides = AccessTools.Method(db, "ApplyOverrides");
                if (levelsField == null || initLevels == null || applyOverrides == null)
                {
                    Log.Error("[WTLGrowthV3] WTL TraitDef cache fields do not match 1.1.6; NO patches installed.");
                    return;
                }

                var generation = AccessTools.Method(typeof(PawnGenerator), "GenerateTraitsFor",
                    new[] { typeof(Pawn), typeof(int), typeof(PawnGenerationRequest?), typeof(bool) });
                var growthLetter = AccessTools.Method(typeof(ChoiceLetter_GrowthMoment), "OpenLetter");
                var tryChoices = AccessTools.Method(typeof(ChoiceLetter_GrowthMoment), "TrySetChoices");
                if (generation == null || growthLetter == null || tryChoices == null)
                {
                    Log.Error("[WTLGrowthV3] RimWorld 1.6.4871 growth method not found. NO patches installed.");
                    return;
                }

                var h = new Harmony(PatchId);
                h.Patch(generation,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(GeneratePrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(GenerateFinalizer))));
                h.Patch(filter,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(FilterPrefix))) { priority = Priority.First },
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(FilterPostfix))));
                h.Patch(growthLetter,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(LetterPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(LetterFinalizer))));
                h.Patch(tryChoices,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(ChoicesPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(ChoicesFinalizer))));
                Log.Message("[WTLGrowthV3] Installed growth candidate snapshot & phase diagnostics. " +
                            "No research filters, generic EnsureInitialized or save data modified.");
            }
            catch (Exception e)
            {
                Log.Error("[WTLGrowthV3] Failed to install Harmony methods: " + e);
            }
        }

        private static void LetterPrefix(ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV3] OpenLetter ENTER pawn=" + PawnText(__instance) +
                        " choices=" + (__instance?.traitChoiceCount ?? 0));
        }
        private static Exception LetterFinalizer(Exception __exception, ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV3] OpenLetter EXIT pawn=" + PawnText(__instance) +
                        " error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }
        private static void ChoicesPrefix(ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV3] TrySetChoices ENTER pawn=" + PawnText(__instance));
        }
        private static Exception ChoicesFinalizer(Exception __exception, ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV3] TrySetChoices EXIT pawn=" + PawnText(__instance) +
                        " traitChoices=" + (__instance?.traitChoices?.Count.ToString() ?? "null") +
                        " passionChoices=" + (__instance?.passionChoices?.Count.ToString() ?? "null") +
                        " error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }
        private static string PawnText(ChoiceLetter_GrowthMoment letter)
        {
            return letter?.pawn?.LabelShort ?? "<null>";
        }

        private static void GeneratePrefix(Pawn pawn, int traitCount, bool growthMomentTrait, out Scope __state)
        {
            __state = null;
            if (!growthMomentTrait) return;
            __state = new Scope(activeScope, pawn, traitCount);
            activeScope = __state;
            Log.Message("[WTLGrowthV3] GenerateTraitsFor ENTER pawn=" + (pawn?.LabelShort ?? "<null>") +
                        " requested=" + traitCount);
        }

        private static Exception GenerateFinalizer(Exception __exception, Scope __state)
        {
            if (__state == null) return __exception;
            if (activeScope == __state) activeScope = __state.Parent;
            var elapsedMs = (Stopwatch.GetTimestamp() - __state.Start) * 1000.0 / Stopwatch.Frequency;
            Log.Message("[WTLGrowthV3] GenerateTraitsFor EXIT pawn=" + (__state.Pawn?.LabelShort ?? "<null>") +
                        " ms=" + elapsedMs.ToString("F2") +
                        " filterCalls=" + __state.FilterCalls +
                        " cachedCandidates=" + (__state.Candidates?.Length.ToString() ?? "null") +
                        " cacheRebuilt=" + __state.CacheRebuilt +
                        " error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }

        /// <summary>
        /// WTL's transpiler calls this non-generic method once per attempt.
        /// Preserve the original method on the first attempt and reuse its
        /// snapshot only inside the SAME vanilla GenerateTraitsFor call.
        /// </summary>
        private static bool FilterPrefix(Pawn pawn, ref IEnumerable<TraitDef> __result)
        {
            Scope s = activeScope;
            if (s == null || s.Pawn != pawn) return true;
            s.FilterCalls++;
            if (s.Candidates != null)
            {
                __result = s.Candidates;
                return false;
            }
            GuardTraitDefCache(s);
            return true;
        }

        private static void FilterPostfix(Pawn pawn, ref IEnumerable<TraitDef> __result)
        {
            Scope s = activeScope;
            if (s == null || s.Pawn != pawn || s.Candidates != null || __result == null) return;
            // The result comes entirely from original WTL eligibility logic.
            // Materialize once so the original random sampler can re-enumerate.
            s.Candidates = __result.ToArray();
            __result = s.Candidates;
            Log.Message("[WTLGrowthV3] WTL permitted " + s.Candidates.Length +
                        "/" + DefDatabase<TraitDef>.AllDefsListForReading.Count +
                        " TraitDefs for " + (pawn?.LabelShort ?? "<null>") +
                        "; vanilla performs trait conflict/degree checks.");
        }

        private static void GuardTraitDefCache(Scope scope)
        {
            if (rebuilding) return;
            try
            {
                var levels = levelsField.GetValue(null) as Array;
                var current = DefDatabase<TraitDef>.AllDefsListForReading.Count;
                if (levels == null || levels.Length == 0 || levels.Length == current || failedCount == current) return;
                rebuilding = true;
                try
                {
                    Log.Warning("[WTLGrowthV3] TraitDef count mismatch " + levels.Length + "->" + current +
                                "; refreshing TRAIT database only.");
                    initLevels.Invoke(null, new object[] { null });
                    applyOverrides.Invoke(null, null);
                    var refreshed = levelsField.GetValue(null) as Array;
                    if (refreshed == null || refreshed.Length != current)
                    {
                        failedCount = current;
                        Log.Error("[WTLGrowthV3] TraitDef refresh size mismatch. Original WTL method remains in control.");
                    }
                    else
                    {
                        scope.CacheRebuilt = true;
                        failedCount = -1;
                    }
                }
                catch (Exception ex)
                {
                    failedCount = current;
                    Log.Error("[WTLGrowthV3] TraitDef-only cache refresh failed: " +
                              (ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex));
                }
                finally { rebuilding = false; }
            }
            catch (Exception ex)
            {
                Log.Error("[WTLGrowthV3] Cache check failed; WTL original behavior preserved: " + ex);
            }
        }
    }
}
