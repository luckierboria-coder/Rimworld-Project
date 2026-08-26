using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    public static class CompatibilityGuard
    {
        private static readonly object Sync = new object();
        private static readonly List<string> ReportLines = new List<string>();
        private static readonly Dictionary<string, List<MethodBase>> Targets = new Dictionary<string, List<MethodBase>>();

        public static IList<string> Report
        {
            get { lock (Sync) return new List<string>(ReportLines).AsReadOnly(); }
        }

        public static void RegisterTarget(string featureId, MethodBase target)
        {
            if (string.IsNullOrEmpty(featureId) || target == null) return;
            lock (Sync)
            {
                List<MethodBase> list;
                if (!Targets.TryGetValue(featureId, out list))
                {
                    list = new List<MethodBase>();
                    Targets.Add(featureId, list);
                }
                if (!list.Contains(target)) list.Add(target);
            }
        }

        internal static void RunBaselineScan()
        {
            RuntimeCompatibility.Initialize();
            lock (Sync)
            {
                ReportLines.Clear();
                ReportLines.Add("Loaded mods: " + LoadedModManager.RunningModsListForReading.Count);
                ReportLines.Add("Policy: whitelist-only, fail-closed, vanilla fallback.");
                ReportLines.Add(RuntimeCompatibility.Summary());
            }

            if (AccessTools.TypeByName("RimThreaded.RimThreaded") != null || HasLoadedModName("RimThreaded"))
            {
                SuppressOptimizationSet("another RimThreaded implementation is loaded");
                AddReportUnique("All RimMT gameplay optimizations disabled because RimThreaded was detected.");
                return;
            }

            if (RuntimeCompatibility.ButterPlusPlusActive)
            {
                if (!RuntimeCompatibility.ButterLogicalTickProbeAvailable)
                {
                    FeatureGate.Suppress("runtime.dispatcher", "Butter++ tick splitting detected but TickManagerPatch._midTickStarted cannot be read safely");
                    AddReportUnique("runtime.dispatcher disabled because Butter++ was detected but its manager-level logical-tick state could not be read safely.");
                }
                else
                {
                    AddReportUnique("Butter++ logical-tick boundary probe active via " + RuntimeCompatibility.ButterProbeDescription + ". Dispatcher callbacks are held while the manager-level logical tick is incomplete.");
                    if (RuntimeCompatibility.ButterTickListProbeAvailable)
                        AddReportUnique("Butter++ TickList diagnostic probe also available via " + RuntimeCompatibility.ButterTickListProbeDescription + "; it is diagnostic only and does not define the manager-level commit boundary.");
                }

                if (RuntimeCompatibility.AdaptiveTPSActive)
                {
                    FeatureGate.Suppress("runtime.adaptiveBurst", "Butter++ and AdaptiveTPS are both loaded; Butter++ declares AdaptiveTPS incompatible");
                    AddReportUnique("WARNING: Butter++ and AdaptiveTPS are both loaded. Butter++ declares Blue.adaptiveTPS incompatible; RimMT adaptive burst is disabled for this combination.");
                }

                if (RuntimeCompatibility.DubsPerformanceAnalyzerActive)
                    AddReportUnique("WARNING: Butter++ declares Dubs Performance Analyzer incompatible. Disable DPA when evaluating Butter++ runtime behavior.");
            }

            KeyValuePair<string, List<MethodBase>>[] snapshot;
            lock (Sync)
                snapshot = new List<KeyValuePair<string, List<MethodBase>>>(Targets).ToArray();

            for (int i = 0; i < snapshot.Length; i++)
                IsSafeForPatch(snapshot[i].Key, snapshot[i].Value.ToArray());
        }

        public static bool IsSafeForPatch(string featureId, params MethodBase[] targets)
        {
            if (targets == null) return true;
            for (int i = 0; i < targets.Length; i++)
            {
                MethodBase target = targets[i];
                if (target == null) continue;
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null) continue;

                string patchKind;
                string owner = FirstBlockingForeignOwner(featureId, target, patches.Prefixes, "prefix", out patchKind);
                if (owner == null) owner = FirstBlockingForeignOwner(featureId, target, patches.Postfixes, "postfix", out patchKind);
                if (owner == null) owner = FirstBlockingForeignOwner(featureId, target, patches.Transpilers, "transpiler", out patchKind);
                if (owner == null) owner = FirstBlockingForeignOwner(featureId, target, patches.Finalizers, "finalizer", out patchKind);

                if (owner != null)
                {
                    string typeName = target.DeclaringType == null ? "<unknown>" : target.DeclaringType.FullName;
                    string reason = "foreign Harmony " + patchKind + " by '" + owner + "' on " + typeName + "." + target.Name;
                    FeatureGate.Suppress(featureId, reason);
                    AddReportUnique(featureId + " disabled: " + reason);
                    return false;
                }
            }
            return true;
        }

        private static string FirstBlockingForeignOwner(string featureId, MethodBase target, IList<Patch> patches, string kind, out string patchKind)
        {
            patchKind = kind;
            if (patches == null) return null;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                string owner = patch == null ? null : patch.owner;
                if (string.IsNullOrEmpty(owner) || owner == RimMTBootstrap.HarmonyId)
                    continue;

                if (IsAllowedCoexistence(featureId, target, patch, kind))
                {
                    string typeName = target.DeclaringType == null ? "<unknown>" : target.DeclaringType.FullName;
                    AddReportUnique(featureId + " coexisting with '" + owner + "' " + kind + " on " + typeName + "." + target.Name + ".");
                    continue;
                }

                return owner;
            }
            return null;
        }

        private static bool IsAllowedCoexistence(string featureId, MethodBase target, Patch patch, string patchKind)
        {
            if (!string.Equals(featureId, "runtime.dispatcher", StringComparison.Ordinal))
                return false;
            if (target == null || target.DeclaringType != typeof(TickManager) || target.Name != "TickManagerUpdate")
                return false;

            string owner = patch == null ? string.Empty : patch.owner;

            // AdaptiveTPS changes pacing in TickManagerUpdate with a transpiler. RimMT only
            // brackets/drains around the completed update and does not rewrite AdaptiveTPS IL.
            if (string.Equals(patchKind, "transpiler", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(owner) && owner.IndexOf("adaptivetps", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Butter++ owns TickManagerUpdate to split a logical tick across rendered frames.
            // V0.4.4.1 reads TickManagerPatch._midTickStarted, the actual manager-level state in
            // the supplied Butter++ 1.5 assembly, and only commits when that state is false.
            if (RuntimeCompatibility.IsButterPatch(patch) &&
                (string.Equals(patchKind, "prefix", StringComparison.Ordinal) ||
                 string.Equals(patchKind, "postfix", StringComparison.Ordinal) ||
                 string.Equals(patchKind, "finalizer", StringComparison.Ordinal)))
                return true;

            return false;
        }

        private static void SuppressOptimizationSet(string reason)
        {
            FeatureGate.Suppress("ui.textCache", reason);
            FeatureGate.Suppress("ui.overlayCache", reason);
            FeatureGate.Suppress("ai.reachNoCache", reason);
            FeatureGate.Suppress("parallel.jobScan", reason);
            FeatureGate.Suppress("parallel.haulGlobal", reason);
        }

        private static bool HasLoadedModName(string token)
        {
            IList<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < mods.Count; i++)
            {
                string name = mods[i] == null ? null : mods[i].Name;
                if (!string.IsNullOrEmpty(name) && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void AddReportUnique(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (Sync)
            {
                if (!ReportLines.Contains(line))
                    ReportLines.Add(line);
            }
        }
    }
}
