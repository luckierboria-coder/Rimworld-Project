using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

[StaticConstructorOnStartup]
internal static class LargeSetTailRescue092
{
	private const int LargeSourceCount = 128;

	private const int MaxSourceCount = 16384;

	private const int WindowSize = 32;

	[ThreadStatic]
	private static Thing[] windowThings;

	[ThreadStatic]
	private static int[] windowDistSq;

	[ThreadStatic]
	private static float[] windowPriority;

	private static int failureLogs;

	private static long observed;

	private static long largeEligible;

	private static long boundedProofs;

	private static long boundedNullFallbacks;

	private static long unsafePriorityBypass;

	private static long failures;

	static LargeSetTailRescue092()
	{
	}

	private static void Install()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Expected O, but got Unknown
		try
		{
			Harmony val = new Harmony("allen.rimmt");
			int num = 0;
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo in methods)
			{
				if (methodInfo == null || methodInfo.ReturnType != typeof(Thing) || (methodInfo.Name != "ClosestThing_Global_Reachable" && methodInfo.Name != "ClosestThingReachable"))
				{
					continue;
				}
				ParameterInfo[] parameters = methodInfo.GetParameters();
				bool flag = false;
				bool flag2 = false;
				bool flag3 = false;
				for (int j = 0; j < parameters.Length; j++)
				{
					Type parameterType = parameters[j].ParameterType;
					if (parameterType == typeof(Map))
					{
						flag = true;
					}
					else if (parameterType == typeof(IntVec3))
					{
						flag2 = true;
					}
					else if (typeof(IEnumerable<Thing>).IsAssignableFrom(parameterType))
					{
						flag3 = true;
					}
				}
				if (flag && flag2 && flag3)
				{
					val.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(LargeSetTailRescue092), "Prefix", (Type[])null)
					{
						priority = 1050
					}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			Log.Message("[RimMT] Unified RC2 Stage3 large-set rescue active on " + num + " GenClosest overload(s); >=128 only, best-32 bounded proof, Vanilla fallback on uncertainty.");
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] Unified Stage3 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static bool Prefix(MethodBase __originalMethod, object[] __args, ref Thing __result)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Invalid comparison between Unknown and I4
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0221: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_0227: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b4: Unknown result type (might be due to invalid IL or missing references)
		if (!JobGiverGlobalNearest04181.InJobGiverScope || __originalMethod == null || __args == null || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		observed++;
		try
		{
			ParameterInfo[] parameters = __originalMethod.GetParameters();
			Map arg = GetArg<Map>(parameters, __args, new string[1] { "map" });
			IntVec3 arg2 = GetArg<IntVec3>(parameters, __args, new string[2] { "root", "center" });
			IEnumerable<Thing> enumerable = GetEnumerable(parameters, __args, (__originalMethod.Name == "ClosestThingReachable") ? "customGlobalSearchSet" : "searchSet");
			if (arg == null || enumerable == null)
			{
				return true;
			}
			if (__originalMethod.Name == "ClosestThingReachable" && !GetBoolArg(parameters, __args, "forceGlobalSearch", GetBoolArg(parameters, __args, "forceAllowGlobalSearch", fallback: false)))
			{
				return true;
			}
			if (GetBoolArg(parameters, __args, "canLookInHaulableSources", fallback: false) || GetBoolArg(parameters, __args, "lookInHaulSources", fallback: false))
			{
				return true;
			}
			if (!(enumerable is ICollection<Thing> { Count: >=128, Count: <=16384 }))
			{
				return true;
			}
			largeEligible++;
			Predicate<Thing> delegateArg = GetDelegateArg<Predicate<Thing>>(parameters, __args, "validator");
			bool flag = delegateArg != null && IsSafeVanillaWorkValidator(delegateArg);
			Func<Thing, float> delegateArg2 = GetDelegateArg<Func<Thing, float>>(parameters, __args, "priorityGetter");
			bool flag2 = delegateArg2 != null;
			if (flag2 && !IsSafeWorkScannerPriority(delegateArg2))
			{
				unsafePriorityBypass++;
				return true;
			}
			PathEndMode arg3 = GetArg<PathEndMode>(parameters, __args, new string[1] { "peMode" });
			TraverseParms arg4 = GetArg<TraverseParms>(parameters, __args, new string[2] { "traverseParams", "traverseParms" });
			float floatArg = GetFloatArg(parameters, __args, "maxDistance", 9999f);
			float num = ((floatArg >= 99999f) ? float.MaxValue : (floatArg * floatArg));
			EnsureWindow();
			int count = 0;
			foreach (Thing item in enumerable)
			{
				if (item != null && item.Spawned && item.Map == arg)
				{
					IntVec3 val = item.PositionHeld - arg2;
					int lengthHorizontalSquared = ((IntVec3)(ref val)).LengthHorizontalSquared;
					if (!((float)lengthHorizontalSquared > num))
					{
						float priority = (flag2 ? delegateArg2(item) : 0f);
						InsertCandidate(item, lengthHorizontalSquared, priority, flag2, ref count);
					}
				}
			}
			for (int i = 0; i < count; i++)
			{
				Thing val2 = windowThings[i];
				if (val2 == null)
				{
					continue;
				}
				if (flag)
				{
					if (!delegateArg(val2) || !arg.reachability.CanReach(arg2, LocalTargetInfo.op_Implicit(val2.SpawnedParentOrMe), arg3, arg4))
					{
						continue;
					}
				}
				else if (!arg.reachability.CanReach(arg2, LocalTargetInfo.op_Implicit(val2.SpawnedParentOrMe), arg3, arg4) || (delegateArg != null && !delegateArg(val2)))
				{
					continue;
				}
				__result = val2;
				boundedProofs++;
				ClearWindow(count);
				return false;
			}
			boundedNullFallbacks++;
			ClearWindow(count);
			return true;
		}
		catch (Exception ex)
		{
			failures++;
			if (failureLogs++ < 4)
			{
				Log.Warning("[RimMT] Unified Stage3 failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
			}
			return true;
		}
	}

	internal static string Summary()
	{
		return "Stage3 large-set rescue: observed=" + observed + ", largeEligible=" + largeEligible + ", boundedProofs=" + boundedProofs + ", boundedNullFallbacks=" + boundedNullFallbacks + ", unsafePriorityBypass=" + unsafePriorityBypass + ", failures=" + failures + ".";
	}

	private static bool IsSafeVanillaWorkValidator(Delegate d)
	{
		if ((object)d == null || d.Method == null || d.Method.DeclaringType == null)
		{
			return false;
		}
		try
		{
			if (d.Method.DeclaringType.Assembly != typeof(WorkGiver).Assembly)
			{
				return false;
			}
			string text = d.Method.DeclaringType.FullName ?? string.Empty;
			return text.IndexOf("WorkGiver", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("JobGiver", StringComparison.OrdinalIgnoreCase) >= 0 || ClosureContainsWorkContext(d.Target);
		}
		catch
		{
			return false;
		}
	}

	private static bool ClosureContainsWorkContext(object target)
	{
		if (target == null)
		{
			return false;
		}
		FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		for (int i = 0; i < fields.Length; i++)
		{
			Type fieldType = fields[i].FieldType;
			if (typeof(WorkGiver).IsAssignableFrom(fieldType) || fieldType == typeof(Pawn))
			{
				return true;
			}
			if ((fieldType.FullName ?? string.Empty).IndexOf("JobGiver", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsSafeWorkScannerPriority(Delegate d)
	{
		if ((object)d == null || d.Target == null)
		{
			return false;
		}
		FieldInfo[] fields = d.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		for (int i = 0; i < fields.Length; i++)
		{
			if (!typeof(WorkGiver_Scanner).IsAssignableFrom(fields[i].FieldType))
			{
				continue;
			}
			try
			{
				if (fields[i].GetValue(d.Target) is WorkGiver_Scanner)
				{
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	private static void EnsureWindow()
	{
		if (windowThings == null || windowThings.Length != 32)
		{
			windowThings = (Thing[])(object)new Thing[32];
			windowDistSq = new int[32];
			windowPriority = new float[32];
		}
	}

	private static void InsertCandidate(Thing thing, int distSq, float priority, bool prioritized, ref int count)
	{
		int num = Math.Min(count, 32);
		while (num > 0 && BetterThan(priority, distSq, windowPriority[num - 1], windowDistSq[num - 1], prioritized))
		{
			if (num < 32)
			{
				windowThings[num] = windowThings[num - 1];
				windowDistSq[num] = windowDistSq[num - 1];
				windowPriority[num] = windowPriority[num - 1];
			}
			num--;
		}
		if (num < 32)
		{
			windowThings[num] = thing;
			windowDistSq[num] = distSq;
			windowPriority[num] = priority;
			if (count < 32)
			{
				count++;
			}
		}
	}

	private static bool BetterThan(float p1, int d1, float p2, int d2, bool prioritized)
	{
		if (!prioritized)
		{
			return d1 < d2;
		}
		if (p1 != p2)
		{
			return p1 > p2;
		}
		return d1 < d2;
	}

	private static void ClearWindow(int count)
	{
		for (int i = 0; i < count; i++)
		{
			windowThings[i] = null;
		}
	}

	private static T GetArg<T>(ParameterInfo[] ps, object[] args, params string[] names)
	{
		for (int i = 0; i < names.Length; i++)
		{
			for (int j = 0; j < ps.Length && j < args.Length; j++)
			{
				if (string.Equals(ps[j].Name, names[i], StringComparison.OrdinalIgnoreCase) && args[j] is T)
				{
					return (T)args[j];
				}
			}
		}
		for (int k = 0; k < ps.Length && k < args.Length; k++)
		{
			if (args[k] is T)
			{
				return (T)args[k];
			}
		}
		return default(T);
	}

	private static IEnumerable<Thing> GetEnumerable(ParameterInfo[] ps, object[] args, string preferredName)
	{
		for (int i = 0; i < ps.Length && i < args.Length; i++)
		{
			if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase) && args[i] is IEnumerable<Thing>)
			{
				return (IEnumerable<Thing>)args[i];
			}
		}
		for (int j = 0; j < ps.Length && j < args.Length; j++)
		{
			if (args[j] is IEnumerable<Thing>)
			{
				return (IEnumerable<Thing>)args[j];
			}
		}
		return null;
	}

	private static T GetDelegateArg<T>(ParameterInfo[] ps, object[] args, string preferredName) where T : class
	{
		for (int i = 0; i < ps.Length && i < args.Length; i++)
		{
			if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase))
			{
				return args[i] as T;
			}
		}
		for (int j = 0; j < args.Length; j++)
		{
			if (args[j] is T)
			{
				return args[j] as T;
			}
		}
		return null;
	}

	private static bool GetBoolArg(ParameterInfo[] ps, object[] args, string name, bool fallback)
	{
		for (int i = 0; i < ps.Length && i < args.Length; i++)
		{
			if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is bool)
			{
				return (bool)args[i];
			}
		}
		return fallback;
	}

	private static float GetFloatArg(ParameterInfo[] ps, object[] args, string name, float fallback)
	{
		for (int i = 0; i < ps.Length && i < args.Length; i++)
		{
			if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is float)
			{
				return (float)args[i];
			}
		}
		return fallback;
	}
}
