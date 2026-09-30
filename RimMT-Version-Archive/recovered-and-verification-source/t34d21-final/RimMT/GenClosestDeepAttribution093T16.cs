using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Verse;

namespace RimMT;

internal static class GenClosestDeepAttribution093T16
{
	internal struct Scope
	{
		internal bool Active;

		internal long Started;

		internal string Phase;

		internal string Caller;

		internal string SourceType;

		internal int SourceCount;

		internal bool SourcePresent;

		internal bool MobileSource;

		internal bool DynamicSource;
	}

	private sealed class Stat
	{
		internal string Caller;

		internal string Phase;

		internal string SourceType;

		internal bool Mobile;

		internal bool Dynamic;

		internal long Calls;

		internal long TotalTicks;

		internal long MaxTicks;

		internal long KnownSourceCalls;

		internal long UnknownSourceCalls;

		internal long SourceCountTotal;

		internal int MaxSourceCount;
	}

	private sealed class PackageState
	{
		internal string Pawn;

		internal long InfraCalls;

		internal long InfraTicks;

		internal long GenClosestCalls;

		internal long ReachCalls;

		internal long RegionCalls;

		internal long SourceCalls;

		internal long KnownSourceCalls;

		internal long UnknownSourceCalls;

		internal long MobileSourceCalls;

		internal long DynamicSourceCalls;

		internal long SourceCountSum;

		internal int MaxSourceCount;
	}

	private sealed class SlowCall
	{
		internal long ElapsedTicks;

		internal string Caller;

		internal string Phase;

		internal string SourceType;

		internal int SourceCount;

		internal bool Mobile;

		internal bool Dynamic;
	}

	private sealed class PackageTrace
	{
		internal long ElapsedTicks;

		internal string Pawn;

		internal long InfraCalls;

		internal long InfraTicks;

		internal long GenClosestCalls;

		internal long ReachCalls;

		internal long RegionCalls;

		internal long SourceCalls;

		internal long KnownSourceCalls;

		internal long UnknownSourceCalls;

		internal long MobileSourceCalls;

		internal long DynamicSourceCalls;

		internal long SourceCountSum;

		internal int MaxSourceCount;
	}

	private const int MaxSlowCalls = 24;

	private const int MaxSlowPackages = 16;

	private static readonly long SlowCallTicks = Math.Max(1L, Stopwatch.Frequency * 8 / 1000);

	private static readonly long SlowPackageTicks = Math.Max(1L, Stopwatch.Frequency * 20 / 1000);

