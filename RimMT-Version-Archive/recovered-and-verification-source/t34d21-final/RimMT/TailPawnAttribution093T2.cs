using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT;

internal static class TailPawnAttribution093T2
{
	private struct DeepFrame
	{
		internal readonly long Frame;

		internal readonly int GameTick;

		internal readonly long TotalUs;

		internal readonly long PawnUs;

		internal readonly long JobTrackerUs;

		internal readonly long DetermineUs;

		internal readonly long OverrideUs;

		internal readonly long PatherUs;

		internal readonly Pawn Pawn1;

		internal readonly long Pawn1Us;

		internal readonly Pawn Pawn2;

		internal readonly long Pawn2Us;

		internal readonly Pawn Pawn3;

		internal readonly long Pawn3Us;

		internal DeepFrame(long frame, int gameTick, long totalUs, long pawnUs, long jobTrackerUs, long determineUs, long overrideUs, long patherUs, Pawn pawn1, long pawn1Us, Pawn pawn2, long pawn2Us, Pawn pawn3, long pawn3Us)
		{
			Frame = frame;
			GameTick = gameTick;
			TotalUs = totalUs;
			PawnUs = pawnUs;
			JobTrackerUs = jobTrackerUs;
			DetermineUs = determineUs;
			OverrideUs = overrideUs;
			PatherUs = patherUs;
			Pawn1 = pawn1;
			Pawn1Us = pawn1Us;
			Pawn2 = pawn2;
			Pawn2Us = pawn2Us;
			Pawn3 = pawn3;
			Pawn3Us = pawn3Us;
		}
	}

	private const int PhaseCount = 5;

	private const int RecentCapacity = 16;

	private const int BurstTicksAfterSevere = 4;

	private static readonly string[] PhaseNames = new string[5] { "PawnTick", "JobTrackerTick", "DetermineNextJob", "CheckForJobOverride", "PatherTick" };

	private static readonly long[] Calls = new long[5];

	private static readonly long[] TotalUs = new long[5];

	private static readonly long[] MaxUs = new long[5];

	private static readonly long[] Over5 = new long[5];

	private static readonly long[] Over10 = new long[5];

	private static readonly long[] Over20 = new long[5];

	private static readonly long[] CurrentUs = new long[5];

	private static readonly DeepFrame[] Recent = new DeepFrame[16];

	[ThreadStatic]
	private static bool deepActive;

	[ThreadStatic]
	private static Pawn topPawn1;

	[ThreadStatic]
	private static Pawn topPawn2;

	[ThreadStatic]
	private static Pawn topPawn3;

	[ThreadStatic]
	private static long topPawn1Us;

	[ThreadStatic]
	private static long topPawn2Us;

	[ThreadStatic]
	private static long topPawn3Us;

	private static int burstRemaining;

	private static long observedTicks;

	private static long deepTicks;

	private static long deepTail20;

	private static long deepTail50;

	private static int recentPos;

	private static int recentCount;

	internal static bool DeepActive => deepActive;

	internal static void BeginTick()
	{
		observedTicks++;
		int num = 0;
		try
		{
			if (Find.TickManager != null)
			{
				num = Find.TickManager.TicksGame;
			}
		}
		catch
		{
		}
		bool num2 = (num & 0x3F) == 0;
		bool flag = burstRemaining > 0;
		if (burstRemaining > 0)
		{
			burstRemaining--;
		}
		deepActive = num2 || flag;
		if (deepActive)
		{
			TailPathfinderAttribution093T3.BeginTick();
			deepTicks++;
			Array.Clear(CurrentUs, 0, CurrentUs.Length);
			topPawn1 = (topPawn2 = (topPawn3 = null));
			topPawn1Us = (topPawn2Us = (topPawn3Us = 0L));
		}
	}

	internal static long BeginPhase()
	{
		if (!deepActive || !RimMTThreadGuard.IsMainThread)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	internal static void EndPhase(long started, PawnTailPhase093T2 phase)
	{
		if (started == 0L || !deepActive)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num <= 0)
		{
			return;
		}
		long num2 = TicksToUs(num);
		if (phase == PawnTailPhase093T2.DetermineNextJob && num2 >= 20000)
		{
			WorkGiverDeepAttribution093T15.ObserveDetermine(num2);
		}
		if (phase >= PawnTailPhase093T2.PawnTick && phase < PawnTailPhase093T2.Count)
		{
			PawnTickAggregateAttribution093T13.RecordPhase(phase, num2);
			PlayerHumanResidualAttribution093T14.RecordPhase(phase, num2);
			Calls[(int)phase]++;
			TotalUs[(int)phase] += num2;
			CurrentUs[(int)phase] += num2;
			if (num2 > MaxUs[(int)phase])
			{
				MaxUs[(int)phase] = num2;
			}
			if (num2 >= 5000)
			{
				Over5[(int)phase]++;
			}
			if (num2 >= 10000)
			{
				Over10[(int)phase]++;
			}
			if (num2 >= 20000)
			{
				Over20[(int)phase]++;
			}
		}
	}

