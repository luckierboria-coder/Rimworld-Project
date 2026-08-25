using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    public static class CompatibilityGuard
    {
        private static readonly List<string> ReportLines = new List<string>();
        public static IList<string> Report => ReportLines.AsReadOnly();

        internal static void RunBaselineScan()
        {
            ReportLines.Clear();
            ReportLines.Add("V0.2 foundation: invasive parallel modules are disabled by design.");
            ReportLines.Add("Loaded mods: " + LoadedModManager.RunningModsListForReading.Count);
        }

        public static bool IsSafeForParallelPatch(string featureId, params MethodBase[] targets)
        {
            foreach (MethodBase target in targets)
            {
                if (target == null) continue;
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null) continue;

                string owner = FirstForeignOwner(patches.Prefixes);
                if (owner == null) owner = FirstForeignOwner(patches.Transpilers);
                if (owner == null) owner = FirstForeignOwner(patches.Finalizers);

                if (owner != null)
                {
                    string typeName = target.DeclaringType == null ? "<unknown>" : target.DeclaringType.FullName;
                    string reason = "foreign Harmony mutation by '" + owner + "' on " + typeName + "." + target.Name;
                    FeatureGate.Suppress(featureId, reason);
                    ReportLines.Add(featureId + " disabled: " + reason);
                    return false;
                }
            }
            return true;
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
