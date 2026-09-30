using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT;

internal static class TailAttribution093T1
{
	private struct TailFrame
	{
		internal readonly long Frame;

		internal readonly int GameTick;

		internal readonly long TotalUs;

		internal readonly long PhaseSumUs;

		internal readonly long UnattributedUs;

		internal readonly int Top1;

		internal readonly long Top1Us;

		internal readonly int Top2;

		internal readonly long Top2Us;

		internal readonly int Top3;

		internal readonly long Top3Us;

		internal readonly int Gc0;

		internal readonly int Gc1;

		internal readonly int Gc2;

		internal TailFrame(long frame, int gameTick, long totalUs, long phaseSumUs, long unattributedUs, int top1, long top1Us, int top2, long top2Us, int top3, long top3Us, int gc0, int gc1, int gc2)
		{
			Frame = frame;
			GameTick = gameTick;
			TotalUs = totalUs;
			PhaseSumUs = phaseSumUs;
			UnattributedUs = unattributedUs;
			Top1 = top1;
			Top1Us = top1Us;
			Top2 = top2;
			Top2Us = top2Us;
			Top3 = top3;
			Top3Us = top3Us;
			Gc0 = gc0;
			Gc1 = gc1;
			Gc2 = gc2;
		}
	}

	private const int PhaseCount = 19;

	private const int RecentCapacity = 16;

	private static readonly string[] PhaseNames = new string[19]
	{
		"TickListNormal", "TickListRare", "TickListLong", "TickListExtra", "MapPreTick", "WorldTick", "StoryWatcher", "GameEnd", "Storyteller", "Tales",
		"WorldPostTick", "MapPostTick", "History", "GameComponents", "Autosaver", "Scenario", "DateNotifier", "Letters", "Filth"
	};

	private static readonly long[] Calls = new long[19];

	private static readonly long[] TotalUs = new long[19];

	private static readonly long[] MaxUs = new long[19];

	private static readonly long[] Over5 = new long[19];

	private static readonly long[] Over10 = new long[19];

	private static readonly long[] Over20 = new long[19];

	private static readonly long[] Over50 = new long[19];

	private static readonly long[] CurrentPhaseUs = new long[19];

	private static readonly TailFrame[] RecentSevere = new TailFrame[16];

	[ThreadStatic]
	private static bool activeTick;

	[ThreadStatic]
	private static int tickListOrdinal;

	[ThreadStatic]
	private static int gc0Start;

	[ThreadStatic]
	private static int gc1Start;

	[ThreadStatic]
	private static int gc2Start;

	private static int recentPos;

	private static int recentCount;

	private static long tail20;

	private static long tail50;

	private static long tail50WithGc0;

	private static long tail50WithGc1;

	private static long tail50WithGc2;

	private static long maxUnattributedUs;

	internal static void BeginTick()
	{
		activeTick = true;
		tickListOrdinal = 0;
		Array.Clear(CurrentPhaseUs, 0, CurrentPhaseUs.Length);
		gc0Start = GC.CollectionCount(0);
		gc1Start = GC.CollectionCount(1);
		gc2Start = GC.CollectionCount(2);
	}

	internal static long BeginPhase()
	{
		if (!activeTick || !RimMTThreadGuard.IsMainThread)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	internal static void EndPhase(long started, TailPhase093T1 phase)
	{
		if (started == 0L || !activeTick)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num <= 0)
		{
			return;
		}
		long num2 = TicksToUs(num);
		if (phase >= TailPhase093T1.TickListNormal && phase < TailPhase093T1.Count)
		{
			if (phase == TailPhase093T1.Storyteller && num2 >= 100000)
			{
				StorytellerCatastrophic093T15.Observe(num2);
			}
			Calls[(int)phase]++;
			TotalUs[(int)phase] += num2;
			CurrentPhaseUs[(int)phase] += num2;
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
			if (num2 >= 50000)
			{
				Over50[(int)phase]++;
			}
		}
	}

	internal static void EndTickList(long started)
	{
		EndPhase(started, tickListOrdinal++ switch
		{
			2 => TailPhase093T1.TickListLong, 
			1 => TailPhase093T1.TickListRare, 
			0 => TailPhase093T1.TickListNormal, 
			_ => TailPhase093T1.TickListExtra, 
		});
	}

