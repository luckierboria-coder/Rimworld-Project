using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT;

public static class CompatibilityGuard
{
	private static readonly object Sync = new object();

	private static readonly List<string> ReportLines = new List<string>();

	private static readonly Dictionary<string, List<MethodBase>> Targets = new Dictionary<string, List<MethodBase>>();

	public static IList<string> Report
	{
		get
		{
			lock (Sync)
			{
				return new List<string>(ReportLines).AsReadOnly();
			}
		}
	}

	public static void RegisterTarget(string featureId, MethodBase target)
	{
		if (string.IsNullOrEmpty(featureId) || target == null)
		{
			return;
		}
		lock (Sync)
		{
			if (!Targets.TryGetValue(featureId, out var value))
			{
				value = new List<MethodBase>();
				Targets.Add(featureId, value);
			}
			if (!value.Contains(target))
			{
				value.Add(target);
			}
		}
	}

	internal static void RunBaselineScan()
	{
		RuntimeCompatibility.Initialize();
		lock (Sync)
		{
			ReportLines.Clear();
			ReportLines.Add("Loaded mods: " + LoadedModManager.RunningModsListForReading.Count);
			ReportLines.Add("Policy: bounded-risk whitelist, sampled validation, Vanilla state commit.");
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
				{
					AddReportUnique("Butter++ TickList diagnostic probe also available via " + RuntimeCompatibility.ButterTickListProbeDescription + "; it is diagnostic only and does not define the manager-level commit boundary.");
				}
			}
			if (RuntimeCompatibility.AdaptiveTPSActive)
			{
				FeatureGate.Suppress("runtime.adaptiveBurst", "Butter++ and AdaptiveTPS are both loaded; Butter++ declares AdaptiveTPS incompatible");
				AddReportUnique("WARNING: Butter++ and AdaptiveTPS are both loaded. Butter++ declares Blue.adaptiveTPS incompatible; RimMT adaptive burst is disabled for this combination.");
			}
			if (RuntimeCompatibility.DubsPerformanceAnalyzerActive)
			{
				AddReportUnique("WARNING: Butter++ declares Dubs Performance Analyzer incompatible. Disable DPA when evaluating Butter++ runtime behavior.");
			}
		}
		KeyValuePair<string, List<MethodBase>>[] array;
		lock (Sync)
		{
			array = new List<KeyValuePair<string, List<MethodBase>>>(Targets).ToArray();
		}
		for (int i = 0; i < array.Length; i++)
		{
			IsSafeForPatch(array[i].Key, array[i].Value.ToArray());
		}
	}

	public static bool IsSafeForPatch(string featureId, params MethodBase[] targets)
	{
		if (targets == null)
		{
			return true;
		}
		foreach (MethodBase methodBase in targets)
		{
			if (methodBase == null)
			{
				continue;
			}
			Patches patchInfo = Harmony.GetPatchInfo(methodBase);
			if (patchInfo != null)
			{
				string patchKind;
				string text = FirstBlockingForeignOwner(featureId, methodBase, patchInfo.Prefixes, "prefix", out patchKind);
				if (text == null)
				{
					text = FirstBlockingForeignOwner(featureId, methodBase, patchInfo.Postfixes, "postfix", out patchKind);
				}
				if (text == null)
				{
					text = FirstBlockingForeignOwner(featureId, methodBase, patchInfo.Transpilers, "transpiler", out patchKind);
				}
				if (text == null)
				{
					text = FirstBlockingForeignOwner(featureId, methodBase, patchInfo.Finalizers, "finalizer", out patchKind);
				}
				if (text != null)
				{
					string text2 = ((methodBase.DeclaringType == null) ? "<unknown>" : methodBase.DeclaringType.FullName);
					string text3 = "foreign Harmony " + patchKind + " by '" + text + "' on " + text2 + "." + methodBase.Name;
					FeatureGate.Suppress(featureId, text3);
					AddReportUnique(featureId + " disabled: " + text3);
					return false;
				}
			}
		}
		return true;
	}

	private static string FirstBlockingForeignOwner(string featureId, MethodBase target, IList<Patch> patches, string kind, out string patchKind)
	{
		patchKind = kind;
		if (patches == null)
		{
			return null;
		}
		for (int i = 0; i < patches.Count; i++)
		{
			Patch val = patches[i];
			string text = val?.owner;
			if (string.IsNullOrEmpty(text) || text == "allen.rimmt")
			{
				continue;
			}
			if (string.Equals(text, "allen.rimmt.diagnostics", StringComparison.Ordinal) && (string.Equals(kind, "prefix", StringComparison.Ordinal) || string.Equals(kind, "postfix", StringComparison.Ordinal)))
			{
				MethodInfo methodInfo = ((val == null) ? null : val.PatchMethod);
				if (((methodInfo == null || methodInfo.DeclaringType == null) ? string.Empty : methodInfo.DeclaringType.FullName).StartsWith("RimMT.Diagnostics.", StringComparison.Ordinal))
				{
					continue;
				}
			}
			if (IsAllowedCoexistence(featureId, target, val, kind))
			{
				string text2 = ((target.DeclaringType == null) ? "<unknown>" : target.DeclaringType.FullName);
				AddReportUnique(featureId + " coexisting with '" + text + "' " + kind + " on " + text2 + "." + target.Name + ".");
				continue;
			}
			return text;
		}
		return null;
	}

	private static bool IsAllowedCoexistence(string featureId, MethodBase target, Patch patch, string patchKind)
	{
		if (string.Equals(featureId, "runtime.dispatcher", StringComparison.Ordinal))
		{
			if (target == null || target.DeclaringType != typeof(TickManager) || target.Name != "TickManagerUpdate")
			{
				return false;
			}
			string text = ((patch == null) ? string.Empty : patch.owner);
			if (string.Equals(patchKind, "transpiler", StringComparison.Ordinal) && !string.IsNullOrEmpty(text) && text.IndexOf("adaptivetps", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			if (RuntimeCompatibility.IsButterPatch(patch) && (string.Equals(patchKind, "prefix", StringComparison.Ordinal) || string.Equals(patchKind, "postfix", StringComparison.Ordinal) || string.Equals(patchKind, "finalizer", StringComparison.Ordinal)))
			{
				return true;
			}
			return false;
		}
		if (string.Equals(featureId, "parallel.reachProfile", StringComparison.Ordinal) && target != null && target.DeclaringType == typeof(Reachability) && target.Name == "CanReach" && string.Equals(patchKind, "prefix", StringComparison.Ordinal))
		{
			string a = ((patch == null) ? string.Empty : patch.owner);
			MethodInfo methodInfo = ((patch == null) ? null : patch.PatchMethod);
			string text2 = ((methodInfo == null || methodInfo.DeclaringType == null) ? string.Empty : methodInfo.DeclaringType.FullName);
			if ((string.Equals(a, "OskarPotocki.VEF", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "OskarPotocki.VFECore", StringComparison.OrdinalIgnoreCase)) && text2.IndexOf("PhasingPatches", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		if (string.Equals(featureId, "parallel.reachProfile", StringComparison.Ordinal) && target != null && target.DeclaringType == typeof(Reachability) && target.Name == "CanReach" && string.Equals(patchKind, "postfix", StringComparison.Ordinal))
		{
			string a2 = ((patch == null) ? string.Empty : patch.owner);
			MethodInfo methodInfo2 = ((patch == null) ? null : patch.PatchMethod);
			string a3 = ((methodInfo2 == null || methodInfo2.DeclaringType == null) ? string.Empty : methodInfo2.DeclaringType.FullName);
			if (string.Equals(a2, "pathfinding.framework", StringComparison.OrdinalIgnoreCase) && string.Equals(a3, "PathfindingFramework.Patches.DevTool.PathDebugging.Reachability_CanReach_DebugPatch", StringComparison.Ordinal) && methodInfo2 != null && string.Equals(methodInfo2.Name, "Postfix", StringComparison.Ordinal))
			{
				return true;
			}
			if (string.Equals(a2, "Orion.Hospitality", StringComparison.OrdinalIgnoreCase) && string.Equals(a3, "Hospitality.Patches.Reachability_Patch+CanReach", StringComparison.Ordinal) && methodInfo2 != null && string.Equals(methodInfo2.Name, "Postfix", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private static void SuppressOptimizationSet(string reason)
	{
		FeatureGate.Suppress("ui.textCache", reason);
		FeatureGate.Suppress("ui.overlayCache", reason);
		FeatureGate.Suppress("ai.reachNoCache", reason);
		FeatureGate.Suppress("parallel.jobScan", reason);
		FeatureGate.Suppress("parallel.haulGlobal", reason);
		FeatureGate.Suppress("parallel.jobPartition", reason);
		FeatureGate.Suppress("parallel.reachProfile", reason);
		FeatureGate.Suppress("parallel.regionHint", reason);
	}

	private static bool HasLoadedModName(string token)
	{
		IList<ModContentPack> runningModsListForReading = LoadedModManager.RunningModsListForReading;
		for (int i = 0; i < runningModsListForReading.Count; i++)
		{
			string text = ((runningModsListForReading[i] == null) ? null : runningModsListForReading[i].Name);
			if (!string.IsNullOrEmpty(text) && text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	private static void AddReportUnique(string line)
	{
		if (string.IsNullOrEmpty(line))
		{
			return;
		}
		lock (Sync)
		{
			if (!ReportLines.Contains(line))
			{
				ReportLines.Add(line);
			}
		}
	}
}
