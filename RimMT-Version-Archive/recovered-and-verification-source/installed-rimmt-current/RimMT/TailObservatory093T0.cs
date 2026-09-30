using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT;

internal static class TailObservatory093T0
{
	private struct TailFrame
	{
		internal readonly long Frame;

		internal readonly int GameTick;

		internal readonly long DurationUs;

		internal readonly TailSignal093T0 Signals;

		internal readonly long ReachQueryMaxUs;

		internal readonly long ReachCaptureMaxUs;

		internal readonly long TopologyMaxUs;

		internal readonly int S4MaxRejects;

		internal TailFrame(long frame, int gameTick, long durationUs, TailSignal093T0 signals, long reachQueryMaxUs, long reachCaptureMaxUs, long topologyMaxUs, int s4MaxRejects)
		{
			Frame = frame;
			GameTick = gameTick;
			DurationUs = durationUs;
			Signals = signals;
			ReachQueryMaxUs = reachQueryMaxUs;
			ReachCaptureMaxUs = reachCaptureMaxUs;
			TopologyMaxUs = topologyMaxUs;
			S4MaxRejects = s4MaxRejects;
		}
	}

	private const int BucketUs = 250;

	private const int HistogramCeilingUs = 250000;

	private const int HistogramBuckets = 1002;

	private const int RecentCapacity = 16;

	private static readonly long FiveMsTicks = Math.Max(1L, Stopwatch.Frequency * 5 / 1000);

	private static readonly long TenMsTicks = Math.Max(1L, Stopwatch.Frequency * 10 / 1000);

	private static readonly long TwentyMsTicks = Math.Max(1L, Stopwatch.Frequency * 20 / 1000);

	private static readonly long[] Histogram = new long[1002];

	private static readonly TailFrame[] Recent = new TailFrame[16];

	private static long samples;

	private static long totalUs;

	private static long maxUs;

	private static long over20;

	private static long over30;

	private static long over50;

	private static long over100;

	private static int recentPos;

	private static int recentCount;

	private static long reachQueryOver5;

	private static long reachQueryOver10;

	private static long reachQueryOver20;

	private static long reachQueryMaxUs;

	private static long reachCaptureOver5;

	private static long reachCaptureOver10;

	private static long reachCaptureOver20;

	private static long reachCaptureMaxUs;

	private static long topologyOver5;

	private static long topologyOver10;

	private static long topologyOver20;

	private static long topologyMaxUs;

	private static long s4HeavyEvents;

	private static int s4MaxRejects;

	[ThreadStatic]
	private static TailSignal093T0 currentSignals;

	[ThreadStatic]
	private static long currentReachQueryMaxUs;

	[ThreadStatic]
	private static long currentReachCaptureMaxUs;

	[ThreadStatic]
	private static long currentTopologyMaxUs;

	[ThreadStatic]
	private static int currentS4MaxRejects;

	internal static void BeginTick()
	{
		TailAttribution093T1.BeginTick();
		TailPawnAttribution093T2.BeginTick();
		currentSignals = TailSignal093T0.None;
		currentReachQueryMaxUs = 0L;
		currentReachCaptureMaxUs = 0L;
		currentTopologyMaxUs = 0L;
		currentS4MaxRejects = 0;
	}

	internal static void RecordTick(long startTimestamp, long endTimestamp)
	{
		long num = endTimestamp - startTimestamp;
		if (num <= 0)
		{
			return;
		}
		long num2 = TicksToUs(num);
		TailAttribution093T1.EndTick(num2);
		TailPawnAttribution093T2.EndTick(num2);
		samples++;
		totalUs += num2;
		if (num2 > maxUs)
		{
			maxUs = num2;
		}
		int num3 = (int)(num2 / 250);
		if (num3 >= 1001)
		{
			num3 = 1001;
		}
		Histogram[num3]++;
		if (num2 >= 20000)
		{
			over20++;
		}
		if (num2 >= 30000)
		{
			over30++;
		}
		if (num2 >= 50000)
		{
			over50++;
		}
		if (num2 >= 100000)
		{
			over100++;
		}
		if (num2 < 20000)
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
		Recent[recentPos] = new TailFrame(RimMTRuntime.MainThreadFrames, gameTick, num2, currentSignals, currentReachQueryMaxUs, currentReachCaptureMaxUs, currentTopologyMaxUs, currentS4MaxRejects);
		recentPos = (recentPos + 1) % 16;
		if (recentCount < 16)
		{
			recentCount++;
		}
	}

	internal static void NoteReachQueryTicks(long elapsedTicks)
	{
		if (elapsedTicks >= FiveMsTicks)
		{
			long num = TicksToUs(elapsedTicks);
			currentSignals |= TailSignal093T0.ReachQuery;
			if (num > currentReachQueryMaxUs)
			{
				currentReachQueryMaxUs = num;
			}
			reachQueryOver5++;
			if (elapsedTicks >= TenMsTicks)
			{
				reachQueryOver10++;
			}
			if (elapsedTicks >= TwentyMsTicks)
			{
				reachQueryOver20++;
			}
			if (num > reachQueryMaxUs)
			{
				reachQueryMaxUs = num;
			}
		}
	}

	internal static void NoteReachCaptureTicks(long elapsedTicks)
	{
		if (elapsedTicks >= FiveMsTicks)
		{
			long num = TicksToUs(elapsedTicks);
			currentSignals |= TailSignal093T0.ReachCapture;
			if (num > currentReachCaptureMaxUs)
			{
				currentReachCaptureMaxUs = num;
			}
			reachCaptureOver5++;
			if (elapsedTicks >= TenMsTicks)
			{
				reachCaptureOver10++;
			}
			if (elapsedTicks >= TwentyMsTicks)
			{
				reachCaptureOver20++;
			}
			if (num > reachCaptureMaxUs)
			{
				reachCaptureMaxUs = num;
			}
		}
	}