	internal static void EndTick(long totalUs)
	{
		if (!activeTick)
		{
			return;
		}
		int num = Math.Max(0, GC.CollectionCount(0) - gc0Start);
		int num2 = Math.Max(0, GC.CollectionCount(1) - gc1Start);
		int num3 = Math.Max(0, GC.CollectionCount(2) - gc2Start);
		activeTick = false;
		if (totalUs >= 20000)
		{
			tail20++;
		}
		if (totalUs < 50000)
		{
			return;
		}
		tail50++;
		if (num > 0)
		{
			tail50WithGc0++;
		}
		if (num2 > 0)
		{
			tail50WithGc1++;
		}
		if (num3 > 0)
		{
			tail50WithGc2++;
		}
		long num4 = 0L;
		int num5 = -1;
		int num6 = -1;
		int top = -1;
		long num7 = 0L;
		long num8 = 0L;
		long num9 = 0L;
		for (int i = 0; i < 19; i++)
		{
			long num10 = CurrentPhaseUs[i];
			num4 += num10;
			if (num10 > num7)
			{
				top = num6;
				num9 = num8;
				num6 = num5;
				num8 = num7;
				num5 = i;
				num7 = num10;
			}
			else if (num10 > num8)
			{
				top = num6;
				num9 = num8;
				num6 = i;
				num8 = num10;
			}
			else if (num10 > num9)
			{
				top = i;
				num9 = num10;
			}
		}
		long num11 = totalUs - num4;
		if (num11 < 0)
		{
			num11 = 0L;
		}
		if (num11 > maxUnattributedUs)
		{
			maxUnattributedUs = num11;
		}
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
		RecentSevere[recentPos] = new TailFrame(RimMTRuntime.MainThreadFrames, gameTick, totalUs, num4, num11, num5, num7, num6, num8, top, num9, num, num2, num3);
		recentPos = (recentPos + 1) % 16;
		if (recentCount < 16)
		{
			recentCount++;
		}
	}

	internal static string Summary()
	{
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T1 top-level attribution: tail20=").Append(tail20).Append(", tail50=")
			.Append(tail50)
			.Append(", tail50WithGC[0/1/2]=")
			.Append(tail50WithGc0)
			.Append('/')
			.Append(tail50WithGc1)
			.Append('/')
			.Append(tail50WithGc2)
			.Append(", maxUnattributedMs=")
			.Append(((double)maxUnattributedUs / 1000.0).ToString("F2"))
			.Append(". Phase stats: ");
		for (int i = 0; i < 19; i++)
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
				.Append(",>5/10/20/50=")
				.Append(Over5[i])
				.Append('/')
				.Append(Over10[i])
				.Append('/')
				.Append(Over20[i])
				.Append('/')
				.Append(Over50[i])
				.Append(",maxMs=")
				.Append(((double)MaxUs[i] / 1000.0).ToString("F2"))
				.Append(')');
		}
		return stringBuilder.ToString();
	}

	internal static string RecentSevereSummary()
	{
		if (recentCount <= 0)
		{
			return "T1 recent >=50ms tails: none.";
		}
		StringBuilder stringBuilder = new StringBuilder(3072);
		stringBuilder.Append("T1 recent >=50ms tails (oldest->newest): ");
		int num = ((recentCount == 16) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			TailFrame tailFrame = RecentSevere[(num + i) % 16];
			stringBuilder.Append("frame=").Append(tailFrame.Frame).Append(",tick=")
				.Append(tailFrame.GameTick)
				.Append(",total=")
				.Append(((double)tailFrame.TotalUs / 1000.0).ToString("F2"))
				.Append("ms")
				.Append(",covered=")
				.Append(((double)tailFrame.PhaseSumUs / 1000.0).ToString("F2"))
				.Append("ms")
				.Append(",other=")
				.Append(((double)tailFrame.UnattributedUs / 1000.0).ToString("F2"))
				.Append("ms")
				.Append(",top=")
				.Append(PhaseName(tailFrame.Top1))
				.Append(':')
				.Append(((double)tailFrame.Top1Us / 1000.0).ToString("F2"))
				.Append('/')
				.Append(PhaseName(tailFrame.Top2))
				.Append(':')
				.Append(((double)tailFrame.Top2Us / 1000.0).ToString("F2"))
				.Append('/')
				.Append(PhaseName(tailFrame.Top3))
				.Append(':')
				.Append(((double)tailFrame.Top3Us / 1000.0).ToString("F2"))
				.Append(",gc=")
				.Append(tailFrame.Gc0)
				.Append('/')
				.Append(tailFrame.Gc1)
				.Append('/')
				.Append(tailFrame.Gc2);
		}
		return stringBuilder.ToString();
	}

	private static string PhaseName(int index)
	{
		if (index < 0 || index >= PhaseNames.Length)
		{
			return "None";
		}
		return PhaseNames[index];
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
