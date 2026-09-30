using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics;

internal static class DiagnosticsHub
{
	private sealed class PhaseStats
	{
		internal long Calls;

		internal long TotalUs;

		internal long MaxUs;

		internal long Over5;

		internal long Over20;

		internal long Over50;

		internal void Reset()
		{
			Calls = (TotalUs = (MaxUs = (Over5 = (Over20 = (Over50 = 0L)))));
		}
	}

	private struct TailFrame
	{
		internal readonly int Tick;

		internal readonly long TotalUs;

		internal readonly Pawn TopPawn;

		internal readonly long TopPawnUs;

		internal TailFrame(int tick, long totalUs, Pawn topPawn, long topPawnUs)
		{
			Tick = tick;
			TotalUs = totalUs;
			TopPawn = topPawn;
			TopPawnUs = topPawnUs;
		}
	}

	private struct WaitState
	{
		internal int PawnId;

		internal string PawnDef;

		internal int IdleSinceTick;

		internal int LastTick;

		internal long ConsecutiveIdleResults;

		internal long NoJobWhileIdle;

		internal Outcome LastOutcome;

		internal string LastSource;
	}

	private enum Outcome
	{
		NoJob,
		Idle,
		NonIdle
	}

	private const int HistogramBuckets = 251;

	private const int RecentTailCapacity = 32;

	private const int MaxPawnStates = 512;

	private const int MaxSourceNames = 64;

	private static readonly long[] TickHistogram = new long[251];

	private static readonly PhaseStats[] Phases = CreatePhaseStats();

	private static readonly TailFrame[] RecentTails = new TailFrame[32];

	private static readonly Dictionary<int, WaitState> WaitStates = new Dictionary<int, WaitState>();

	private static readonly Dictionary<string, long> WaitSources = new Dictionary<string, long>(StringComparer.Ordinal);

	private static readonly FieldInfo JobTrackerPawnField = AccessToolsCompat.Field(typeof(Pawn_JobTracker), "pawn");

	[ThreadStatic]
	private static bool deepActive;

	[ThreadStatic]
	private static long tickStart;

	[ThreadStatic]
	private static int currentTick;

	[ThreadStatic]
	private static Pawn topPawn;

	[ThreadStatic]
	private static long topPawnUs;

	private static long ticks;

	private static long tickTotalUs;

	private static long tickMaxUs;

	private static long tickOver20;

	private static long tickOver50;

	private static long tickOver100;

	private static int burstTicksRemaining;

	private static int recentTailPos;

	private static int recentTailCount;

	private static long determineObserved;

	private static long playerHumanlikeDetermine;

	private static long currentIdleDetermine;

	private static long resultIdle;

	private static long resultNonIdle;

	private static long resultNoJob;

	private static long currentIdleResultIdle;

	private static long currentIdleResultNonIdle;

	private static long currentIdleResultNoJob;

	private static long waitStateEvictions;

	private static long failures;

	internal static bool DeepActive => deepActive;

	internal static void BeginTick()
	{
		tickStart = Stopwatch.GetTimestamp();
		try
		{
			currentTick = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
		}
		catch
		{
			currentTick = -1;
		}
		int sampleEveryTicks = RimMTDiagnosticsSettings.SampleEveryTicks;
		bool num = sampleEveryTicks <= 1 || (currentTick >= 0 && currentTick % sampleEveryTicks == 0);
		bool flag = burstTicksRemaining > 0;
		if (burstTicksRemaining > 0)
		{
			burstTicksRemaining--;
		}
		deepActive = num || flag;
		topPawn = null;
		topPawnUs = 0L;
	}

	internal static void EndTick()
	{
		long started = tickStart;
		tickStart = 0L;
		long num = ElapsedUs(started);
		if (num <= 0)
		{
			deepActive = false;
			return;
		}
		ticks++;
		tickTotalUs += num;
		if (num > tickMaxUs)
		{
			tickMaxUs = num;
		}
		if (num >= 20000)
		{
			tickOver20++;
		}
		if (num >= 50000)
		{
			tickOver50++;
		}
		if (num >= 100000)
		{
			tickOver100++;
		}
		int num2 = (int)(num / 1000);
		if (num2 >= 250)
		{
			num2 = 250;
		}
		if (num2 < 0)
		{
			num2 = 0;
		}
		TickHistogram[num2]++;
		int num3 = RimMTDiagnosticsSettings.TailThresholdMs * 1000;
		if (num >= num3)
		{
			if (num >= 50000 && burstTicksRemaining < RimMTDiagnosticsSettings.PostSpikeBurstTicks)
			{
				burstTicksRemaining = RimMTDiagnosticsSettings.PostSpikeBurstTicks;
			}
			RecentTails[recentTailPos] = new TailFrame(currentTick, num, topPawn, topPawnUs);
			recentTailPos = (recentTailPos + 1) % 32;
			if (recentTailCount < 32)
			{
				recentTailCount++;
			}
		}
		deepActive = false;
	}

