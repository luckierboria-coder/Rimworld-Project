using System;
using System.Diagnostics;
using System.Threading;

namespace RimMT;

internal static class AdaptiveLoadBalancer
{
	private const int Window = 256;

	private const int PressureRefreshMask = 15;

	private const int DownshiftWindows = 4;

	private static readonly double[] TickMs = new double[256];

	private static readonly double[] SortScratch = new double[256];

	private static int index;

	private static int count;

	private static int downshiftStreak;

	private static double emaMs;

	private static double rollingP95Ms;

	private static double rollingSlowRatio;

	private static long sampleCount;

	private static long spikes;

	private static long butterFrameSamples;

	private static int pressureValue = 1;

	internal static LoadPressure Pressure => (LoadPressure)Volatile.Read(ref pressureValue);

	internal static double EmaTickMs => Volatile.Read(ref emaMs);

	internal static double RollingP95Ms => Volatile.Read(ref rollingP95Ms);

	internal static double RollingSlowRatio => Volatile.Read(ref rollingSlowRatio);

	internal static long SampleCount => Volatile.Read(ref sampleCount);

	internal static long SpikeCount => Volatile.Read(ref spikes);

	internal static long ButterFrameSamples => Volatile.Read(ref butterFrameSamples);

	internal static string SampleSource
	{
		get
		{
			if (!RuntimeCompatibility.ButterPlusPlusActive)
			{
				return "DoSingleTick";
			}
			return "Butter++ TickManagerUpdate slice";
		}
	}

	internal static JobPriority RecommendedOffloadPriority
	{
		get
		{
			switch (Pressure)
			{
			case LoadPressure.High:
			case LoadPressure.Critical:
				return JobPriority.High;
			case LoadPressure.Low:
				return JobPriority.Background;
			default:
				return JobPriority.Normal;
			}
		}
	}

	internal static int BackgroundConcurrencyBudget(int workerCount)
	{
		if (workerCount <= 0)
		{
			return 0;
		}
		return Pressure switch
		{
			LoadPressure.Low => workerCount, 
			LoadPressure.Normal => Math.Max(1, (workerCount + 1) / 2), 
			LoadPressure.High => 1, 
			_ => 0, 
		};
	}

	internal static void RecordTick(long startTimestamp)
	{
		if (!RuntimeCompatibility.ButterPlusPlusActive)
		{
			RecordSample(startTimestamp);
		}
	}

	internal static void RecordButterFrameSlice(long startTimestamp)
	{
		if (RuntimeCompatibility.ButterPlusPlusActive && startTimestamp != 0L)
		{
			butterFrameSamples++;
			RecordSample(startTimestamp);
		}
	}

	private static void RecordSample(long startTimestamp)
	{
		if (startTimestamp != 0L)
		{
			double num = (double)(Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / (double)Stopwatch.Frequency;
			long num2 = ++sampleCount;
			TickMs[index] = num;
			index = (index + 1) % 256;
			if (count < 256)
			{
				count++;
			}
			emaMs = ((emaMs <= 0.0) ? num : (emaMs * 0.92 + num * 0.08));
			double num3 = Math.Max(20.0, emaMs * 1.75);
			if (num >= num3)
			{
				spikes++;
			}
			if ((num2 & 0xF) == 0L || count < 32)
			{
				RefreshPressure();
			}
		}
	}

	private static void RefreshPressure()
	{
		if (count <= 0)
		{
			return;
		}
		for (int i = 0; i < count; i++)
		{
			SortScratch[i] = TickMs[i];
		}
		Array.Sort(SortScratch, 0, count);
		int num = (int)Math.Ceiling((double)count * 0.95) - 1;
		if (num < 0)
		{
			num = 0;
		}
		if (num >= count)
		{
			num = count - 1;
		}
		rollingP95Ms = SortScratch[num];
		double num2 = Math.Max(20.0, emaMs * 1.35);
		int num3 = 0;
		for (int j = 0; j < count; j++)
		{
			if (TickMs[j] >= num2)
			{
				num3++;
			}
		}
		rollingSlowRatio = (double)num3 / (double)count;
		LoadPressure loadPressure = ((!(rollingP95Ms >= Math.Max(40.0, emaMs * 2.0)) && !(rollingSlowRatio >= 0.2)) ? ((rollingP95Ms >= Math.Max(28.0, emaMs * 1.5) || rollingSlowRatio >= 0.08) ? LoadPressure.High : ((!(emaMs < 8.0) || !(rollingP95Ms < 12.0) || !(rollingSlowRatio < 0.02)) ? LoadPressure.Normal : LoadPressure.Low)) : LoadPressure.Critical);
		LoadPressure loadPressure2 = (LoadPressure)Volatile.Read(ref pressureValue);
		if (loadPressure > loadPressure2)
		{
			downshiftStreak = 0;
			Volatile.Write(ref pressureValue, (int)loadPressure);
			return;
		}
		if (loadPressure == loadPressure2)
		{
			downshiftStreak = 0;
			return;
		}
		downshiftStreak++;
		if (downshiftStreak >= 4)
		{
			downshiftStreak = 0;
			int value = Math.Max((int)loadPressure, (int)(loadPressure2 - 1));
			Volatile.Write(ref pressureValue, value);
		}
	}

	internal static double Percentile95()
	{
		double num = Volatile.Read(ref rollingP95Ms);
		if (num > 0.0)
		{
			return num;
		}
		if (count == 0)
		{
			return 0.0;
		}
		for (int i = 0; i < count; i++)
		{
			SortScratch[i] = TickMs[i];
		}
		Array.Sort(SortScratch, 0, count);
		int num2 = (int)Math.Ceiling((double)count * 0.95) - 1;
		if (num2 < 0)
		{
			num2 = 0;
		}
		if (num2 >= count)
		{
			num2 = count - 1;
		}
		return SortScratch[num2];
	}
}
