using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace WorldTechLevelFinalGuard
{
    [StaticConstructorOnStartup]
    internal static class FinalGuardBootstrap
    {
        internal const string HarmonyId = "allen.wtl.finalguard";
        private static bool installed;

        static FinalGuardBootstrap()
        {
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                Log.Error("[World Tech Level - Final Guard V2] Install failed; World Tech Level remains fully authoritative. " + ex);
            }
        }

        private static void Install()
        {
            if (installed) return;

            Type openDatabase = AccessTools.TypeByName("WorldTechLevel.TechLevelDatabase`1");
            if (openDatabase == null)
            {
                Log.Warning("[World Tech Level - Final Guard V2] WorldTechLevel.TechLevelDatabase`1 was not found; guard is inactive.");
                return;
            }

            Type traitDatabase = openDatabase.MakeGenericType(typeof(TraitDef));
            MethodInfo ensureInitialized = AccessTools.Method(traitDatabase, "EnsureInitialized");
            MethodInfo initialize = AccessTools.Method(traitDatabase, "Initialize");
            MethodInfo applyOverrides = AccessTools.Method(traitDatabase, "ApplyOverrides");
            FieldInfo levels = AccessTools.Field(traitDatabase, "Levels");
            FieldInfo alternatives = AccessTools.Field(traitDatabase, "Alternatives");
            MethodInfo setIndices = AccessTools.Method(typeof(DefDatabase<TraitDef>), "SetIndices");

            if (ensureInitialized == null || initialize == null || applyOverrides == null || levels == null || alternatives == null || setIndices == null)
            {
                Log.Warning("[World Tech Level - Final Guard V2] World Tech Level internals differ from the supported 1.1.6 layout; guard is inactive.");
                return;
            }

            TraitDefLocalRepair.Configure(initialize, applyOverrides, levels, alternatives, setIndices);

            Harmony harmony = new Harmony(HarmonyId);
            harmony.Patch(
                ensureInitialized,
                prefix: new HarmonyMethod(typeof(TraitDefLocalRepair), nameof(TraitDefLocalRepair.Prefix))
                {
                    priority = Priority.First
                });

            installed = true;
            TraitDefLocalRepair.PrimeKnownDefs();
            Log.Message("[World Tech Level - Final Guard V2] Active. TraitDef database mismatches will be repaired locally; full DefTechLevels.Initialize() storms are suppressed for TraitDef only.");
        }
    }

    internal static class TraitDefLocalRepair
    {
        private static MethodInfo initializeMethod;
        private static MethodInfo applyOverridesMethod;
        private static FieldInfo levelsField;
        private static FieldInfo alternativesField;
        private static MethodInfo setIndicesMethod;

        private static readonly HashSet<string> knownDefs = new HashSet<string>(StringComparer.Ordinal);
        private static int repairs;
        private static int failures;
        private static int lastObservedCount = -1;

        [ThreadStatic]
        private static bool repairing;

        internal static void Configure(
            MethodInfo initialize,
            MethodInfo applyOverrides,
            FieldInfo levels,
            FieldInfo alternatives,
            MethodInfo setIndices)
        {
            initializeMethod = initialize;
            applyOverridesMethod = applyOverrides;
            levelsField = levels;
            alternativesField = alternatives;
            setIndicesMethod = setIndices;
        }

        internal static void PrimeKnownDefs()
        {
            RefreshKnownDefs();
            lastObservedCount = DefDatabase<TraitDef>.AllDefsListForReading.Count;
        }

        /// <summary>
        /// Replaces WorldTechLevel.TechLevelDatabase&lt;TraitDef&gt;.EnsureInitialized().
        /// Healthy/empty states are no-ops, exactly as upstream. A count mismatch is repaired by
        /// rebuilding only TraitDef's local World Tech Level tables, never the global DefTechLevels database.
        /// Returning true is reserved for repair failure, which fails open to upstream behavior.
        /// </summary>
        public static bool Prefix()
        {
            if (repairing) return false;
            if (initializeMethod == null || applyOverridesMethod == null || levelsField == null || setIndicesMethod == null)
                return true;

            try
            {
                List<TraitDef> defs = DefDatabase<TraitDef>.AllDefsListForReading;
                Array levels = levelsField.GetValue(null) as Array;

                // Upstream does nothing while the table is still uninitialized, and does nothing when counts match.
                if (levels == null || levels.Length == 0 || levels.Length == defs.Count)
                    return false;

                int oldCount = levels.Length;
                int newCount = defs.Count;
                string delta = DescribeDelta(defs);

                repairing = true;
                try
                {
                    // Match upstream TraitDef initialization semantics, but only for TraitDef:
                    // SetIndices -> Initialize(default tech level) -> ApplyOverrides.
                    setIndicesMethod.Invoke(null, null);
                    initializeMethod.Invoke(null, new object[] { null });
                    applyOverridesMethod.Invoke(null, null);

                    Array repaired = levelsField.GetValue(null) as Array;
                    if (repaired == null || repaired.Length != newCount)
                        throw new InvalidOperationException("Localized TraitDef repair did not converge: levels=" +
                            (repaired == null ? -1 : repaired.Length) + ", defs=" + newCount + ".");

                    // Initialize() also rebuilds Alternatives when configured. Validate only its outer length when present.
                    Array alternatives = alternativesField == null ? null : alternativesField.GetValue(null) as Array;
                    if (alternatives != null && alternatives.Length != 0 && alternatives.Length != newCount)
                        throw new InvalidOperationException("Localized TraitDef alternatives table length mismatch: alternatives=" +
                            alternatives.Length + ", defs=" + newCount + ".");

                    repairs++;
                    lastObservedCount = newCount;
                    RefreshKnownDefs();

                    // Never reproduce WorldTechLevel's per-call warning + stack-trace flood.
                    // Log only on powers of two so a pathological runtime still remains observable.
                    if (repairs == 1 || (repairs & (repairs - 1)) == 0)
                    {
                        Log.Message("[World Tech Level - Final Guard V2] Local TraitDef repair #" + repairs +
                            ": " + oldCount + " -> " + newCount +
                            (string.IsNullOrEmpty(delta) ? "." : "; delta=" + delta + "."));
                    }

                    return false;
                }
                finally
                {
                    repairing = false;
                }
            }
            catch (Exception ex)
            {
                failures++;
                repairing = false;
                Log.Error("[World Tech Level - Final Guard V2] Local TraitDef repair failed (#" + failures +
                    "); failing open to World Tech Level's original full rebuild. " + ex);
                return true;
            }
        }

        private static string DescribeDelta(List<TraitDef> defs)
        {
            if (defs == null) return string.Empty;

            List<string> added = null;
            for (int i = 0; i < defs.Count; i++)
            {
                TraitDef def = defs[i];
                if (def == null || string.IsNullOrEmpty(def.defName) || knownDefs.Contains(def.defName))
                    continue;

                if (added == null) added = new List<string>();
                if (added.Count < 8)
                {
                    string package = def.modContentPack == null ? "unknown" : def.modContentPack.PackageId;
                    added.Add(def.defName + "@" + package);
                }
            }

            if (added != null && added.Count > 0)
                return "+" + string.Join(",", added.ToArray());

            if (lastObservedCount >= 0 && defs.Count < lastObservedCount)
                return "removed=" + (lastObservedCount - defs.Count);

            return string.Empty;
        }

        private static void RefreshKnownDefs()
        {
            knownDefs.Clear();
            List<TraitDef> defs = DefDatabase<TraitDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                TraitDef def = defs[i];
                if (def != null && !string.IsNullOrEmpty(def.defName))
                    knownDefs.Add(def.defName);
            }
        }
    }
}
