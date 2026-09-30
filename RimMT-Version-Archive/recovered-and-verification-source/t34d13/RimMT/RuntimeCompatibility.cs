using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class RuntimeCompatibility
{
	private const string ButterPackageId = "olli.butterplusplus";

	private const string AdaptiveTpsPackageId = "blue.adaptivetps";

	private const string DpaPackageId = "dubwise.dubsperformanceanalyzer";

	private const string DpaSteamPackageId = "dubwise.dubsperformanceanalyzer.steam";

	private static bool initialized;

	private static bool butterPlusPlusActive;

	private static bool adaptiveTpsActive;

	private static bool dubsPerformanceAnalyzerActive;

	private static FieldInfo butterLogicalTickField;

	private static string butterLogicalTickProbeDescription = "not initialized";

	private static MethodInfo butterTickListMidTickGetter;

	private static FieldInfo butterTickListMidTickField;

	private static string butterTickListProbeDescription = "not initialized";

	internal static bool ButterPlusPlusActive
	{
		get
		{
			EnsureInitialized();
			return butterPlusPlusActive;
		}
	}

	internal static bool AdaptiveTPSActive
	{
		get
		{
			EnsureInitialized();
			return adaptiveTpsActive;
		}
	}

	internal static bool DubsPerformanceAnalyzerActive
	{
		get
		{
			EnsureInitialized();
			return dubsPerformanceAnalyzerActive;
		}
	}

	internal static bool ButterLogicalTickProbeAvailable
	{
		get
		{
			EnsureInitialized();
			return butterLogicalTickField != null;
		}
	}

	internal static bool ButterTickListProbeAvailable
	{
		get
		{
			EnsureInitialized();
			if (!(butterTickListMidTickGetter != null))
			{
				return butterTickListMidTickField != null;
			}
			return true;
		}
	}

	internal static string ButterProbeDescription
	{
		get
		{
			EnsureInitialized();
			return butterLogicalTickProbeDescription;
		}
	}

	internal static string ButterTickListProbeDescription
	{
		get
		{
			EnsureInitialized();
			return butterTickListProbeDescription;
		}
	}

	internal static void Initialize()
	{
		if (!initialized)
		{
			initialized = true;
			butterPlusPlusActive = HasPackage("olli.butterplusplus") || AccessTools.TypeByName("ButterPlusPlus.TickManagerPatch") != null;
			adaptiveTpsActive = HasPackage("blue.adaptivetps") || AccessTools.TypeByName("AdaptiveTPS.AdaptiveTickComponent") != null;
			dubsPerformanceAnalyzerActive = HasPackage("dubwise.dubsperformanceanalyzer") || HasPackage("dubwise.dubsperformanceanalyzer.steam");
			if (!butterPlusPlusActive)
			{
				butterLogicalTickProbeDescription = "Butter++ not loaded";
				butterTickListProbeDescription = "Butter++ not loaded";
			}
			else
			{
				ProbeButterLogicalTickState();
				ProbeButterTickListState();
			}
		}
	}

	private static void ProbeButterLogicalTickState()
	{
		Type type = AccessTools.TypeByName("ButterPlusPlus.TickManagerPatch");
		if (type == null)
		{
			butterLogicalTickProbeDescription = "Butter++ package loaded but TickManagerPatch type was not found";
			return;
		}
		try
		{
			FieldInfo fieldInfo = AccessTools.Field(type, "_midTickStarted");
			if (fieldInfo != null && fieldInfo.FieldType == typeof(bool) && fieldInfo.IsStatic)
			{
				butterLogicalTickField = fieldInfo;
				butterLogicalTickProbeDescription = "ButterPlusPlus.TickManagerPatch._midTickStarted";
			}
			else
			{
				butterLogicalTickProbeDescription = "Butter++ detected but TickManagerPatch._midTickStarted was not found as a static bool";
			}
		}
		catch (Exception ex)
		{
			butterLogicalTickProbeDescription = "Butter++ logical-tick probe failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static void ProbeButterTickListState()
	{
		Type type = AccessTools.TypeByName("ButterPlusPlus.TickListPatch");
		if (type == null)
		{
			butterTickListProbeDescription = "TickListPatch type was not found";
			return;
		}
		try
		{
			PropertyInfo propertyInfo = AccessTools.Property(type, "MidTick");
			if (propertyInfo != null && propertyInfo.PropertyType == typeof(bool))
			{
				MethodInfo getMethod = propertyInfo.GetGetMethod(nonPublic: true);
				if (getMethod != null && getMethod.IsStatic)
				{
					butterTickListMidTickGetter = getMethod;
					butterTickListProbeDescription = "ButterPlusPlus.TickListPatch.MidTick";
					return;
				}
			}
			FieldInfo fieldInfo = AccessTools.Field(type, "_midTick");
			if (fieldInfo != null && fieldInfo.FieldType == typeof(bool) && fieldInfo.IsStatic)
			{
				butterTickListMidTickField = fieldInfo;
				butterTickListProbeDescription = "ButterPlusPlus.TickListPatch._midTick";
			}
			else
			{
				butterTickListProbeDescription = "TickListPatch detected but no compatible MidTick property/field was found";
			}
		}
		catch (Exception ex)
		{
			butterTickListProbeDescription = "Butter++ TickList diagnostic probe failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	internal static bool TryGetButterLogicalTickInProgress(out bool inProgress)
	{
		EnsureInitialized();
		inProgress = false;
		if (!butterPlusPlusActive)
		{
			return true;
		}
		if (butterLogicalTickField == null)
		{
			return false;
		}
		try
		{
			inProgress = (bool)butterLogicalTickField.GetValue(null);
			return true;
		}
		catch (Exception ex)
		{
			butterLogicalTickField = null;
			butterLogicalTickProbeDescription = "Butter++ logical-tick runtime read failed: " + ex.GetType().Name + ": " + ex.Message;
			inProgress = true;
			return false;
		}
	}

	internal static bool TryGetButterTickListMidTick(out bool midTick)
	{
		EnsureInitialized();
		midTick = false;
		if (!butterPlusPlusActive)
		{
			return true;
		}
		try
		{
			if (butterTickListMidTickGetter != null)
			{
				midTick = (bool)butterTickListMidTickGetter.Invoke(null, null);
				return true;
			}
			if (butterTickListMidTickField != null)
			{
				midTick = (bool)butterTickListMidTickField.GetValue(null);
				return true;
			}
		}
		catch (Exception ex)
		{
			butterTickListMidTickGetter = null;
			butterTickListMidTickField = null;
			butterTickListProbeDescription = "Butter++ TickList runtime read failed: " + ex.GetType().Name + ": " + ex.Message;
		}
		return false;
	}

	internal static bool IsButterPatch(Patch patch)
	{
		if (patch == null)
		{
			return false;
		}
		MethodInfo patchMethod = patch.PatchMethod;
		Type type = ((patchMethod == null) ? null : patchMethod.DeclaringType);
		string obj = ((type == null) ? string.Empty : type.FullName);
		string text = ((type == null || type.Assembly == null) ? string.Empty : type.Assembly.GetName().Name);
		if (!obj.StartsWith("ButterPlusPlus.", StringComparison.Ordinal) && text.IndexOf("ButterPlusPlus", StringComparison.OrdinalIgnoreCase) < 0)
		{
			if (!string.IsNullOrEmpty(patch.owner))
			{
				return patch.owner.IndexOf("butterplusplus", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return false;
		}
		return true;
	}

	internal static string Summary()
	{
		EnsureInitialized();
		return "Runtime compatibility: Butter++=" + butterPlusPlusActive + (butterPlusPlusActive ? (" (LogicalTickProbe=" + ButterLogicalTickProbeAvailable + ", source=" + butterLogicalTickProbeDescription + ", TickListProbe=" + ButterTickListProbeAvailable + ", tickListSource=" + butterTickListProbeDescription + ")") : string.Empty) + ", AdaptiveTPS=" + adaptiveTpsActive + ", DubsPerformanceAnalyzer=" + dubsPerformanceAnalyzerActive;
	}

	private static bool HasPackage(string packageId)
	{
		if (string.IsNullOrEmpty(packageId))
		{
			return false;
		}
		List<ModContentPack> runningModsListForReading = LoadedModManager.RunningModsListForReading;
		for (int i = 0; i < runningModsListForReading.Count; i++)
		{
			ModContentPack val = runningModsListForReading[i];
			if (val != null && string.Equals(val.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static void EnsureInitialized()
	{
		if (!initialized)
		{
			Initialize();
		}
	}
}
