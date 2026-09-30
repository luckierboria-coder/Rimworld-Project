using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT;

internal static class TailPathfinderAttribution093T3
{
	private struct PathFrame
	{
		internal readonly long Frame;

		internal readonly int GameTick;

		internal readonly long TickUs;

		internal readonly int Calls;

		internal readonly long FindUs;

		internal readonly long MaxCallUs;

		internal PathFrame(long frame, int gameTick, long tickUs, int calls, long findUs, long maxCallUs)
		{
			Frame = frame;
			GameTick = gameTick;
			TickUs = tickUs;
			Calls = calls;
			FindUs = findUs;
			MaxCallUs = maxCallUs;
		}
	}

	private const int RecentCapacity = 16;

	private static readonly PathFrame[] Recent = new PathFrame[16];

	[ThreadStatic]
	private static bool active;

	[ThreadStatic]
	private static long currentUs;

	[ThreadStatic]
	private static long currentMaxCallUs;

	[ThreadStatic]
	private static int currentCalls;

	private static long calls;

	private static long totalUs;

	private static long maxUs;

	private static long over5;

	private static long over10;

	private static long over20;

	private static long over50;

	private static int recentPos;

	private static int recentCount;

	internal static void BeginTick()
	{
		active = true;
		currentUs = 0L;
		currentMaxCallUs = 0L;
		currentCalls = 0;
	}

	internal static long BeginCall()
	{
		if (!active || !TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	internal static void EndCall(long started)
	{
		if (started == 0L || !active)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num > 0)
		{
			long num2 = TicksToUs(num);
			calls++;
			totalUs += num2;
			currentUs += num2;
			currentCalls++;
			if (num2 > currentMaxCallUs)
			{
				currentMaxCallUs = num2;
			}
			if (num2 > maxUs)
			{
				maxUs = num2;
			}
			if (num2 >= 5000)
			{
				over5++;
			}
			if (num2 >= 10000)
			{
				over10++;
			}
			if (num2 >= 20000)
			{
				over20++;
			}
			if (num2 >= 50000)
			{
				over50++;
			}
		}
	}

	internal static void EndTick(long tickUs)
	{
		if (!active)
		{
			return;
		}
		active = false;
		if (tickUs < 20000 || currentCalls <= 0)
		{
			return;
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
		Recent[recentPos] = new PathFrame(RimMTRuntime.MainThreadFrames, gameTick, tickUs, currentCalls, currentUs, currentMaxCallUs);
		recentPos = (recentPos + 1) % 16;
		if (recentCount < 16)
		{
			recentCount++;
		}
	}

	internal static string Summary()
	{
		double num = ((calls == 0L) ? 0.0 : ((double)totalUs / (double)calls));
		return "T3 PathFinder deep attribution: calls=" + calls + ", avgUs=" + num.ToString("F1") + ", >5/10/20/50=" + over5 + "/" + over10 + "/" + over20 + "/" + over50 + ", maxMs=" + ((double)maxUs / 1000.0).ToString("F2") + ".";
	}

	internal static string RecentSummary()
	{
		if (recentCount <= 0)
		{
			return "T3 recent deep >=20ms ticks with FindPath: none.";
		}
		StringBuilder stringBuilder = new StringBuilder(2048);
		stringBuilder.Append("T3 recent deep >=20ms ticks with FindPath (oldest->newest): ");
		int num = ((recentCount == 16) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			PathFrame pathFrame = Recent[(num + i) % 16];
			stringBuilder.Append("frame=").Append(pathFrame.Frame).Append(",tick=")
				.Append(pathFrame.GameTick)
				.Append(",total=")
				.Append(((double)pathFrame.TickUs / 1000.0).ToString("F2"))
				.Append("ms")
				.Append(",findCalls=")
				.Append(pathFrame.Calls)
				.Append(",findTotal=")
				.Append(((double)pathFrame.FindUs / 1000.0).ToString("F2"))
				.Append("ms")
				.Append(",findMax=")
				.Append(((double)pathFrame.MaxCallUs / 1000.0).ToString("F2"))
				.Append("ms");
		}
		return stringBuilder.ToString();
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
