using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class SMFDispatcherCoexistence093T16
    {
        internal static string Summary()
        {
            MethodBase tickTarget = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
            MethodBase bridgeTarget = AccessTools.Method(typeof(Root_Play), "Update");
            Dictionary<string, FeatureGate.FeatureState> gates = FeatureGate.Snapshot();
            FeatureGate.FeatureState dispatcher;
            gates.TryGetValue("runtime.dispatcher", out dispatcher);

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T22 SMF dispatcher bridge audit: dispatcherEnabled=")
              .Append(FeatureGate.IsEnabled("runtime.dispatcher"))
              .Append(", bridgeTarget=Root_Play.Update")
              .Append(", bridgePatched=").Append(RimMTBootstrap.DispatcherBridgePatched);

            if (dispatcher != null)
                sb.Append(", suppressed=").Append(dispatcher.Suppressed)
                  .Append(", reason=").Append(string.IsNullOrEmpty(dispatcher.Reason) ? "<none>" : dispatcher.Reason);

            int tickForeign = 0, tickSmf = 0;
            int bridgeForeign = 0, bridgeSmf = 0, bridgeRimMT = 0;
            Append(tickTarget, sb, "TickManager", ref tickForeign, ref tickSmf, ref bridgeRimMT, false);
            Append(bridgeTarget, sb, "RootPlay", ref bridgeForeign, ref bridgeSmf, ref bridgeRimMT, true);

            sb.Append(", TickManager[foreign=").Append(tickForeign).Append(",smf=").Append(tickSmf).Append("]")
              .Append(", RootPlay[foreign=").Append(bridgeForeign).Append(",smf=").Append(bridgeSmf)
              .Append(",rimmt=").Append(bridgeRimMT).Append("]")
              .Append(". T22 does not register TickManagerUpdate as runtime.dispatcher authority; SMF frame-budget transpilers remain untouched.");
            return sb.ToString();
        }

        private static void Append(MethodBase target, StringBuilder sb, string label,
            ref int foreign, ref int smf, ref int rimmt, bool countRimMT)
        {
            if (target == null)
            {
                sb.Append(" ").Append(label).Append("[missing]");
                return;
            }

            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return;
            AppendPatches(info.Prefixes, sb, label + ".Prefix", ref foreign, ref smf, ref rimmt, countRimMT);
            AppendPatches(info.Postfixes, sb, label + ".Postfix", ref foreign, ref smf, ref rimmt, countRimMT);
            AppendPatches(info.Transpilers, sb, label + ".Transpiler", ref foreign, ref smf, ref rimmt, countRimMT);
            AppendPatches(info.Finalizers, sb, label + ".Finalizer", ref foreign, ref smf, ref rimmt, countRimMT);
        }

        private static void AppendPatches(IEnumerable<Patch> patches, StringBuilder sb, string kind,
            ref int foreign, ref int smf, ref int rimmt, bool countRimMT)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                string owner = patch.owner ?? "<null>";
                MethodInfo method = patch.PatchMethod;
                string methodName = method == null || method.DeclaringType == null
                    ? "<null>" : method.DeclaringType.FullName + "." + method.Name;
                bool ours = string.Equals(owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal);
                if (ours)
                {
                    if (countRimMT) rimmt++;
                    continue;
                }

                foreign++;
                bool isSmf = owner.IndexOf("simplymorefps", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             owner.IndexOf("game-frame-budget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             methodName.IndexOf("SimplyMoreFPS", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSmf) smf++;
                sb.Append(" ").Append(kind).Append("[owner=").Append(owner)
                  .Append(",method=").Append(methodName)
                  .Append(isSmf ? ",SMF]" : "]");
            }
        }
    }
}
