using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace RimMT;

internal static class JobGiverInfrastructureProfiler
{
	private sealed class Stat
	{
		internal long Count;

		internal long TotalTicks;

		internal long MaxTicks;
	}

	private struct Entry
	{
		internal readonly string Name;

		internal readonly Stat Stat;

		internal Entry(string name, Stat stat)
		{
			Name = name;
			Stat = stat;
		}
	}

	private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);

	private static long totalSamples;

	internal static void Reset()
	{
		Stats.Clear();
		totalSamples = 0L;
	}

	internal static long Begin()
	{
		if (!WorkGiverProfiler.DetailCaptureActive || !RimMTThreadGuard.IsMainThread)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	internal static void Record(MethodBase method, long started)
	{
		if (started != 0L && !(method == null) && RimMTThreadGuard.IsMainThread)
		{
			long num = Stopwatch.GetTimestamp() - started;
			string text = Classify(method);
			if (!Stats.TryGetValue(text, out var value))
			{
				value = new Stat();
				Stats.Add(text, value);
			}
			value.Count++;
			value.TotalTicks += num;
			if (num > value.MaxTicks)
			{
				value.MaxTicks = num;
			}
			totalSamples++;
			WorkGiverProfiler.RecordInclusivePhase(text, num);
		}
	}

	internal static string Summary(int topN)
	{
		List<Entry> list = new List<Entry>(Stats.Count);
		foreach (KeyValuePair<string, Stat> stat in Stats)
		{
			list.Add(new Entry(stat.Key, stat.Value));
		}
		list.Sort(delegate(Entry a, Entry b)
		{
			int num5 = b.Stat.TotalTicks.CompareTo(a.Stat.TotalTicks);
			return (num5 != 0) ? num5 : b.Stat.MaxTicks.CompareTo(a.Stat.MaxTicks);
		});
		if (topN < 1)
		{
			topN = 1;
		}
		if (topN > list.Count)
		{
			topN = list.Count;
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append("JobGiver infrastructure V0.4.8: samples=").Append(totalSamples).Append(", tracked=")
			.Append(list.Count)
			.Append(" (inclusive timings; nested phases can overlap)");
		for (int num = 0; num < topN; num++)
		{
			Entry entry = list[num];
			double num2 = (double)entry.Stat.TotalTicks * 1000.0 / (double)Stopwatch.Frequency;
			double num3 = ((entry.Stat.Count == 0L) ? 0.0 : (num2 / (double)entry.Stat.Count));
			double num4 = (double)entry.Stat.MaxTicks * 1000.0 / (double)Stopwatch.Frequency;
			stringBuilder.Append("\n  #").Append(num + 1).Append(' ')
				.Append(entry.Name)
				.Append(": calls=")
				.Append(entry.Stat.Count)
				.Append(", sampledTotalMs=")
				.Append(num2.ToString("F1"))
				.Append(", avgMs=")
				.Append(num3.ToString("F3"))
				.Append(", maxMs=")
				.Append(num4.ToString("F3"));
		}
		return stringBuilder.ToString();
	}

	private static string Classify(MethodBase method)
	{
		Type declaringType = method.DeclaringType;
		string text = ((declaringType == null) ? "<unknown>" : declaringType.FullName);
		return text switch
		{
			"Verse.GenClosest" => "GenClosest." + method.Name, 
			"Verse.Reachability" => "Reachability." + method.Name, 
			"Verse.RegionTraverser" => "RegionTraverser." + method.Name, 
			"RimWorld.WorkGiver_Scanner" => "WorkGiver_Scanner." + method.Name, 
			_ => text + "." + method.Name, 
		};
	}
}
