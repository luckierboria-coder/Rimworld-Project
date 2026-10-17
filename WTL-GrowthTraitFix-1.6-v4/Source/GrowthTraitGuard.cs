using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WTLGrowthTraitFixV4
{
    /// <summary>
    /// Reuse WTL's *original* eligible TraitDefs for one bounded
    /// PawnGenerator.GenerateTraitsFor invocation. No changes to eligibility,
    /// selection probabilities, vanilla conflict checks, or world tech limits.
    /// </summary>
    public sealed class GrowthTraitGuardMod : Mod
    {
        private const string PatchId = "allen.wtl.growthtraitfix.v4";
        private static FieldInfo levelsField;
        private static MethodInfo initLevels;
        private static MethodInfo applyOverrides;
        private static bool rebuilding;
        private static int failedCount = -1;
        private static int uiLogBudget = 135;
        [ThreadStatic] private static bool insideGrowthDraw;
        private static void UiTrace(string message)
        {
            if (Interlocked.Decrement(ref uiLogBudget) >= 0)
                Log.Message("[WTLGrowthV4/UI] " + message);
        }

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
                    Log.Error("[WTLGrowthV4] WTL Lunar component not loaded or version incompatible. " +
                              "NO patches installed; check complete WTL 1.1.6 installation.");
                    return;
                }

                var db = generic.MakeGenericType(typeof(TraitDef));
                levelsField = AccessTools.Field(db, "Levels");
                initLevels = AccessTools.Method(db, "Initialize");
                applyOverrides = AccessTools.Method(db, "ApplyOverrides");
                if (levelsField == null || initLevels == null || applyOverrides == null)
                {
                    Log.Error("[WTLGrowthV4] WTL TraitDef cache fields do not match 1.1.6; NO patches installed.");
                    return;
                }

                var generation = AccessTools.Method(typeof(PawnGenerator), "GenerateTraitsFor",
                    new[] { typeof(Pawn), typeof(int), typeof(PawnGenerationRequest?), typeof(bool) });
                var growthLetter = AccessTools.Method(typeof(ChoiceLetter_GrowthMoment), "OpenLetter");
                var tryChoices = AccessTools.Method(typeof(ChoiceLetter_GrowthMoment), "TrySetChoices");
                if (generation == null || growthLetter == null || tryChoices == null)
                {
                    Log.Error("[WTLGrowthV4] RimWorld 1.6.4871 growth method not found. NO patches installed.");
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
                var dialogType = typeof(Dialog_GrowthMomentChoices);
                var windowOnGui = AccessTools.Method(typeof(Window), "WindowOnGUI");
                var initialSize = AccessTools.PropertyGetter(dialogType, "InitialSize");
                var draw = AccessTools.Method(dialogType, "DoWindowContents");
                if (windowOnGui == null || initialSize == null || draw == null)
                {
                    Log.Error("[WTLGrowthV4] UI signature mismatch; UI trace could not install.");
                }
                else
                {
                    h.Patch(windowOnGui,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(WindowPrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(WindowFinalizer))));
                    h.Patch(initialSize,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(SizePrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(SizeFinalizer))));
                    h.Patch(draw,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(DrawPrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(DrawFinalizer))));
                    foreach (var method in new MethodBase[]
                    {
                        AccessTools.Method(dialogType, "DrawPassionChoices"),
                        AccessTools.Method(dialogType, "DrawTraitChoices"),
                        AccessTools.Method(dialogType, "DrawBottomText"),
                        AccessTools.Method(typeof(CharacterCardUtility), "DrawCharacterCard"),
                        AccessTools.Method(typeof(CharacterCardUtility), "PawnCardSize"),
                        AccessTools.Method(typeof(HealthCardUtility), "DrawHediffListing"),
                        AccessTools.Method(typeof(Trait), "TipString", new[] { typeof(Pawn) })
                    })
                    {
                        if (method == null) continue;
                        h.Patch(method,
                            prefix: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(PhasePrefix))),
                            finalizer: new HarmonyMethod(AccessTools.Method(typeof(GrowthTraitGuardMod), nameof(PhaseFinalizer))));
                    }
                    Log.Message("[WTLGrowthV4] UI stage trace installed: WindowOnGUI, InitialSize, DoWindowContents, choice sections, character card and trait tips.");
                }
                Log.Message("[WTLGrowthV4] Installed growth candidate snapshot & UI trace; no research or save changes.");
            }
            catch (Exception e)
            {
                Log.Error("[WTLGrowthV4] Failed to install Harmony methods: " + e);
            }
        }

        // Passive UI diagnostics: do not skip or modify the original window.
        private static void WindowPrefix(Window __instance, out bool __state)
        {
            __state = __instance is Dialog_GrowthMomentChoices;
            if (__state) UiTrace("Window.WindowOnGUI ENTER");
        }
        private static Exception WindowFinalizer(Exception __exception, bool __state)
        {
            if (__state) UiTrace("Window.WindowOnGUI EXIT error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }
        private static void SizePrefix() { UiTrace("Dialog.InitialSize ENTER"); }
        private static Exception SizeFinalizer(Exception __exception)
        {
            UiTrace("Dialog.InitialSize EXIT error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }
        private static void DrawPrefix(out bool __state)
        {
            __state = insideGrowthDraw;
            insideGrowthDraw = true;
            UiTrace("Dialog.DoWindowContents ENTER");
        }
        private static Exception DrawFinalizer(Exception __exception, bool __state)
        {
            UiTrace("Dialog.DoWindowContents EXIT error=" + (__exception?.GetType().Name ?? "none"));
            insideGrowthDraw = __state;
            return __exception;
        }
        private static void PhasePrefix(MethodBase __originalMethod, out bool __state)
        {
            __state = insideGrowthDraw;
            if (__state) UiTrace(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " ENTER");
        }
        private static Exception PhaseFinalizer(Exception __exception, MethodBase __originalMethod, bool __state)
        {
            if (__state) UiTrace(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name +
                " EXIT error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }

        private static void LetterPrefix(ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV4] OpenLetter ENTER pawn=" + PawnText(__instance) +
                        " choices=" + (__instance?.traitChoiceCount ?? 0));
        }
        private static Exception LetterFinalizer(Exception __exception, ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV4] OpenLetter EXIT pawn=" + PawnText(__instance) +
                        " error=" + (__exception?.GetType().Name ?? "none"));
            return __exception;
        }
        private static void ChoicesPrefix(ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV4] TrySetChoices ENTER pawn=" + PawnText(__instance));
        }
        private static Exception ChoicesFinalizer(Exception __exception, ChoiceLetter_GrowthMoment __instance)
        {
            Log.Message("[WTLGrowthV4] TrySetChoices EXIT pawn=" + PawnText(__instance) +
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
            Log.Message("[WTLGrowthV4] GenerateTraitsFor ENTER pawn=" + (pawn?.LabelShort ?? "<null>") +
                        " requested=" + traitCount);
        }

        private static Exception GenerateFinalizer(Exception __exception, Scope __state)
        {
            if (__state == null) return __exception;
            if (activeScope == __state) activeScope = __state.Parent;
            var elapsedMs = (Stopwatch.GetTimestamp() - __state.Start) * 1000.0 / Stopwatch.Frequency;
            Log.Message("[WTLGrowthV4] GenerateTraitsFor EXIT pawn=" + (__state.Pawn?.LabelShort ?? "<null>") +
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
            Log.Message("[WTLGrowthV4] WTL permitted " + s.Candidates.Length +
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
                    Log.Warning("[WTLGrowthV4] TraitDef count mismatch " + levels.Length + "->" + current +
                                "; refreshing TRAIT database only.");
                    initLevels.Invoke(null, new object[] { null });
                    applyOverrides.Invoke(null, null);
                    var refreshed = levelsField.GetValue(null) as Array;
                    if (refreshed == null || refreshed.Length != current)
                    {
                        failedCount = current;
                        Log.Error("[WTLGrowthV4] TraitDef refresh size mismatch. Original WTL method remains in control.");
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
                    Log.Error("[WTLGrowthV4] TraitDef-only cache refresh failed: " +
                              (ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex));
                }
                finally { rebuilding = false; }
            }
            catch (Exception ex)
            {
                Log.Error("[WTLGrowthV4] Cache check failed; WTL original behavior preserved: " + ex);
            }
        }
    }
}
