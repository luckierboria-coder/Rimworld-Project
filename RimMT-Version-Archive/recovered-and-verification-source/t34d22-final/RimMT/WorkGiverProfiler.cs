using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;

namespace RimMT;

internal static class WorkGiverProfiler
{
	internal struct JobPackageScope
	{
		internal long Started;

		internal bool Entered;

		internal bool Outermost;
	}

	private struct ProfileKey : IEquatable<ProfileKey>
	{
		internal readonly WorkGiverDef Def;

		internal readonly Type WorkerType;

		internal readonly string Phase;

		internal string DefName
		{
			get
			{
				if (Def != null)
				{
					return ((Def)Def).defName;
				}
				return "<no-def>";
			}
		}

		internal string WorkerTypeName
		{
			get
			{
				if (!(WorkerType == null))
				{
					return WorkerType.FullName;
				}
				return "<null>";
			}
		}

		internal ProfileKey(WorkGiverDef def, Type workerType, string phase)
		{
			Def = def;
			WorkerType = workerType;
			Phase = phase ?? "?";
		}

		public bool Equals(ProfileKey other)
		{
			if (Def == other.Def && WorkerType == other.WorkerType)
			{
				return Phase == other.Phase;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ProfileKey)
			{
				return Equals((ProfileKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (((((Def != null) ? ((object)Def).GetHashCode() : 0) * 397) ^ ((!(WorkerType == null)) ? WorkerType.GetHashCode() : 0)) * 397) ^ Phase.GetHashCode();
		}
	}

	private sealed class Stat
	{
		internal long Count;

		internal long TotalTicks;

		internal long MaxTicks;

		internal long Over16Ms;

		internal long Over64Ms;

		internal long Over128Ms;
	}

	private struct Entry
	{
		internal readonly ProfileKey Key;

		internal readonly Stat Stat;

		internal Entry(ProfileKey key, Stat stat)
		{
			Key = key;
			Stat = stat;
		}
	}

	private struct PhaseEntry
	{
		internal readonly string Name;

		internal readonly long Ticks;

		internal PhaseEntry(string name, long ticks)
		{
			Name = name;
			Ticks = ticks;
		}
	}

	private sealed class SlowTrace
	{
		internal readonly long ElapsedTicks;

		internal readonly string Pawn;

		internal readonly List<PhaseEntry> Phases;

		internal SlowTrace(long elapsedTicks, string pawn, List<PhaseEntry> phases)
		{
			ElapsedTicks = elapsedTicks;
			Pawn = pawn;
			Phases = phases;
		}
	}

	private const int MaxSlowTraces = 16;

	private const int MaxPhasesPerSlowTrace = 10;

	private static readonly Dictionary<ProfileKey, Stat> Stats = new Dictionary<ProfileKey, Stat>();

	private static readonly List<SlowTrace> SlowTraces = new List<SlowTrace>();

	private static readonly long Threshold16Ticks = Math.Max(1L, Stopwatch.Frequency * 16 / 1000);

	private static readonly long Threshold20Ticks = Math.Max(1L, Stopwatch.Frequency * 20 / 1000);

	private static readonly long Threshold64Ticks = Math.Max(1L, Stopwatch.Frequency * 64 / 1000);

	private static readonly long Threshold128Ticks = Math.Max(1L, Stopwatch.Frequency * 128 / 1000);

	private static long totalSamples;

	private static long totalJobPackages;

	private static long slowJobPackages;

	private static int targetJobPackages;

	private static int patchedMethods;

	private static int patchFailures;

	private static bool sessionActive;

	[ThreadStatic]
	private static int jobPackageDepth;

	[ThreadStatic]
	private static bool captureDetail;

	[ThreadStatic]
	private static Dictionary<string, long> currentInclusivePhases;

	[ThreadStatic]
	private static string currentPawn;

	[ThreadStatic]
	private static List<string> callerStack;

	internal static bool DetailCaptureActive
	{
		get
		{
			if (captureDetail)
			{
				return sessionActive;
			}
			return false;
		}
	}

	internal static int PackagesRemaining
	{
		get
		{
			int num = targetJobPackages - (int)totalJobPackages;
			if (num >= 0)
			{
				return num;
			}
			return 0;
		}
	}

	internal static string CurrentCaller
	{
		get
		{
			if (callerStack == null || callerStack.Count == 0)
			{
				return "<package>";
			}
			return callerStack[callerStack.Count - 1];
		}
	}

	internal static void StartSession(int packageTarget, int patched, int failures)
	{
		Stats.Clear();
		SlowTraces.Clear();
		JobGiverInfrastructureProfiler.Reset();
		totalSamples = 0L;
		totalJobPackages = 0L;
		slowJobPackages = 0L;
		targetJobPackages = Math.Max(1, packageTarget);
		patchedMethods = patched;
		patchFailures = failures;
		jobPackageDepth = 0;
		captureDetail = false;
		currentInclusivePhases = null;
		currentPawn = null;
		callerStack = null;
		GenClosestDeepAttribution093T16.Reset();
		sessionActive = true;
	}

	internal static void StopSession()
	{
		sessionActive = false;
		captureDetail = false;
		jobPackageDepth = 0;
		currentInclusivePhases = null;
		currentPawn = null;
		callerStack = null;
	}

	internal static JobPackageScope BeginJobPackage(Pawn pawn)
	{
		JobPackageScope result = default(JobPackageScope);
		if (!sessionActive || !RimMTThreadGuard.IsMainThread)
		{
			return result;
		}
		result.Entered = true;
		result.Outermost = jobPackageDepth == 0;
		jobPackageDepth++;
		if (!result.Outermost)
		{
			return result;
		}
		result.Started = Stopwatch.GetTimestamp();
		totalJobPackages++;
		captureDetail = true;
		currentInclusivePhases = new Dictionary<string, long>(StringComparer.Ordinal);
		currentPawn = ((pawn == null) ? "<null>" : ((object)pawn).ToString());
		callerStack = new List<string>(8);
		GenClosestDeepAttribution093T16.BeginPackage(currentPawn);
		return result;
	}

	internal static void EndJobPackage(JobPackageScope state)
	{
		if (!state.Entered)
		{
			return;
		}
		if (state.Outermost && state.Started != 0L)
		{
			long num = Stopwatch.GetTimestamp() - state.Started;
			GenClosestDeepAttribution093T16.EndPackage(num);
			if (num >= Threshold20Ticks)
			{
				slowJobPackages++;
				SaveSlowTrace(num);
			}
		}
		if (jobPackageDepth > 0)
		{
			jobPackageDepth--;
		}
		if (jobPackageDepth == 0)
		{
			captureDetail = false;
			currentInclusivePhases = null;
			currentPawn = null;
			callerStack = null;
			if (sessionActive && totalJobPackages >= targetJobPackages)
			{
				WorkGiverDetailPatches.RequestStopCapture();
			}
		}
	}

	internal static void EnterCaller(WorkGiver giver, MethodBase method)
	{
		if (captureDetail && RimMTThreadGuard.IsMainThread)
		{
			if (callerStack == null)
			{
				callerStack = new List<string>(8);
			}
			string text = ((giver == null || giver.def == null) ? "<no-def>" : ((Def)giver.def).defName);
			string text2 = ((giver == null) ? "<null>" : ((object)giver).GetType().FullName);
			string text3 = ((method == null) ? "?" : method.Name);
			callerStack.Add(text + "/" + text2 + "." + text3);
		}
	}

	internal static void ExitCaller()
	{
		if (callerStack != null && callerStack.Count != 0)
		{
			callerStack.RemoveAt(callerStack.Count - 1);
		}
	}

	internal static long Begin()
	{
		if (!captureDetail || !RimMTThreadGuard.IsMainThread)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	internal static void Record(WorkGiver giver, MethodBase method, long started)
	{
		if (started != 0L && giver != null && !(method == null) && RimMTThreadGuard.IsMainThread)
		{
			long num = Stopwatch.GetTimestamp() - started;
			ProfileKey key = new ProfileKey(giver.def, ((object)giver).GetType(), method.Name);
			if (!Stats.TryGetValue(key, out var value))
			{
				value = new Stat();
				Stats.Add(key, value);
			}
			value.Count++;
			value.TotalTicks += num;
			if (num > value.MaxTicks)
			{
				value.MaxTicks = num;
			}
			if (num >= Threshold16Ticks)
			{
				value.Over16Ms++;
			}
			if (num >= Threshold64Ticks)
			{
				value.Over64Ms++;
			}
			if (num >= Threshold128Ticks)
			{
				value.Over128Ms++;
			}
			totalSamples++;
			RecordInclusivePhase(key.DefName + "/" + key.WorkerTypeName + "." + key.Phase, num);
		}
	}

	internal static void RecordInclusivePhase(string phase, long elapsedTicks)
	{
		if (captureDetail && currentInclusivePhases != null && !string.IsNullOrEmpty(phase) && elapsedTicks > 0)
		{
			currentInclusivePhases.TryGetValue(phase, out var value);
			currentInclusivePhases[phase] = value + elapsedTicks;
		}
	}

	internal static string Summary(int topN)
	{
		List<Entry> list = new List<Entry>(Stats.Count);
		foreach (KeyValuePair<ProfileKey, Stat> stat2 in Stats)
		{
			list.Add(new Entry(stat2.Key, stat2.Value));
		}
		list.Sort(delegate(Entry a, Entry b)
		{
			int num7 = b.Stat.TotalTicks.CompareTo(a.Stat.TotalTicks);
			return (num7 != 0) ? num7 : b.Stat.MaxTicks.CompareTo(a.Stat.MaxTicks);
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
		stringBuilder.Append("JobGiver detail V0.4.8: active=").Append(sessionActive).Append(", patchedMethods=")
			.Append(patchedMethods)
			.Append(", patchFailures=")
			.Append(patchFailures)
			.Append(", outerCalls=")
			.Append(totalJobPackages)
			.Append('/')
			.Append(targetJobPackages)
			.Append(", slowPackages>=20ms=")
			.Append(slowJobPackages)
			.Append(", phaseSamples=")
			.Append(totalSamples)
			.Append(", tracked=")
			.Append(list.Count)
			.Append(", slowTracesKept=")
			.Append(SlowTraces.Count);
		for (int num = 0; num < topN; num++)
		{
			Entry entry = list[num];
			Stat stat = entry.Stat;
			double num2 = (double)stat.TotalTicks * 1000.0 / (double)Stopwatch.Frequency;
			double num3 = ((stat.Count <= 0) ? 0.0 : (num2 / (double)stat.Count));
			double num4 = (double)stat.MaxTicks * 1000.0 / (double)Stopwatch.Frequency;
			stringBuilder.Append("\n  #").Append(num + 1).Append(' ')
				.Append(entry.Key.DefName)
				.Append(" / ")
				.Append(entry.Key.WorkerTypeName)
				.Append(" [")
				.Append(entry.Key.Phase)
				.Append("]")
				.Append(": calls=")
				.Append(stat.Count)
				.Append(", sampledTotalMs=")
				.Append(num2.ToString("F1"))
				.Append(", avgMs=")
				.Append(num3.ToString("F3"))
				.Append(", maxMs=")
				.Append(num4.ToString("F3"))
				.Append(", >=16ms=")
				.Append(stat.Over16Ms)
				.Append(", >=64ms=")
				.Append(stat.Over64Ms)
				.Append(", >=128ms=")
				.Append(stat.Over128Ms);
		}
		for (int num5 = 0; num5 < SlowTraces.Count; num5++)
		{
			SlowTrace slowTrace = SlowTraces[num5];
			stringBuilder.Append("\n  SLOW#").Append(num5 + 1).Append(": totalMs=")
				.Append(((double)slowTrace.ElapsedTicks * 1000.0 / (double)Stopwatch.Frequency).ToString("F3"))
				.Append(", pawn=")
				.Append(slowTrace.Pawn)
				.Append(", topInclusivePhases=");
			for (int num6 = 0; num6 < slowTrace.Phases.Count; num6++)
			{
				if (num6 > 0)
				{
					stringBuilder.Append(" | ");
				}
				PhaseEntry phaseEntry = slowTrace.Phases[num6];
				stringBuilder.Append(phaseEntry.Name).Append('=').Append(((double)phaseEntry.Ticks * 1000.0 / (double)Stopwatch.Frequency).ToString("F3"))
					.Append("ms");
			}
		}
		return stringBuilder.ToString();
	}

	private static void SaveSlowTrace(long elapsed)
	{
		List<PhaseEntry> list = new List<PhaseEntry>();
		if (currentInclusivePhases != null)
		{
			foreach (KeyValuePair<string, long> currentInclusivePhase in currentInclusivePhases)
			{
				list.Add(new PhaseEntry(currentInclusivePhase.Key, currentInclusivePhase.Value));
			}
			list.Sort((PhaseEntry a, PhaseEntry b) => b.Ticks.CompareTo(a.Ticks));
			if (list.Count > 10)
			{
				list.RemoveRange(10, list.Count - 10);
			}
		}
		SlowTrace item = new SlowTrace(elapsed, currentPawn ?? "<unknown>", list);
		SlowTraces.Add(item);
		SlowTraces.Sort((SlowTrace a, SlowTrace b) => b.ElapsedTicks.CompareTo(a.ElapsedTicks));
		if (SlowTraces.Count > 16)
		{
			SlowTraces.RemoveRange(16, SlowTraces.Count - 16);
		}
	}
}
