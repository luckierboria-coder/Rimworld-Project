using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class RimMTPatches
{
	internal static void Apply(Harmony harmony)
	{
		TryPatchAdaptiveTickSampler(harmony);
		SafeFeaturePatch(harmony, "ui.textCache", () => AccessTools.Method(typeof(Text), "CalcHeight", new Type[2]
		{
			typeof(string),
			typeof(float)
		}, (Type[])null), typeof(TextMetricCache), "CalcHeightPrefix", "CalcHeightPostfix");
		SafeFeaturePatch(harmony, "ui.textCache", () => AccessTools.Method(typeof(Text), "CalcSize", new Type[1] { typeof(string) }, (Type[])null), typeof(TextMetricCache), "CalcSizePrefix", "CalcSizePostfix");
		if (0 + PatchAllNamed(harmony, "ai.pathTopology", typeof(PathGrid), "RecalculatePerceivedPathCostAt", typeof(PathGridInvalidation), null, "Postfix") + PatchAllNamed(harmony, "ai.pathTopology", typeof(PathGrid), "RecalculateAllPerceivedPathCosts", typeof(PathGridInvalidation), null, "Postfix") == 0)
		{
			FeatureGate.Suppress("ai.pathTopology", "no compatible PathGrid invalidation targets were patched");
		}
	}

	private static void TryPatchAdaptiveTickSampler(Harmony harmony)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Expected O, but got Unknown
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(TickManager), "DoSingleTick", (Type[])null, (Type[])null);
			if (methodBase == null)
			{
				FeatureGate.Suppress("runtime.adaptiveBurst", "TickManager.DoSingleTick was not found");
				return;
			}
			HarmonyMethod val = new HarmonyMethod(typeof(RimMTPatches), "AdaptiveTickPrefix", (Type[])null)
			{
				priority = 800
			};
			HarmonyMethod val2 = new HarmonyMethod(typeof(RimMTPatches), "AdaptiveTickPostfix", (Type[])null)
			{
				priority = 0
			};
			harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress("runtime.adaptiveBurst", "DoSingleTick pressure sampler install failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] runtime.adaptiveBurst pressure sampler disabled: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void AdaptiveTickPrefix(ref long __state)
	{
		__state = 0L;
		if (FeatureGate.IsEnabled("runtime.adaptiveBurst") && !RuntimeCompatibility.ButterPlusPlusActive)
		{
			__state = Stopwatch.GetTimestamp();
		}
	}

	public static void AdaptiveTickPostfix(long __state)
	{
		if (__state != 0L)
		{
			AdaptiveLoadBalancer.RecordTick(__state);
		}
	}

	private static void SafeFeaturePatch(Harmony harmony, string featureId, Func<MethodBase> resolver, Type patchType, string prefixName, string postfixName)
	{
		try
		{
			MethodBase methodBase = resolver?.Invoke();
			if (methodBase == null)
			{
				FeatureGate.Suppress(featureId, "target method was not found for RimWorld 1.5");
				return;
			}
			CompatibilityGuard.RegisterTarget(featureId, methodBase);
			Patch(harmony, methodBase, patchType, prefixName, postfixName);
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress(featureId, "patch installation failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] " + featureId + " disabled: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static int PatchAllNamed(Harmony harmony, string featureId, Type targetType, string methodName, Type patchType, string prefixName, string postfixName)
	{
		if (targetType == null)
		{
			return 0;
		}
		int num = 0;
		try
		{
			MethodInfo[] methods = targetType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (!(methodInfo == null) && !(methodInfo.Name != methodName))
				{
					Patch(harmony, methodInfo, patchType, prefixName, postfixName);
					CompatibilityGuard.RegisterTarget(featureId, methodInfo);
					num++;
				}
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] " + featureId + " partial install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
		return num;
	}

	private static void Patch(Harmony harmony, MethodBase target, Type patchType, string prefixName, string postfixName)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		HarmonyMethod val = (string.IsNullOrEmpty(prefixName) ? ((HarmonyMethod)null) : new HarmonyMethod(patchType, prefixName, (Type[])null));
		HarmonyMethod val2 = (string.IsNullOrEmpty(postfixName) ? ((HarmonyMethod)null) : new HarmonyMethod(patchType, postfixName, (Type[])null));
		harmony.Patch(target, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
	}
}
