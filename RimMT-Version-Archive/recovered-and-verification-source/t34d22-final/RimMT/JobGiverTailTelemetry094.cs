using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class JobGiverTailTelemetry094
{
	private sealed class TailStats
	{
		internal long Over2;

		internal long Over5;

		internal long Over10;

		internal long Over20;

		internal long Over50;

		internal long MaxTicks;
	}

	private const int MaxKeys = 32;

	private const int TopKeys = 8;

	private static readonly long T2 = Math.Max(1L, Stopwatch.Frequency * 2 / 1000);

	private static readonly long T5 = Math.Max(1L, Stopwatch.Frequency * 5 / 1000);

	private static readonly long T10 = Math.Max(1L, Stopwatch.Frequency * 10 / 1000);

	private static readonly long T20 = Math.Max(1L, Stopwatch.Frequency * 20 / 1000);

	private static readonly long T50 = Math.Max(1L, Stopwatch.Frequency * 50 / 1000);

	private static bool patched;

	private static long timedCalls;

	private static long over2;

	private static long over5;

	private static long over10;

	private static long over20;

	private static long over50;

	private static long unresolved;

	private static long maxTicks;

	private static readonly Dictionary<string, TailStats> Stats = new Dictionary<string, TailStats>();

	private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();

	internal static void Apply(Harmony harmony)
	{
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Expected O, but got Unknown
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			int num = 0;
			foreach (MethodInfo methodInfo in methods)
			{
				if (IsSupportedOverload(methodInfo))
				{
					HarmonyMethod val = new HarmonyMethod(typeof(JobGiverTailTelemetry094), "Prefix", (Type[])null)
					{
						priority = 925
					};
					HarmonyMethod val2 = new HarmonyMethod(typeof(JobGiverTailTelemetry094), "Postfix", (Type[])null)
					{
						priority = 0
					};
					harmony.Patch((MethodBase)methodInfo, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			patched = num > 0;
			Log.Message("[RimMT] V0.9.4 lightweight JobGiver tail buckets installed on " + num + " ClosestThingReachable overload(s); WorkGiver reflection is deferred until a call exceeds 2ms.");
		}
		catch (Exception ex)
		{
			patched = false;
			Log.Warning("[RimMT] V0.9.4 JobGiver tail buckets failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static bool IsSupportedOverload(MethodInfo method)
	{
		if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable")
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length >= 8 && parameters[0].ParameterType == typeof(IntVec3) && parameters[1].ParameterType == typeof(Map) && parameters[2].ParameterType == typeof(ThingRequest) && parameters[3].ParameterType == typeof(PathEndMode) && parameters[4].ParameterType == typeof(TraverseParms) && parameters[5].ParameterType == typeof(float) && parameters[6].ParameterType == typeof(Predicate<Thing>))
		{
			return typeof(IEnumerable<Thing>).IsAssignableFrom(parameters[7].ParameterType);
		}
		return false;
	}

	public static void Prefix(ref long __state)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Invalid comparison between Unknown and I4
		__state = 0L;
		if (JobGiverGlobalNearest04181.InJobGiverScope && RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			__state = Stopwatch.GetTimestamp();
		}
	}

	public static void Postfix(Predicate<Thing> __6, long __state)
	{
		if (__state == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - __state;
		timedCalls++;
		if (num > maxTicks)
		{
			maxTicks = num;
		}
		if (num < T2)
		{
			return;
		}
		over2++;
		if (num >= T5)
		{
			over5++;
		}
		if (num >= T10)
		{
			over10++;
		}
		if (num >= T20)
		{
			over20++;
		}
		if (num >= T50)
		{
			over50++;
		}
		WorkGiver_Scanner val = TryResolveScanner(__6);
		if (val == null)
		{
			unresolved++;
			return;
		}
		string key = ScannerKey(val);
		if (!Stats.TryGetValue(key, out var value))
		{
			if (Stats.Count >= 32)
			{
				return;
			}
			value = new TailStats();
			Stats[key] = value;
		}
		value.Over2++;
		if (num >= T5)
		{
			value.Over5++;
		}
		if (num >= T10)
		{
			value.Over10++;
		}
		if (num >= T20)
		{
			value.Over20++;
		}
		if (num >= T50)
		{
			value.Over50++;
		}
		if (num > value.MaxTicks)
		{
			value.MaxTicks = num;
		}
	}

	internal static bool IsRecurringHot(WorkGiver_Scanner scanner)
	{
		if (scanner == null || !RimMTThreadGuard.IsMainThread)
		{
			return false;
		}
		if (!Stats.TryGetValue(ScannerKey(scanner), out var value) || value == null)
		{
			return false;
		}
		if (value.Over20 <= 0)
		{
			return value.Over5 >= 2;
		}
		return true;
	}

	internal static WorkGiver_Scanner TryResolveScanner(Predicate<Thing> validator)
	{
		if (validator == null)
		{
			return null;
		}
		try
		{
			object target = validator.Target;
			if (target == null)
			{
				return null;
			}
			Type type = target.GetType();
			if (!ScannerFieldCache.TryGetValue(type, out var value))
			{
				value = ResolveScannerField(type);
				ScannerFieldCache[type] = value;
			}
			return (WorkGiver_Scanner)((value == null) ? null : /*isinst with value type is only supported in some contexts*/);
		}
		catch
		{
			return null;
		}
	}

	private static string ScannerKey(WorkGiver_Scanner scanner)
	{
		if (scanner == null)
		{
			return "<unknown>";
		}
		string text = ((((WorkGiver)scanner).def == null || string.IsNullOrEmpty(((Def)((WorkGiver)scanner).def).defName)) ? ((object)scanner).GetType().FullName : ((Def)((WorkGiver)scanner).def).defName);
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		return "<unknown>";
	}

	private static FieldInfo ResolveScannerField(Type targetType)
	{
		if (targetType == null)
		{
			return null;
		}
		FieldInfo[] fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		FieldInfo fieldInfo = null;
		foreach (FieldInfo fieldInfo2 in fields)
		{
			if (typeof(WorkGiver_Scanner).IsAssignableFrom(fieldInfo2.FieldType))
			{
				return fieldInfo2;
			}
			if (fieldInfo == null && typeof(WorkGiver).IsAssignableFrom(fieldInfo2.FieldType))
			{
				fieldInfo = fieldInfo2;
			}
		}
		return fieldInfo;
	}

	internal static string Summary()
	{
		List<KeyValuePair<string, TailStats>> list = new List<KeyValuePair<string, TailStats>>(Stats);
		list.Sort(delegate(KeyValuePair<string, TailStats> a, KeyValuePair<string, TailStats> b)
		{
			int num5 = b.Value.Over50.CompareTo(a.Value.Over50);
			if (num5 != 0)
			{
				return num5;
			}
			num5 = b.Value.Over20.CompareTo(a.Value.Over20);
			if (num5 != 0)
			{
				return num5;
			}
			num5 = b.Value.Over10.CompareTo(a.Value.Over10);
			if (num5 != 0)
			{
				return num5;
			}
			num5 = b.Value.Over5.CompareTo(a.Value.Over5);
			if (num5 != 0)
			{
				return num5;
			}
			num5 = b.Value.Over2.CompareTo(a.Value.Over2);
			return (num5 != 0) ? num5 : b.Value.MaxTicks.CompareTo(a.Value.MaxTicks);
		});
		List<string> list2 = new List<string>();
		int num = Math.Min(8, list.Count);
		for (int num2 = 0; num2 < num; num2++)
		{
			TailStats value = list[num2].Value;
			double num3 = (double)value.MaxTicks * 1000000.0 / (double)Stopwatch.Frequency;
			list2.Add(list[num2].Key + "(>2ms=" + value.Over2 + ", >5ms=" + value.Over5 + ", >10ms=" + value.Over10 + ", >20ms=" + value.Over20 + ", >50ms=" + value.Over50 + ", maxUs=" + num3.ToString("F1") + ")");
		}
		double num4 = (double)maxTicks * 1000000.0 / (double)Stopwatch.Frequency;
		return "JobGiver tail buckets V0.9.4: patched=" + patched + ", timedCalls=" + timedCalls + ", >2ms=" + over2 + ", >5ms=" + over5 + ", >10ms=" + over10 + ", >20ms=" + over20 + ", >50ms=" + over50 + ", unresolved=" + unresolved + ", maxUs=" + num4.ToString("F1") + ", top=" + ((list2.Count == 0) ? "<none>" : string.Join("; ", list2.ToArray()));
	}
}