	internal static long BeginPhase()
	{
		if (!deepActive)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	internal static void EndPhase(long started, DiagPhase phase)
	{
		if (started == 0L)
		{
			return;
		}
		long num = ElapsedUs(started);
		if (num > 0)
		{
			PhaseStats phaseStats = Phases[(int)phase];
			phaseStats.Calls++;
			phaseStats.TotalUs += num;
			if (num > phaseStats.MaxUs)
			{
				phaseStats.MaxUs = num;
			}
			if (num >= 5000)
			{
				phaseStats.Over5++;
			}
			if (num >= 20000)
			{
				phaseStats.Over20++;
			}
			if (num >= 50000)
			{
				phaseStats.Over50++;
			}
		}
	}

	internal static void EndPawn(long started, Pawn pawn)
	{
		if (started == 0L)
		{
			return;
		}
		long num = ElapsedUs(started);
		if (num > 0)
		{
			PhaseStats phaseStats = Phases[0];
			phaseStats.Calls++;
			phaseStats.TotalUs += num;
			if (num > phaseStats.MaxUs)
			{
				phaseStats.MaxUs = num;
			}
			if (num >= 5000)
			{
				phaseStats.Over5++;
			}
			if (num >= 20000)
			{
				phaseStats.Over20++;
			}
			if (num >= 50000)
			{
				phaseStats.Over50++;
			}
			if (num > topPawnUs)
			{
				topPawnUs = num;
				topPawn = pawn;
			}
		}
	}

	internal static void ObserveDetermine(Pawn_JobTracker tracker, ThinkResult result)
	{
		if (!RimMTDiagnosticsSettings.EnableWaitTrace)
		{
			return;
		}
		determineObserved++;
		if (tracker == null || JobTrackerPawnField == null)
		{
			return;
		}
		Pawn val;
		try
		{
			object value = JobTrackerPawnField.GetValue(tracker);
			val = (Pawn)((value is Pawn) ? value : null);
		}
		catch
		{
			failures++;
			return;
		}
		if (val == null || ((Thing)val).Destroyed || val.RaceProps == null || !val.RaceProps.Humanlike || ((Thing)val).Faction != Faction.OfPlayer)
		{
			return;
		}
		playerHumanlikeDetermine++;
		Job job = null;
		try
		{
			job = val.CurJob;
		}
		catch
		{
		}
		bool flag = IsIdle(job);
		if (flag)
		{
			currentIdleDetermine++;
		}
		Job val2 = null;
		ThinkNode val3 = null;
		try
		{
			val2 = ((ThinkResult)(ref result)).Job;
			val3 = ((ThinkResult)(ref result)).SourceNode;
		}
		catch
		{
		}
		string text = ((val3 == null) ? "<null>" : ((object)val3).GetType().FullName);
		if (string.IsNullOrEmpty(text))
		{
			text = "<unnamed>";
		}
		Outcome outcome;
		if (val2 == null)
		{
			resultNoJob++;
			if (flag)
			{
				currentIdleResultNoJob++;
			}
			outcome = Outcome.NoJob;
		}
		else if (IsIdle(val2))
		{
			resultIdle++;
			if (flag)
			{
				currentIdleResultIdle++;
			}
			outcome = Outcome.Idle;
			IncrementSource(text);
		}
		else
		{
			resultNonIdle++;
			if (flag)
			{
				currentIdleResultNonIdle++;
			}
			outcome = Outcome.NonIdle;
		}
		int tick = -1;
		try
		{
			tick = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
		}
		catch
		{
		}
		UpdateWaitState(val, flag, outcome, text, tick);
	}

	internal static string BuildSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(8192);
		long num = ticks;
		double num2 = ((num == 0L) ? 0.0 : ((double)tickTotalUs / 1000.0 / (double)num));
		stringBuilder.AppendLine("[RimMT Diagnostics v0.1]");
		stringBuilder.Append("TickTail: samples=").Append(num).Append(", avgMs=")
			.Append(num2.ToString("F3"))
			.Append(", P50/P95/P99/P99.9=")
			.Append(Percentile(0.5).ToString("F1"))
			.Append('/')
			.Append(Percentile(0.95).ToString("F1"))
			.Append('/')
			.Append(Percentile(0.99).ToString("F1"))
			.Append('/')
			.Append(Percentile(0.999).ToString("F1"))
			.Append(", >20/50/100=")
			.Append(tickOver20)
			.Append('/')
			.Append(tickOver50)
			.Append('/')
			.Append(tickOver100)
			.Append(", maxMs=")
			.Append(((double)tickMaxUs / 1000.0).ToString("F2"))
			.AppendLine();
		for (int i = 0; i < 9; i++)
		{
			PhaseStats phaseStats = Phases[i];
			StringBuilder stringBuilder2 = stringBuilder.Append("Phase ");
			DiagPhase diagPhase = (DiagPhase)i;
			stringBuilder2.Append(diagPhase.ToString()).Append(": calls=").Append(phaseStats.Calls)
				.Append(", avgUs=")
				.Append((phaseStats.Calls == 0L) ? "0.0" : ((double)phaseStats.TotalUs / (double)phaseStats.Calls).ToString("F1"))
				.Append(", >5/20/50ms=")
				.Append(phaseStats.Over5)
				.Append('/')
				.Append(phaseStats.Over20)
				.Append('/')
				.Append(phaseStats.Over50)
				.Append(", maxMs=")
				.Append(((double)phaseStats.MaxUs / 1000.0).ToString("F2"))
				.AppendLine();
		}
		stringBuilder.Append("WaitTrace: determineObserved=").Append(determineObserved).Append(", playerHumanlike=")
			.Append(playerHumanlikeDetermine)
			.Append(", currentIdleCalls=")
			.Append(currentIdleDetermine)
			.Append(", result[idle/nonIdle/noJob]=")
			.Append(resultIdle)
			.Append('/')
			.Append(resultNonIdle)
			.Append('/')
			.Append(resultNoJob)
			.Append(", currentIdle->result[idle/nonIdle/noJob]=")
			.Append(currentIdleResultIdle)
			.Append('/')
			.Append(currentIdleResultNonIdle)
			.Append('/')
			.Append(currentIdleResultNoJob)
			.Append(", stateEvictions=")
			.Append(waitStateEvictions)
			.Append(", failures=")
			.Append(failures)
			.AppendLine();
		stringBuilder.AppendLine("TopWaitSources=" + TopWaitSources());
		stringBuilder.AppendLine("LongestIdle=" + LongestIdle());
		stringBuilder.AppendLine("RecentTailTicks=" + RecentTailSummary());
		return stringBuilder.ToString();
	}

