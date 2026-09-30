using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class TailPathfinderPatches093T3
{
	internal static void Apply(Harmony harmony)
	{
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Expected O, but got Unknown
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		int num = 0;
		try
		{
			MethodInfo[] methods = typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (!(methodInfo == null) && !(methodInfo.Name != "FindPath"))
				{
					HarmonyMethod val = new HarmonyMethod(typeof(TailPathfinderPatches093T3), "Prefix", (Type[])null)
					{
						priority = 800
					};
					HarmonyMethod val2 = new HarmonyMethod(typeof(TailPathfinderPatches093T3), "Postfix", (Type[])null)
					{
						priority = 0
					};
					harmony.Patch((MethodBase)methodInfo, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			Log.Message("[RimMT] T3 bounded PathFinder attribution installed on " + num + " FindPath overload(s); Stopwatch is active only inside T2 deep windows.");
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] T3 PathFinder attribution failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void Prefix(ref long __state)
	{
		__state = (TailPawnAttribution093T2.DeepActive ? TailPathfinderAttribution093T3.BeginCall() : 0);
	}

	public static void Postfix(long __state)
	{
		if (__state != 0L)
		{
			TailPathfinderAttribution093T3.EndCall(__state);
		}
	}
}
