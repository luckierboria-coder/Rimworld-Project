using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class SMFDispatcherCoexistence093T16
{
	internal static string Summary()
	{
		MethodBase target = AccessTools.Method(typeof(TickManager), "TickManagerUpdate", (Type[])null, (Type[])null);
		MethodInfo target2 = AccessTools.Method(typeof(Root_Play), "Update", (Type[])null, (Type[])null);
		FeatureGate.Snapshot().TryGetValue("runtime.dispatcher", out var value);
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T22 SMF dispatcher bridge audit: dispatcherEnabled=").Append(FeatureGate.IsEnabled("runtime.dispatcher")).Append(", bridgeTarget=Root_Play.Update")
			.Append(", bridgePatched=")
			.Append(RimMTBootstrap.DispatcherBridgePatched);
		if (value != null)
		{
			stringBuilder.Append(", suppressed=").Append(value.Suppressed).Append(", reason=")
				.Append(string.IsNullOrEmpty(value.Reason) ? "<none>" : value.Reason);
		}
		int foreign = 0;
		int smf = 0;
		int foreign2 = 0;
		int smf2 = 0;
		int rimmt = 0;
		Append(target, stringBuilder, "TickManager", ref foreign, ref smf, ref rimmt, countRimMT: false);
		Append(target2, stringBuilder, "RootPlay", ref foreign2, ref smf2, ref rimmt, countRimMT: true);
		stringBuilder.Append(", TickManager[foreign=").Append(foreign).Append(",smf=")
			.Append(smf)
			.Append("]")
			.Append(", RootPlay[foreign=")
			.Append(foreign2)
			.Append(",smf=")
			.Append(smf2)
			.Append(",rimmt=")
			.Append(rimmt)
			.Append("]")
			.Append(". T22 does not register TickManagerUpdate as runtime.dispatcher authority; SMF frame-budget transpilers remain untouched.");
		return stringBuilder.ToString();
	}

	private static void Append(MethodBase target, StringBuilder sb, string label, ref int foreign, ref int smf, ref int rimmt, bool countRimMT)
	{
		if (target == null)
		{
			sb.Append(" ").Append(label).Append("[missing]");
			return;
		}
		Patches patchInfo = Harmony.GetPatchInfo(target);
		if (patchInfo != null)
		{
			AppendPatches(patchInfo.Prefixes, sb, label + ".Prefix", ref foreign, ref smf, ref rimmt, countRimMT);
			AppendPatches(patchInfo.Postfixes, sb, label + ".Postfix", ref foreign, ref smf, ref rimmt, countRimMT);
			AppendPatches(patchInfo.Transpilers, sb, label + ".Transpiler", ref foreign, ref smf, ref rimmt, countRimMT);
			AppendPatches(patchInfo.Finalizers, sb, label + ".Finalizer", ref foreign, ref smf, ref rimmt, countRimMT);
		}
	}

	private static void AppendPatches(IEnumerable<Patch> patches, StringBuilder sb, string kind, ref int foreign, ref int smf, ref int rimmt, bool countRimMT)
	{
		if (patches == null)
		{
			return;
		}
		foreach (Patch patch in patches)
		{
			if (patch == null)
			{
				continue;
			}
			string text = patch.owner ?? "<null>";
			MethodInfo patchMethod = patch.PatchMethod;
			string text2 = ((patchMethod == null || patchMethod.DeclaringType == null) ? "<null>" : (patchMethod.DeclaringType.FullName + "." + patchMethod.Name));
			if (string.Equals(text, "allen.rimmt", StringComparison.Ordinal))
			{
				if (countRimMT)
				{
					rimmt++;
				}
				continue;
			}
			foreign++;
			bool flag = text.IndexOf("simplymorefps", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("game-frame-budget", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("SimplyMoreFPS", StringComparison.OrdinalIgnoreCase) >= 0;
			if (flag)
			{
				smf++;
			}
			sb.Append(" ").Append(kind).Append("[owner=")
				.Append(text)
				.Append(",method=")
				.Append(text2)
				.Append(flag ? ",SMF]" : "]");
		}
	}
}