	internal static void Reset()
	{
		Array.Clear(TickHistogram, 0, TickHistogram.Length);
		for (int i = 0; i < Phases.Length; i++)
		{
			Phases[i].Reset();
		}
		Array.Clear(RecentTails, 0, RecentTails.Length);
		WaitStates.Clear();
		WaitSources.Clear();
		ticks = (tickTotalUs = (tickMaxUs = (tickOver20 = (tickOver50 = (tickOver100 = 0L)))));
		determineObserved = (playerHumanlikeDetermine = (currentIdleDetermine = 0L));
		resultIdle = (resultNonIdle = (resultNoJob = 0L));
		currentIdleResultIdle = (currentIdleResultNonIdle = (currentIdleResultNoJob = 0L));
		waitStateEvictions = (failures = 0L);
		recentTailPos = (recentTailCount = (burstTicksRemaining = 0));
	}

	private static void UpdateWaitState(Pawn pawn, bool currentIdle, Outcome outcome, string source, int tick)
	{
		int thingIDNumber = ((Thing)pawn).thingIDNumber;
		if (!WaitStates.TryGetValue(thingIDNumber, out var value))
		{
			if (WaitStates.Count >= 512)
			{
				int key = WaitStates.Keys.FirstOrDefault();
				WaitStates.Remove(key);
				waitStateEvictions++;
			}
			value = new WaitState
			{
				PawnId = thingIDNumber,
				PawnDef = ((((Thing)pawn).def == null) ? "Pawn" : ((Def)((Thing)pawn).def).defName),
				IdleSinceTick = (currentIdle ? tick : (-1))
			};
		}
		if (!currentIdle)
		{
			value.IdleSinceTick = -1;
			value.ConsecutiveIdleResults = 0L;
			value.NoJobWhileIdle = 0L;
		}
		else
		{
			if (value.IdleSinceTick < 0)
			{
				value.IdleSinceTick = tick;
			}
			if (outcome == Outcome.Idle)
			{
				value.ConsecutiveIdleResults++;
			}
			if (outcome == Outcome.NoJob)
			{
				value.NoJobWhileIdle++;
			}
		}
		value.LastTick = tick;
		value.LastOutcome = outcome;
		value.LastSource = source;
		WaitStates[thingIDNumber] = value;
	}