	private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);

	private static readonly List<SlowCall> SlowCalls = new List<SlowCall>();

	private static readonly List<PackageTrace> SlowPackages = new List<PackageTrace>();

	private static long totalCalls;

	private static long genClosestCalls;

	private static long reachabilityCalls;

	private static long regionTraverserCalls;

	private static long sourceObserved;

	private static long knownSourceCountCalls;

	private static long unknownSourceCountCalls;

	private static long mobileSourceCalls;

	private static long dynamicSourceCalls;

	private static long sourceCountTotal;

	private static int maxSourceCount;

	private static long slowCalls8;

	private static long slowCalls20;

	[ThreadStatic]
	private static PackageState currentPackage;

	internal static void Reset()
	{
		Stats.Clear();
		SlowCalls.Clear();
		SlowPackages.Clear();
		totalCalls = (genClosestCalls = (reachabilityCalls = (regionTraverserCalls = 0L)));
		sourceObserved = (knownSourceCountCalls = (unknownSourceCountCalls = 0L));
		mobileSourceCalls = (dynamicSourceCalls = (sourceCountTotal = 0L));
		maxSourceCount = 0;
		slowCalls8 = (slowCalls20 = 0L);
		currentPackage = null;
	}

	internal static void BeginPackage(string pawn)
	{
		if (RimMTThreadGuard.IsMainThread)
		{
			currentPackage = new PackageState
			{
				Pawn = (string.IsNullOrEmpty(pawn) ? "<unknown>" : pawn)
			};
		}
	}

	internal static void EndPackage(long elapsedTicks)
	{
		PackageState packageState = currentPackage;
		currentPackage = null;
		if (packageState != null && elapsedTicks >= SlowPackageTicks)
		{
			PackageTrace item = new PackageTrace
			{
				ElapsedTicks = elapsedTicks,
				Pawn = packageState.Pawn,
				InfraCalls = packageState.InfraCalls,
				GenClosestCalls = packageState.GenClosestCalls,
				ReachCalls = packageState.ReachCalls,
				RegionCalls = packageState.RegionCalls,
				SourceCalls = packageState.SourceCalls,
				KnownSourceCalls = packageState.KnownSourceCalls,
				UnknownSourceCalls = packageState.UnknownSourceCalls,
				MobileSourceCalls = packageState.MobileSourceCalls,
				DynamicSourceCalls = packageState.DynamicSourceCalls,
				SourceCountSum = packageState.SourceCountSum,
				MaxSourceCount = packageState.MaxSourceCount,
				InfraTicks = packageState.InfraTicks
			};
			SlowPackages.Add(item);
			SlowPackages.Sort((PackageTrace a, PackageTrace b) => b.ElapsedTicks.CompareTo(a.ElapsedTicks));
			if (SlowPackages.Count > 16)
			{
				SlowPackages.RemoveRange(16, SlowPackages.Count - 16);
			}
		}
	}

	internal static Scope Begin(MethodBase method, object[] args)
	{
		Scope scope = default(Scope);
		if (!WorkGiverProfiler.DetailCaptureActive || !RimMTThreadGuard.IsMainThread || method == null)
		{
			return scope;
		}
		scope.Active = true;
		scope.Started = Stopwatch.GetTimestamp();
		scope.Phase = Classify(method);
		scope.Caller = WorkGiverProfiler.CurrentCaller;
		scope.SourceCount = -1;
		DescribeSource(args, ref scope);
		return scope;
	}

	internal static void End(Scope scope)
	{
		if (!scope.Active || scope.Started == 0L || !RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - scope.Started;
		totalCalls++;
		if (scope.Phase.StartsWith("GenClosest.", StringComparison.Ordinal))
		{
			genClosestCalls++;
		}
		else if (scope.Phase.StartsWith("Reachability.", StringComparison.Ordinal))
		{
			reachabilityCalls++;
		}
		else if (scope.Phase.StartsWith("RegionTraverser.", StringComparison.Ordinal))
		{
			regionTraverserCalls++;
		}
		if (scope.SourcePresent)
		{
			sourceObserved++;
			if (scope.SourceCount >= 0)
			{
				knownSourceCountCalls++;
				sourceCountTotal += scope.SourceCount;
				if (scope.SourceCount > maxSourceCount)
				{
					maxSourceCount = scope.SourceCount;
				}
			}
			else
			{
				unknownSourceCountCalls++;
			}
			if (scope.MobileSource)
			{
				mobileSourceCalls++;
			}
			if (scope.DynamicSource)
			{
				dynamicSourceCalls++;
			}
		}
		string key = (scope.Caller ?? "<package>") + "|" + scope.Phase + "|" + (scope.SourcePresent ? scope.SourceType : "<no-source>") + "|" + (scope.MobileSource ? "mobile" : "static") + "|" + (scope.DynamicSource ? "dynamic" : "countable");
		if (!Stats.TryGetValue(key, out var value))
		{
			value = new Stat
			{
				Caller = (scope.Caller ?? "<package>"),
				Phase = scope.Phase,
				SourceType = (scope.SourcePresent ? scope.SourceType : "<no-source>"),
				Mobile = scope.MobileSource,
				Dynamic = scope.DynamicSource
			};
			Stats.Add(key, value);
		}
		value.Calls++;
		value.TotalTicks += num;
		if (num > value.MaxTicks)
		{
			value.MaxTicks = num;
		}
		if (scope.SourceCount >= 0)
		{
			value.KnownSourceCalls++;
			value.SourceCountTotal += scope.SourceCount;
			if (scope.SourceCount > value.MaxSourceCount)
			{
				value.MaxSourceCount = scope.SourceCount;
			}
		}
		else if (scope.SourcePresent)
		{
			value.UnknownSourceCalls++;
		}
		if (num >= SlowCallTicks)
		{
			slowCalls8++;
			SaveSlowCall(scope, num);
		}
		if (num >= SlowPackageTicks)
		{
			slowCalls20++;
		}
		PackageState packageState = currentPackage;
		if (packageState == null)
		{
			return;
		}
		packageState.InfraCalls++;
		packageState.InfraTicks += num;
		if (scope.Phase.StartsWith("GenClosest.", StringComparison.Ordinal))
		{
			packageState.GenClosestCalls++;
		}
		else if (scope.Phase.StartsWith("Reachability.", StringComparison.Ordinal))
		{
			packageState.ReachCalls++;
		}
		else if (scope.Phase.StartsWith("RegionTraverser.", StringComparison.Ordinal))
		{
			packageState.RegionCalls++;
		}
		if (!scope.SourcePresent)
		{
			return;
		}
		packageState.SourceCalls++;
		if (scope.SourceCount >= 0)
		{
			packageState.KnownSourceCalls++;
			packageState.SourceCountSum += scope.SourceCount;
			if (scope.SourceCount > packageState.MaxSourceCount)
			{
				packageState.MaxSourceCount = scope.SourceCount;
			}
		}
		else
		{
			packageState.UnknownSourceCalls++;
		}
		if (scope.MobileSource)
		{
			packageState.MobileSourceCalls++;
		}
		if (scope.DynamicSource)
		{
			packageState.DynamicSourceCalls++;
		}
	}

	internal static string Summary(int topN)
	{
		List<Stat> list = new List<Stat>(Stats.Values);
		list.Sort(delegate(Stat a, Stat b)
		{
			int num11 = b.TotalTicks.CompareTo(a.TotalTicks);
			return (num11 != 0) ? num11 : b.MaxTicks.CompareTo(a.MaxTicks);
		});
		if (topN < 1)
		{
			topN = 1;
		}
		if (topN > list.Count)
		{
			topN = list.Count;
		}
		double num = ((knownSourceCountCalls == 0L) ? 0.0 : ((double)sourceCountTotal / (double)knownSourceCountCalls));
		StringBuilder stringBuilder = new StringBuilder(12288);
		stringBuilder.Append("T16 GenClosest/Region deep attribution: calls=").Append(totalCalls).Append(", genClosest=")
			.Append(genClosestCalls)
			.Append(", reachability=")
			.Append(reachabilityCalls)
			.Append(", regionTraverser=")
			.Append(regionTraverserCalls)
			.Append(", sourceObserved=")
			.Append(sourceObserved)
			.Append(", sourceCountKnown/unknown=")
			.Append(knownSourceCountCalls)
			.Append('/')
			.Append(unknownSourceCountCalls)
			.Append(", mobileSourceCalls=")
			.Append(mobileSourceCalls)
			.Append(", dynamicSourceCalls=")
			.Append(dynamicSourceCalls)
			.Append(", avgKnownSourceCount=")
			.Append(num.ToString("F1"))
			.Append(", maxSourceCount=")
			.Append(maxSourceCount)
			.Append(", infra>=8/20ms=")
			.Append(slowCalls8)
			.Append('/')
			.Append(slowCalls20)
			.Append(". Source counts are read only from ICollection; unknown/dynamic enumerables are never consumed; inclusive nested calls can overlap.");
		for (int num2 = 0; num2 < topN; num2++)
		{
			Stat stat = list[num2];
			double num3 = (double)stat.TotalTicks * 1000.0 / (double)Stopwatch.Frequency;
			double num4 = ((stat.Calls == 0L) ? 0.0 : (num3 / (double)stat.Calls));
			double num5 = (double)stat.MaxTicks * 1000.0 / (double)Stopwatch.Frequency;
			double num6 = ((stat.KnownSourceCalls == 0L) ? 0.0 : ((double)stat.SourceCountTotal / (double)stat.KnownSourceCalls));
			stringBuilder.Append("\n  #").Append(num2 + 1).Append(' ')
				.Append(stat.Caller)
				.Append(" -> ")
				.Append(stat.Phase)
				.Append(": calls=")
				.Append(stat.Calls)
				.Append(", totalMs=")
				.Append(num3.ToString("F1"))
				.Append(", avgMs=")
				.Append(num4.ToString("F3"))
				.Append(", maxMs=")
				.Append(num5.ToString("F3"))
				.Append(", source=")
				.Append(stat.SourceType)
				.Append(", mobile=")
				.Append(stat.Mobile)
				.Append(", dynamic=")
				.Append(stat.Dynamic)
				.Append(", countKnown/unknown=")
				.Append(stat.KnownSourceCalls)
				.Append('/')
				.Append(stat.UnknownSourceCalls)
				.Append(", avgCount=")
				.Append(num6.ToString("F1"))
				.Append(", maxCount=")
				.Append(stat.MaxSourceCount);
		}
		for (int num7 = 0; num7 < SlowCalls.Count; num7++)
		{
			SlowCall slowCall = SlowCalls[num7];
			stringBuilder.Append("\n  CALL#").Append(num7 + 1).Append(": ms=")
				.Append(((double)slowCall.ElapsedTicks * 1000.0 / (double)Stopwatch.Frequency).ToString("F3"))
				.Append(", caller=")
				.Append(slowCall.Caller)
				.Append(", phase=")
				.Append(slowCall.Phase)
				.Append(", source=")
				.Append(slowCall.SourceType)
				.Append(", count=")
				.Append((slowCall.SourceCount < 0) ? "unknown" : slowCall.SourceCount.ToString())
				.Append(", mobile=")
				.Append(slowCall.Mobile)
				.Append(", dynamic=")
				.Append(slowCall.Dynamic);
		}
		for (int num8 = 0; num8 < SlowPackages.Count; num8++)
		{
			PackageTrace packageTrace = SlowPackages[num8];
			double num9 = (double)packageTrace.InfraTicks * 1000.0 / (double)Stopwatch.Frequency;
			double num10 = ((packageTrace.KnownSourceCalls == 0L) ? 0.0 : ((double)packageTrace.SourceCountSum / (double)packageTrace.KnownSourceCalls));
			stringBuilder.Append("\n  PKG#").Append(num8 + 1).Append(": totalMs=")
				.Append(((double)packageTrace.ElapsedTicks * 1000.0 / (double)Stopwatch.Frequency).ToString("F3"))
				.Append(", pawn=")
				.Append(packageTrace.Pawn)
				.Append(", infraCalls=")
				.Append(packageTrace.InfraCalls)
				.Append(", infraInclusiveMs=")
				.Append(num9.ToString("F3"))
				.Append(", gen/reach/region=")
				.Append(packageTrace.GenClosestCalls)
				.Append('/')
				.Append(packageTrace.ReachCalls)
				.Append('/')
				.Append(packageTrace.RegionCalls)
				.Append(", sources=")
				.Append(packageTrace.SourceCalls)
				.Append(", known/unknown=")
				.Append(packageTrace.KnownSourceCalls)
				.Append('/')
				.Append(packageTrace.UnknownSourceCalls)
				.Append(", mobile/dynamic=")
				.Append(packageTrace.MobileSourceCalls)
				.Append('/')
				.Append(packageTrace.DynamicSourceCalls)
				.Append(", avgKnownCount=")
				.Append(num10.ToString("F1"))
				.Append(", maxCount=")
				.Append(packageTrace.MaxSourceCount);
		}
		return stringBuilder.ToString();
	}

	private static void DescribeSource(object[] args, ref Scope scope)
	{
		if (args == null)
		{
			return;
		}
		foreach (object obj in args)
		{
			if (obj == null)
			{
				continue;
			}
			Type type = obj.GetType();
			Type type2 = ThingElementType(type);
			if (type2 == null)
			{
				continue;
			}
			scope.SourcePresent = true;
			scope.SourceType = type.FullName ?? type.Name;
			ICollection collection = obj as ICollection;
			scope.SourceCount = collection?.Count ?? (-1);
			scope.DynamicSource = collection == null;
			scope.MobileSource = typeof(Pawn).IsAssignableFrom(type2);
			if (scope.MobileSource || scope.SourceCount <= 0 || !(obj is IList<Thing> list))
			{
				break;
			}
			int num = Math.Min(8, list.Count);
			for (int j = 0; j < num; j++)
			{
				if (list[j] is Pawn)
				{
					scope.MobileSource = true;
					break;
				}
			}
			break;
		}
	}

	private static Type ThingElementType(Type type)
	{
		if (type == null)
		{
			return null;
		}
		if (type.IsArray)
		{
			Type elementType = type.GetElementType();
			if (!(elementType != null) || !typeof(Thing).IsAssignableFrom(elementType))
			{
				return null;
			}
			return elementType;
		}
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
		{
			Type type2 = type.GetGenericArguments()[0];
			if (typeof(Thing).IsAssignableFrom(type2))
			{
				return type2;
			}
		}
		Type[] interfaces;
		try
		{
			interfaces = type.GetInterfaces();
		}
		catch
		{
			return null;
		}
		foreach (Type type3 in interfaces)
		{
			if (type3.IsGenericType && !(type3.GetGenericTypeDefinition() != typeof(IEnumerable<>)))
			{
				Type type4 = type3.GetGenericArguments()[0];
				if (typeof(Thing).IsAssignableFrom(type4))
				{
					return type4;
				}
			}
		}
		return null;
	}

	private static string Classify(MethodBase method)
	{
		Type declaringType = method.DeclaringType;
		string text = ((declaringType == null) ? "<unknown>" : declaringType.FullName);
		switch (text)
		{
		case "Verse.GenClosest":
			return "GenClosest." + method.Name;
		case "Verse.Reachability":
			return "Reachability." + method.Name;
		case "Verse.RegionTraverser":
			return "RegionTraverser." + method.Name;
		default:
			if (text.IndexOf("WorkGiver", StringComparison.Ordinal) >= 0 && (method.Name == "get_PotentialWorkThingsGlobal" || method.Name == "get_PotentialWorkCellsGlobal"))
			{
				return "ScannerSource." + text + "." + method.Name;
			}
			break;
		case null:
			break;
		}
		return text + "." + method.Name;
	}

	private static void SaveSlowCall(Scope scope, long elapsed)
	{
		SlowCalls.Add(new SlowCall
		{
			ElapsedTicks = elapsed,
			Caller = (scope.Caller ?? "<package>"),
			Phase = scope.Phase,
			SourceType = (scope.SourcePresent ? scope.SourceType : "<no-source>"),
			SourceCount = (scope.SourcePresent ? scope.SourceCount : (-1)),
			Mobile = scope.MobileSource,
			Dynamic = scope.DynamicSource
		});
		SlowCalls.Sort((SlowCall a, SlowCall b) => b.ElapsedTicks.CompareTo(a.ElapsedTicks));
		if (SlowCalls.Count > 24)
		{
			SlowCalls.RemoveRange(24, SlowCalls.Count - 24);
		}
	}
}