	internal static void EndPawn(long started, Pawn pawn)
	{
		if (started == 0L || !deepActive)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num > 0)
		{
			long num2 = TicksToUs(num);
			PawnTickAggregateAttribution093T13.RecordPawn(pawn, num2);
			PlayerHumanResidualAttribution093T14.RecordPawn(pawn, num2);
			int num3 = 0;
			Calls[num3]++;
			TotalUs[num3] += num2;
			CurrentUs[num3] += num2;
			if (num2 > MaxUs[num3])
			{
				MaxUs[num3] = num2;
			}
			if (num2 >= 5000)
			{
				Over5[num3]++;
			}
			if (num2 >= 10000)
			{
				Over10[num3]++;
			}
			if (num2 >= 20000)
			{
				Over20[num3]++;
			}
			if (num2 > topPawn1Us)
			{
				topPawn3 = topPawn2;
				topPawn3Us = topPawn2Us;
				topPawn2 = topPawn1;
				topPawn2Us = topPawn1Us;
				topPawn1 = pawn;
				topPawn1Us = num2;
			}
			else if (num2 > topPawn2Us)
			{
				topPawn3 = topPawn2;
				topPawn3Us = topPawn2Us;
				topPawn2 = pawn;
				topPawn2Us = num2;
			}
			else if (num2 > topPawn3Us)
			{
				topPawn3 = pawn;
				topPawn3Us = num2;
			}
		}
	}

	internal static void EndTick(long totalUs)
	{
		TailPathfinderAttribution093T3.EndTick(totalUs);
		bool num = deepActive;
		deepActive = false;
		if (totalUs >= 50000 && burstRemaining < 4)
		{
			burstRemaining = 4;
		}
		if (!num || totalUs < 20000)
		{
			return;
		}
		deepTail20++;
		if (totalUs >= 50000)
		{
			deepTail50++;
		}
		long pawnUs = CurrentUs[0];
		long jobTrackerUs = CurrentUs[1];
		long determineUs = CurrentUs[2];
		long overrideUs = CurrentUs[3];
		long patherUs = CurrentUs[4];
		int gameTick = -1;
		try
		{
			if (Find.TickManager != null)
			{
				gameTick = Find.TickManager.TicksGame;
			}
		}
		catch
		{
		}
		Recent[recentPos] = new DeepFrame(RimMTRuntime.MainThreadFrames, gameTick, totalUs, pawnUs, jobTrackerUs, determineUs, overrideUs, patherUs, topPawn1, topPawn1Us, topPawn2, topPawn2Us, topPawn3, topPawn3Us);
		recentPos = (recentPos + 1) % 16;
		if (recentCount < 16)
		{
			recentCount++;
		}
	}

	internal static string Summary()
	{
		StringBuilder stringBuilder = new StringBuilder(2048);
		stringBuilder.Append("T2 pawn-tail attribution: observedTicks=").Append(observedTicks).Append(", deepTicks=")
			.Append(deepTicks)
			.Append(", deepRate=")
			.Append((observedTicks == 0L) ? "0.00" : ((double)deepTicks * 100.0 / (double)observedTicks).ToString("F2"))
			.Append('%')
			.Append(", deepTail20=")
			.Append(deepTail20)
			.Append(", deepTail50=")
			.Append(deepTail50)
			.Append(". Phase stats: ");
		for (int i = 0; i < 5; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			long num = Calls[i];
			double num2 = ((num == 0L) ? 0.0 : ((double)TotalUs[i] / (double)num));
			stringBuilder.Append(PhaseNames[i]).Append("(calls=").Append(num)
				.Append(",avgUs=")
				.Append(num2.ToString("F1"))
				.Append(",>5/10/20=")
				.Append(Over5[i])
				.Append('/')
				.Append(Over10[i])
				.Append('/')
				.Append(Over20[i])
				.Append(",maxMs=")
				.Append(((double)MaxUs[i] / 1000.0).ToString("F2"))
				.Append(')');
		}
		return stringBuilder.ToString();
	}

	internal static string RecentSummary()
	{
		if (recentCount <= 0)
		{
			return "T2 recent deep >=20ms tails: none.";
		}
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T2 recent deep >=20ms tails (oldest->newest): ");
		int num = ((recentCount == 16) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			DeepFrame deepFrame = Recent[(num + i) % 16];
			stringBuilder.Append("frame=").Append(deepFrame.Frame).Append(",tick=")
				.Append(deepFrame.GameTick)
				.Append(",total=")
				.Append(((double)deepFrame.TotalUs / 1000.0).ToString("F2"))
				.Append("ms")
				.Append(",pawn=")
				.Append(((double)deepFrame.PawnUs / 1000.0).ToString("F2"))
				.Append(",jobTracker=")
				.Append(((double)deepFrame.JobTrackerUs / 1000.0).ToString("F2"))
				.Append(",determine=")
				.Append(((double)deepFrame.DetermineUs / 1000.0).ToString("F2"))
				.Append(",override=")
				.Append(((double)deepFrame.OverrideUs / 1000.0).ToString("F2"))
				.Append(",pather=")
				.Append(((double)deepFrame.PatherUs / 1000.0).ToString("F2"))
				.Append(",topPawns=")
				.Append(PawnText(deepFrame.Pawn1, deepFrame.Pawn1Us))
				.Append('/')
				.Append(PawnText(deepFrame.Pawn2, deepFrame.Pawn2Us))
				.Append('/')
				.Append(PawnText(deepFrame.Pawn3, deepFrame.Pawn3Us));
		}
		return stringBuilder.ToString();
	}

	private static string PawnText(Pawn pawn, long us)
	{
		if (pawn == null || us <= 0)
		{
			return "None:0";
		}
		string text = ((((Thing)pawn).def == null) ? "Pawn" : ((Def)((Thing)pawn).def).defName);
		string text2 = "none";
		try
		{
			if (pawn.CurJobDef != null)
			{
				text2 = ((Def)pawn.CurJobDef).defName;
			}
		}
		catch
		{
		}
		return text + "#" + ((Thing)pawn).thingIDNumber + "[" + text2 + "]:" + ((double)us / 1000.0).ToString("F2");
	}

	private static long TicksToUs(long ticks)
	{
		if (ticks > 0)
		{
			return (long)((double)ticks * (1000000.0 / (double)Stopwatch.Frequency));
		}
		return 0L;
	}
}