	private static bool IsIdle(Job job)
	{
		if (job == null || job.def == null)
		{
			return false;
		}
		string defName = ((Def)job.def).defName;
		if (string.IsNullOrEmpty(defName))
		{
			return false;
		}
		if (!(defName == "Wait") && !(defName == "Wait_MaintainPosture"))
		{
			return defName.StartsWith("Wait_", StringComparison.Ordinal);
		}
		return true;
	}

	private static void IncrementSource(string source)
	{
		if (WaitSources.TryGetValue(source, out var value))
		{
			WaitSources[source] = value + 1;
		}
		else if (WaitSources.Count < 64)
		{
			WaitSources[source] = 1L;
		}
		else
		{
			WaitSources["<other>"] = (WaitSources.ContainsKey("<other>") ? (WaitSources["<other>"] + 1) : 1);
		}
	}

	private static string TopWaitSources()
	{
		if (WaitSources.Count == 0)
		{
			return "none";
		}
		return string.Join("; ", from kv in WaitSources.OrderByDescending((KeyValuePair<string, long> kv) => kv.Value).Take(12)
			select kv.Key + "=" + kv.Value);
	}

	private static string LongestIdle()
	{
		int now = -1;
		try
		{
			now = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
		}
		catch
		{
		}
		IEnumerable<string> source = from s in (from s in WaitStates.Values
				where s.IdleSinceTick >= 0
				orderby (now >= 0 && s.IdleSinceTick >= 0) ? (now - s.IdleSinceTick) : 0 descending
				select s).Take(12)
			select s.PawnDef + "#" + s.PawnId + " durTicks=" + ((now >= 0) ? Math.Max(0, now - s.IdleSinceTick) : 0) + " idleResults=" + s.ConsecutiveIdleResults + " noJob=" + s.NoJobWhileIdle + " last=" + s.LastOutcome.ToString() + " source=" + (s.LastSource ?? "<none>");
		return string.Join("; ", source.ToArray());
	}

	private static string RecentTailSummary()
	{
		if (recentTailCount == 0)
		{
			return "none";
		}
		List<string> list = new List<string>();
		int num = ((recentTailCount == 32) ? recentTailPos : 0);
		for (int i = 0; i < recentTailCount; i++)
		{
			TailFrame tailFrame = RecentTails[(num + i) % 32];
			string text = ((tailFrame.TopPawn == null) ? "none" : (((((Thing)tailFrame.TopPawn).def == null) ? "Pawn" : ((Def)((Thing)tailFrame.TopPawn).def).defName) + "#" + ((Thing)tailFrame.TopPawn).thingIDNumber));
			list.Add("tick=" + tailFrame.Tick + ",total=" + ((double)tailFrame.TotalUs / 1000.0).ToString("F2") + "ms,topPawn=" + text + ":" + ((double)tailFrame.TopPawnUs / 1000.0).ToString("F2") + "ms");
		}
		return string.Join("; ", list.ToArray());
	}

	private static double Percentile(double p)
	{
		long num = ticks;
		if (num <= 0)
		{
			return 0.0;
		}
		long num2 = (long)Math.Ceiling((double)num * p);
		long num3 = 0L;
		for (int i = 0; i < TickHistogram.Length; i++)
		{
			num3 += TickHistogram[i];
			if (num3 >= num2)
			{
				return i;
			}
		}
		return TickHistogram.Length - 1;
	}

	private static long ElapsedUs(long started)
	{
		if (started == 0L)
		{
			return 0L;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num > 0)
		{
			return (long)((double)num * (1000000.0 / (double)Stopwatch.Frequency));
		}
		return 0L;
	}

	private static PhaseStats[] CreatePhaseStats()
	{
		PhaseStats[] array = new PhaseStats[9];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new PhaseStats();
		}
		return array;
	}
}
