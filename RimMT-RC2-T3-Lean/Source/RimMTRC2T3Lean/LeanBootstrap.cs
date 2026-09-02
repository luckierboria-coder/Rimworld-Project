using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMTRC2T3Lean
{
    [StaticConstructorOnStartup]
    internal static class LeanBootstrap
    {
        private const string HarmonyId = "allen.rimmt.rc2t3lean";
        private static int applied;

        static LeanBootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(ApplyLeanPolicy);
        }

        private static void ApplyLeanPolicy()
        {
            if (Interlocked.Exchange(ref applied, 1) != 0)
                return;

            try
            {
                int suppressed = 0;
                suppressed += SuppressFeature("parallel.workPrefilter", "RC2-T3 Lean: diagnostic run showed high snapshot/build volume for very low fast-negative yield");
                suppressed += SuppressFeature("parallel.pathSnapshot", "RC2-T3 Lean: shadow path validation is telemetry-only and is disabled in the lean build");
                suppressed += SuppressFeature("diagnostics.selfTest", "RC2-T3 Lean: production lean policy");
                suppressed += SuppressFeature("diagnostics.pathFinder", "RC2-T3 Lean: detailed path diagnostics disabled");
                suppressed += SuppressFeature("diagnostics.jobGiver", "RC2-T3 Lean: detailed JobGiver diagnostics disabled");

                Harmony harmony = new Harmony(HarmonyId);
                List<Removal> removals = BuildRemovalPlan();
                int removed = 0;
                int failures = 0;

                for (int i = 0; i < removals.Count; i++)
                {
                    Removal removal = removals[i];
                    try
                    {
                        harmony.Unpatch(removal.Original, removal.PatchMethod);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Log.Warning("[RimMT-T3Lean] Could not remove " + removal.Label + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                Log.Message("[RimMT-T3Lean] RC2-T3 Lean policy active. featureSuppressions=" + suppressed +
                    ", patchRelationsRemoved=" + removed +
                    ", removalFailures=" + failures +
                    ". Production S5.1/S5.3/Stage3/DoBill patches are never removed by owner; ambiguous Stage4D patches fail closed and remain installed.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT-T3Lean] Lean policy failed closed; existing RimMT behavior is unchanged. " + ex);
            }
        }

        private static int SuppressFeature(string id, string reason)
        {
            try
            {
                Type featureGate = AccessTools.TypeByName("RimMT.FeatureGate");
                if (featureGate == null)
                    return 0;

                MethodInfo suppress = AccessTools.Method(featureGate, "Suppress", new Type[] { typeof(string), typeof(string) });
                if (suppress == null)
                    return 0;

                suppress.Invoke(null, new object[] { id, reason });
                return 1;
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT-T3Lean] FeatureGate suppress failed for " + id + ": " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private static List<Removal> BuildRemovalPlan()
        {
            List<Removal> result = new List<Removal>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> ambiguousStage4D = new HashSet<string>(StringComparer.Ordinal);

            MethodBase[] originals = Harmony.GetAllPatchedMethods().ToArray();
            for (int i = 0; i < originals.Length; i++)
            {
                MethodBase original = originals[i];
                Patches info = Harmony.GetPatchInfo(original);
                if (info == null)
                    continue;

                Collect(original, info.Prefixes, result, seen, ambiguousStage4D);
                Collect(original, info.Postfixes, result, seen, ambiguousStage4D);
                Collect(original, info.Transpilers, result, seen, ambiguousStage4D);
                Collect(original, info.Finalizers, result, seen, ambiguousStage4D);
            }

            if (ambiguousStage4D.Count != 0)
            {
                string[] sample = ambiguousStage4D.Take(12).ToArray();
                Log.Message("[RimMT-T3Lean] Stage4D fail-closed: " + ambiguousStage4D.Count +
                    " patch method(s) were not safely classifiable and remain installed. sample=" + string.Join(" | ", sample));
            }

            return result;
        }

        private static void Collect(
            MethodBase original,
            IList<Patch> patches,
            List<Removal> result,
            HashSet<string> seen,
            HashSet<string> ambiguousStage4D)
        {
            if (patches == null)
                return;

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                MethodInfo patchMethod = patch == null ? null : patch.PatchMethod;
                if (patchMethod == null)
                    continue;

                string typeName = patchMethod.DeclaringType == null ? string.Empty : patchMethod.DeclaringType.FullName ?? string.Empty;
                string methodName = patchMethod.Name ?? string.Empty;
                string identity = typeName + "." + methodName;

                bool remove = IsPureDiagnostic(typeName) || IsOldHotPathDiagnostic(typeName, methodName);

                bool stage4DRecognized;
                if (IsStage4DLowYield(typeName, methodName, out stage4DRecognized))
                    remove = true;
                else if (IsStage4D(typeName) && !stage4DRecognized)
                    ambiguousStage4D.Add(identity);

                if (!remove)
                    continue;

                string key = OriginalKey(original) + " <- " + identity;
                if (!seen.Add(key))
                    continue;

                result.Add(new Removal(original, patchMethod, key));
            }
        }

        private static bool IsPureDiagnostic(string typeName)
        {
            return Contains(typeName, "RimMTRC2T2.GapClassifier") ||
                   Contains(typeName, "RimMTRC2T2.PreTailStructureProfiler") ||
                   Contains(typeName, "RimMTRC2T2.Stage4CDoBillOutcomeProfiler");
        }

        private static bool IsOldHotPathDiagnostic(string typeName, string methodName)
        {
            if (!string.Equals(typeName, "RimMT.HotPathPatches", StringComparison.Ordinal))
                return false;

            // TickPrefix/TickPostfix are deliberately retained because the current base runtime
            // also feeds AdaptiveLoadBalancer from TickPostfix. Path/JobGiver wrappers are
            // diagnostics/shadow plumbing only in the tested base and are removed for Lean.
            return string.Equals(methodName, "PathPrefix", StringComparison.Ordinal) ||
                   string.Equals(methodName, "PathPostfix", StringComparison.Ordinal) ||
                   string.Equals(methodName, "JobGiverPrefix", StringComparison.Ordinal) ||
                   string.Equals(methodName, "JobGiverPostfix", StringComparison.Ordinal);
        }

        private static bool IsStage4D(string typeName)
        {
            return Contains(typeName, "Stage4DCommonSenseAccelerator");
        }

        private static bool IsStage4DLowYield(string typeName, string methodName, out bool recognized)
        {
            recognized = false;
            if (!IsStage4D(typeName))
                return false;

            string identity = (typeName + "." + methodName).ToLowerInvariant();

            // Keep the one Stage4D path with overwhelming measured reuse.
            if (identity.Contains("ingredientexpand") || identity.Contains("ingredient_expand") || identity.Contains("expandingredient"))
            {
                recognized = true;
                return false;
            }

            // Current diagnostic run showed zero/near-zero useful hits for these paths.
            if (identity.Contains("clean") ||
                identity.Contains("opportun") ||
                identity.Contains("ingredientsort") ||
                identity.Contains("ingredient_sort") ||
                identity.Contains("spoil") ||
                identity.Contains("pathorder") ||
                identity.Contains("path_order") ||
                identity.Contains("pathordering"))
            {
                recognized = true;
                return true;
            }

            // Generic Prefix/Postfix names are not guessed at. Leaving an ambiguous patch in
            // place is preferable to removing a production authority path accidentally.
            return false;
        }

        private static bool Contains(string value, string fragment)
        {
            return value != null && value.IndexOf(fragment, StringComparison.Ordinal) >= 0;
        }

        private static string OriginalKey(MethodBase method)
        {
            if (method == null)
                return "<null-original>";
            string typeName = method.DeclaringType == null ? "<no-type>" : method.DeclaringType.FullName;
            return typeName + "." + method.Name;
        }

        private sealed class Removal
        {
            internal readonly MethodBase Original;
            internal readonly MethodInfo PatchMethod;
            internal readonly string Label;

            internal Removal(MethodBase original, MethodInfo patchMethod, string label)
            {
                Original = original;
                PatchMethod = patchMethod;
                Label = label;
            }
        }
    }
}
