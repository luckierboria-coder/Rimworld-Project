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
            lock (Sync)
            {
                ReportLines.Clear();
                ReportLines.Add("Loaded mods: " + LoadedModManager.RunningModsListForReading.Count);
                ReportLines.Add("Policy: whitelist-only, fail-closed, vanilla fallback.");
            }

            if (AccessTools.TypeByName("RimThreaded.RimThreaded") != null || HasLoadedModName("RimThreaded"))
            {
                SuppressOptimizationSet("another RimThreaded implementation is loaded");
                AddReport("All RimMT gameplay optimizations disabled because RimThreaded was detected.");
                return;
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

                string owner = FirstForeignOwner(patches.Prefixes);
                if (owner == null) owner = FirstForeignOwner(patches.Postfixes);
                if (owner == null) owner = FirstForeignOwner(patches.Transpilers);
                if (owner == null) owner = FirstForeignOwner(patches.Finalizers);

                if (owner != null)
                {
                    string typeName = target.DeclaringType == null ? "<unknown>" : target.DeclaringType.FullName;
                    string reason = "foreign Harmony patch by '" + owner + "' on " + typeName + "." + target.Name;
                    FeatureGate.Suppress(featureId, reason);
                    AddReport(featureId + " disabled: " + reason);
                    return false;
                }
            }
            return true;
        }

        private static void SuppressOptimizationSet(string reason)
        {
            FeatureGate.Suppress("ui.textCache", reason);
            FeatureGate.Suppress("ui.overlayCache", reason);
            FeatureGate.Suppress("ai.reachNoCache", reason);
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

        private static void AddReport(string line)
        {
            lock (Sync) ReportLines.Add(line);
        }

        private static string FirstForeignOwner(IList<Patch> patches)
        {
            if (patches == null) return null;
            for (int i = 0; i < patches.Count; i++)
            {
                string owner = patches[i].owner;
                if (!string.IsNullOrEmpty(owner) && owner != RimMTBootstrap.HarmonyId)
                    return owner;
            }
            return null;
        }
    }
}
