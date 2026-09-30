using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT.Diagnostics;

[StaticConstructorOnStartup]
internal static class DiagnosticsBootstrap
{
	internal const string HarmonyId = "allen.rimmt.diagnostics";

	internal const string Version = "0.20.6";

	private static int patched;

	private static int missing;

	static DiagnosticsBootstrap()
	{
		try
		{
			RimMTDiagnosticsSettings.SampleEveryTicks = 1;
			RimMTDiagnosticsSettings.EnableWaitTrace = false;
			RimMTDiagnosticsSettings.EnableSearchTiming = false;
			Harmony harmony = new Harmony(HarmonyId);
			Patch(harmony, AccessTools.Method(typeof(TickManager), "DoSingleTick"), "TickPrefix", "TickPostfix");
			Patch(harmony, AccessTools.Method(typeof(Map), "MapPostTick"), "MapPostPrefix", "MapPostPostfix");
			Patch(harmony, AccessTools.Method(typeof(RimWorld.Planet.World), "WorldTick"), "WorldPrefix", "WorldPostfix");
			Patch(harmony, AccessTools.Method(typeof(RimWorld.Storyteller), "StorytellerTick"), "StorytellerPrefix", "StorytellerPostfix");
			Log.Message("[RimMT Diagnostics] v0.20.6 safe top-level attribution initialized: patched=" + patched +
				", missing=" + missing + ". Pawn, WorkGiver, GenClosest, Reachability and MapComponent override probes remain absent.");
		}
		catch (Exception ex)
		{
			Log.Error("[RimMT Diagnostics] bootstrap failed: " + ex);
		}
	}

	private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Expected O, but got Unknown
		//IL_0054: Expected O, but got Unknown
		if (target == null)
		{
			missing++;
			return;
		}
		try
		{
			harmony.Patch(target, new HarmonyMethod(typeof(DiagnosticsPatches), prefix, (Type[])null)
			{
				priority = 800
			}, new HarmonyMethod(typeof(DiagnosticsPatches), postfix, (Type[])null)
			{
				priority = 0
			}, (HarmonyMethod)null, (HarmonyMethod)null);
			patched++;
		}
		catch (Exception ex)
		{
			missing++;
			Log.Warning("[RimMT Diagnostics] patch failed for " + target.DeclaringType.FullName + "." + target.Name + ": " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void PatchNamedMethods(Harmony harmony, Type type, string name, string prefix, string postfix)
	{
		List<MethodInfo> declaredMethods;
		try
		{
			declaredMethods = AccessTools.GetDeclaredMethods(type);
		}
		catch
		{
			missing++;
			return;
		}
		for (int i = 0; i < declaredMethods.Count; i++)
		{
			MethodInfo methodInfo = declaredMethods[i];
			if (methodInfo != null && methodInfo.Name == name)
			{
				Patch(harmony, methodInfo, prefix, postfix);
			}
		}
	}
}
