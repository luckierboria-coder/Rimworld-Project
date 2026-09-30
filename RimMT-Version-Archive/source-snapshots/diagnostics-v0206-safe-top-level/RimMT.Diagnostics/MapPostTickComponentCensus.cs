using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT.Diagnostics;

internal static class MapPostTickComponentCensus
{
	private sealed class Stat
	{
		internal long Calls;

		internal long TotalUs;

		internal long MaxUs;

		internal long Over5;

		internal long Over20;

		internal long Over50;
	}

	private static readonly object Sync = new object();

	private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);

	private static int patchedComponents;

	private static int patchedForeignPostfixes;

	private static int failures;

	internal static void Apply(Harmony harmony)
	{
		if (harmony == null)
		{
			return;
		}
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		try
		{
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			for (int i = 0; i < assemblies.Length; i++)
			{
				Type[] types;
				try
				{
					types = assemblies[i].GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = ex.Types;
				}
				catch
				{
					continue;
				}
				if (types == null)
				{
					continue;
				}
				foreach (Type type in types)
				{
					if (!(type == null) && !type.IsAbstract && typeof(MapComponent).IsAssignableFrom(type))
					{
						MethodInfo method;
						try
						{
							method = type.GetMethod("MapComponentTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
						}
						catch
						{
							continue;
						}
						if (!(method == null) && !method.IsAbstract && !(method.DeclaringType != type) && hashSet.Add(method))
						{
							Patch(harmony, method);
							patchedComponents++;
						}
					}
				}
			}
			Type type2 = AccessTools.TypeByName("StaggeredRaids.Map_MapPostTick_Patch");
			MethodInfo methodInfo = ((type2 == null) ? null : AccessTools.Method(type2, "Postfix", (Type[])null, (Type[])null));
			if (methodInfo != null && hashSet.Add(methodInfo))
			{
				Patch(harmony, methodInfo);
				patchedForeignPostfixes++;
			}
		}
		catch (Exception ex2)
		{
			failures++;
			Log.Warning("[RimMT Diagnostics] MapPostTick component census partial install: " + ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	private static void Patch(Harmony harmony, MethodBase target)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Expected O, but got Unknown
		//IL_0045: Expected O, but got Unknown
		harmony.Patch(target, new HarmonyMethod(typeof(MapPostTickComponentCensus), "Prefix", (Type[])null)
		{
			priority = 800
		}, new HarmonyMethod(typeof(MapPostTickComponentCensus), "Postfix", (Type[])null)
		{
			priority = 0
		}, (HarmonyMethod)null, (HarmonyMethod)null);
	}

	public static void Prefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void Postfix(MethodBase __originalMethod, long __state)
	{
		if (__state == 0L || __originalMethod == null)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - __state;
		if (num <= 0)
		{
			return;
		}
		long num2 = (long)((double)num * (1000000.0 / (double)Stopwatch.Frequency));
		string key = ((__originalMethod.DeclaringType == null) ? "<unknown>" : __originalMethod.DeclaringType.FullName) + "." + __originalMethod.Name;
		lock (Sync)
		{
			if (!Stats.TryGetValue(key, out var value))
			{
				value = new Stat();
				Stats.Add(key, value);
			}
			value.Calls++;
			value.TotalUs += num2;
			if (num2 > value.MaxUs)
			{
				value.MaxUs = num2;
			}
			if (num2 >= 5000)
			{
				value.Over5++;
			}
			if (num2 >= 20000)
			{
				value.Over20++;
			}
			if (num2 >= 50000)
			{
				value.Over50++;
			}
		}
	}

	internal static string Summary()
	{
		if (patchedComponents == 0 && patchedForeignPostfixes == 0)
		{
			return "MapPostTick component census: DISABLED. Patching every loaded MapComponent override at bootstrap can JIT mod methods before a game world exists; this triggered MoreFactionInteraction.FactionInteractionTimeSeperator and caused an initialization failure on every map tick. Aggregate Map.MapPostTick timing remains active.\n";
		}
		List<KeyValuePair<string, Stat>> list;
		lock (Sync)
		{
			list = (from kv in Stats
				orderby kv.Value.MaxUs descending, kv.Value.TotalUs descending
				select kv).Take(20).ToList();
		}
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("MapPostTick component census: patched[components/foreignPostfixes]=").Append(patchedComponents).Append('/')
			.Append(patchedForeignPostfixes)
			.Append(", observed=")
			.Append(Stats.Count)
			.Append(", failures=")
			.Append(failures)
			.AppendLine();
		if (list.Count == 0)
		{
			stringBuilder.AppendLine("MapPostTick component top: none");
			return stringBuilder.ToString();
		}
		stringBuilder.Append("MapPostTick component top: ");
		for (int num = 0; num < list.Count; num++)
		{
			if (num != 0)
			{
				stringBuilder.Append("; ");
			}
			Stat value = list[num].Value;
			stringBuilder.Append(list[num].Key).Append("[calls=").Append(value.Calls)
				.Append(",totalMs=")
				.Append(((double)value.TotalUs / 1000.0).ToString("F2"))
				.Append(",maxMs=")
				.Append(((double)value.MaxUs / 1000.0).ToString("F2"))
				.Append(",>5/20/50=")
				.Append(value.Over5)
				.Append('/')
				.Append(value.Over20)
				.Append('/')
				.Append(value.Over50)
				.Append(']');
		}
		stringBuilder.AppendLine();
		return stringBuilder.ToString();
	}
}
