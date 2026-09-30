using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld.Planet;

namespace RimMT;

internal static class WorldTailBoundary093T22
{
	private static bool installed;

	private static int patched;

	private static int installFailures;

	private static long worldCalls;

	private static long worldTicks;

	private static long worldMaxTicks;

	private static long worldOver50;

	private static long worldOver100;

	private static long worldOver1000;

	private static long componentCalls;

	private static long componentTicks;

	private static long componentMaxTicks;

	private static long componentOver50;

	private static long componentOver100;

	private static long componentOver1000;

	internal static void Apply(Harmony harmony)
	{
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Expected O, but got Unknown
		//IL_0058: Expected O, but got Unknown
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Expected O, but got Unknown
		//IL_00ca: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(World), "WorldTick", (Type[])null, (Type[])null);
			if (methodBase != null)
			{
				harmony.Patch(methodBase, new HarmonyMethod(typeof(WorldTailBoundary093T22), "WorldPrefix", (Type[])null), new HarmonyMethod(typeof(WorldTailBoundary093T22), "WorldPostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
				patched++;
			}
			MethodBase methodBase2 = AccessTools.Method(typeof(WorldComponentUtility), "WorldComponentTick", new Type[1] { typeof(World) }, (Type[])null);
			if (methodBase2 != null)
			{
				harmony.Patch(methodBase2, new HarmonyMethod(typeof(WorldTailBoundary093T22), "ComponentPrefix", (Type[])null), new HarmonyMethod(typeof(WorldTailBoundary093T22), "ComponentPostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
				patched++;
			}
			installed = patched > 0;
		}
		catch
		{
			installFailures++;
			installed = false;
		}
	}

	public static void WorldPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void WorldPostfix(long __state)
	{
		Record(__state, ref worldCalls, ref worldTicks, ref worldMaxTicks, ref worldOver50, ref worldOver100, ref worldOver1000);
	}

	public static void ComponentPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void ComponentPostfix(long __state)
	{
		Record(__state, ref componentCalls, ref componentTicks, ref componentMaxTicks, ref componentOver50, ref componentOver100, ref componentOver1000);
	}

	private static void Record(long started, ref long calls, ref long total, ref long max, ref long over50, ref long over100, ref long over1000)
	{
		if (started == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num >= 0)
		{
			Interlocked.Increment(ref calls);
			Interlocked.Add(ref total, num);
			UpdateMax(ref max, num);
			long num2 = Stopwatch.Frequency / 20;
			long num3 = Stopwatch.Frequency / 10;
			long frequency = Stopwatch.Frequency;
			if (num >= num2)
			{
				Interlocked.Increment(ref over50);
			}
			if (num >= num3)
			{
				Interlocked.Increment(ref over100);
			}
			if (num >= frequency)
			{
				Interlocked.Increment(ref over1000);
			}
		}
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref worldCalls);
		long num2 = Interlocked.Read(ref componentCalls);
		double num3 = (double)Interlocked.Read(ref worldTicks) * 1000.0 / (double)Stopwatch.Frequency;
		double num4 = (double)Interlocked.Read(ref componentTicks) * 1000.0 / (double)Stopwatch.Frequency;
		return "T22 world-tail direct boundary: installed=" + installed + ", patched=" + patched + ", World.WorldTick[calls=" + num + ", avgUs=" + ((num == 0L) ? 0.0 : (num3 * 1000.0 / (double)num)).ToString("F1") + ", maxMs=" + ((double)Interlocked.Read(ref worldMaxTicks) * 1000.0 / (double)Stopwatch.Frequency).ToString("F3") + ", >=50/100/1000ms=" + Interlocked.Read(ref worldOver50) + "/" + Interlocked.Read(ref worldOver100) + "/" + Interlocked.Read(ref worldOver1000) + "], WorldComponentUtility[calls=" + num2 + ", avgUs=" + ((num2 == 0L) ? 0.0 : (num4 * 1000.0 / (double)num2)).ToString("F1") + ", maxMs=" + ((double)Interlocked.Read(ref componentMaxTicks) * 1000.0 / (double)Stopwatch.Frequency).ToString("F3") + ", >=50/100/1000ms=" + Interlocked.Read(ref componentOver50) + "/" + Interlocked.Read(ref componentOver100) + "/" + Interlocked.Read(ref componentOver1000) + "], installFailures=" + installFailures + ". Direct Harmony timing closes the T1 WorldTick attribution hole; measurement-only.";
	}
}