	internal static void NoteTopologySliceTicks(long elapsedTicks)
	{
		if (elapsedTicks >= FiveMsTicks)
		{
			long num = TicksToUs(elapsedTicks);
			currentSignals |= TailSignal093T0.ReachTopologySlice;
			if (num > currentTopologyMaxUs)
			{
				currentTopologyMaxUs = num;
			}
			topologyOver5++;
			if (elapsedTicks >= TenMsTicks)
			{
				topologyOver10++;
			}
			if (elapsedTicks >= TwentyMsTicks)
			{
				topologyOver20++;
			}
			if (num > topologyMaxUs)
			{
				topologyMaxUs = num;
			}
		}
	}

	internal static void NoteS4HeavyValidator(int rejects)
	{
		if (rejects > 0)
		{
			currentSignals |= TailSignal093T0.S4HeavyValidator;
			if (rejects > currentS4MaxRejects)
			{
				currentS4MaxRejects = rejects;
			}
			s4HeavyEvents++;
			if (rejects > s4MaxRejects)
			{
				s4MaxRejects = rejects;
			}
		}
	}

	internal static string Summary()
	{
		long num = samples;
		double num2 = ((num == 0L) ? 0.0 : ((double)totalUs / (double)num / 1000.0));
		return "Tail Observatory V0.9.3-T0: samples=" + num + ", avgMs=" + num2.ToString("F3") + ", P50ms=" + ((double)PercentileUs(0.5) / 1000.0).ToString("F3") + ", P95ms=" + ((double)PercentileUs(0.95) / 1000.0).ToString("F3") + ", P99ms=" + ((double)PercentileUs(0.99) / 1000.0).ToString("F3") + ", P99.9ms=" + ((double)PercentileUs(0.999) / 1000.0).ToString("F3") + ", >20ms=" + over20 + ", >30ms=" + over30 + ", >50ms=" + over50 + ", >100ms=" + over100 + ", maxMs=" + ((double)maxUs / 1000.0).ToString("F3") + ", spike20Per10k=" + RatePer10k(over20, num).ToString("F2") + ", spike50Per10k=" + RatePer10k(over50, num).ToString("F2") + ". Histogram resolution=" + 250 + "us; final bucket >=" + 250000 + "us.";
	}

	internal static string ComponentSummary()
	{
		return "Tail component signals (measurement-only): ReachQuery >5/>10/>20ms=" + reachQueryOver5 + "/" + reachQueryOver10 + "/" + reachQueryOver20 + ", maxMs=" + ((double)reachQueryMaxUs / 1000.0).ToString("F3") + "; ReachCapture=" + reachCaptureOver5 + "/" + reachCaptureOver10 + "/" + reachCaptureOver20 + ", maxMs=" + ((double)reachCaptureMaxUs / 1000.0).ToString("F3") + "; TopologySlice=" + topologyOver5 + "/" + topologyOver10 + "/" + topologyOver20 + ", maxMs=" + ((double)topologyMaxUs / 1000.0).ToString("F3") + "; S4HeavyValidatorEvents=" + s4HeavyEvents + ", maxRejects=" + s4MaxRejects + ".";
	}

	internal static string RecentSummary()
	{
		if (recentCount <= 0)
		{
			return "Recent >=20ms tail frames: none.";
		}
		StringBuilder stringBuilder = new StringBuilder(1024);
		stringBuilder.Append("Recent >=20ms tail frames (oldest->newest): ");
		int num = ((recentCount == 16) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			int num2 = (num + i) % 16;
			TailFrame tailFrame = Recent[num2];
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			stringBuilder.Append("frame=").Append(tailFrame.Frame).Append(",tick=")
				.Append(tailFrame.GameTick)
				.Append(",ms=")
				.Append(((double)tailFrame.DurationUs / 1000.0).ToString("F2"))
				.Append(",signals=")
				.Append(tailFrame.Signals);
			if (tailFrame.ReachQueryMaxUs > 0)
			{
				stringBuilder.Append(",reachQ=").Append(((double)tailFrame.ReachQueryMaxUs / 1000.0).ToString("F2")).Append("ms");
			}
			if (tailFrame.ReachCaptureMaxUs > 0)
			{
				stringBuilder.Append(",capture=").Append(((double)tailFrame.ReachCaptureMaxUs / 1000.0).ToString("F2")).Append("ms");
			}
			if (tailFrame.TopologyMaxUs > 0)
			{
				stringBuilder.Append(",topology=").Append(((double)tailFrame.TopologyMaxUs / 1000.0).ToString("F2")).Append("ms");
			}
			if (tailFrame.S4MaxRejects > 0)
			{
				stringBuilder.Append(",s4Rejects=").Append(tailFrame.S4MaxRejects);
			}
		}
		return stringBuilder.ToString();
	}

	private static long PercentileUs(double percentile)
	{
		long num = samples;
		if (num <= 0)
		{
			return 0L;
		}
		long num2 = (long)Math.Ceiling((double)num * percentile);
		if (num2 < 1)
		{
			num2 = 1L;
		}
		long num3 = 0L;
		for (int i = 0; i < Histogram.Length; i++)
		{
			num3 += Histogram[i];
			if (num3 >= num2)
			{
				if (i >= Histogram.Length - 1)
				{
					return Math.Max(250000L, maxUs);
				}
				return (long)i * 250L + 125;
			}
		}
		return maxUs;
	}

	private static double RatePer10k(long value, long n)
	{
		if (n > 0)
		{
			return (double)value * 10000.0 / (double)n;
		}
		return 0.0;
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
