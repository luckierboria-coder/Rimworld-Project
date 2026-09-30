using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class SimulationEpochCoordinator093T26
{
	internal struct EpochState
	{
		internal bool Entered;

		internal long Started;
	}

	internal const string FeatureId = "parallel.engineStage";

	private const int MinParallelItems = 256;

	private const int BatchSize = 64;

	private const int MaxInFlightKernels = 2;

	private const double MainThreadBudgetMs = 0.4;

	private static bool installed;

	private static int installFailures;

	private static long epoch;

	private static long epochCalls;

	private static long epochTicksTotal;

	private static long epochTicksMax;

	private static long currentEpoch;

	private static long currentGameTick;

	private static int inFlightKernels;

	private static long kernelAttempts;

	private static long kernelAccepted;

	private static long kernelCompletedInBudget;

	private static long kernelTimedOut;

	private static long kernelRejected;

	private static long kernelBusyBypass;

	private static long kernelItems;

	private static long kernelBatches;

	private static long kernelMainWaitTicks;

	private static long kernelMainWaitTicksMax;

	internal static long CurrentEpoch => Interlocked.Read(ref currentEpoch);

	internal static long CurrentGameTick => Interlocked.Read(ref currentGameTick);

	internal static void Apply(Harmony harmony)
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Expected O, but got Unknown
		//IL_0089: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(TickManager), "DoSingleTick", (Type[])null, (Type[])null);
			if (methodBase == null)
			{
				installFailures++;
				Log.Warning("[RimMT] T26 simulation epoch unavailable: TickManager.DoSingleTick not found.");
				return;
			}
			harmony.Patch(methodBase, new HarmonyMethod(typeof(SimulationEpochCoordinator093T26), "TickPrefix", (Type[])null)
			{
				priority = 1150
			}, new HarmonyMethod(typeof(SimulationEpochCoordinator093T26), "TickPostfix", (Type[])null)
			{
				priority = -350
			}, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = true;
			Log.Message("[RimMT] T26 Engine Parallel Simulation coordinator installed at DoSingleTick. Unity/Verse state stays main-thread; primitive compute stages may fan out to workers with bounded no-wait fallback.");
		}
		catch (Exception ex)
		{
			installFailures++;
			installed = false;
			Log.Warning("[RimMT] T26 simulation epoch install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void TickPrefix(ref EpochState __state)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Invalid comparison between Unknown and I4
		__state = default(EpochState);
		if (RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			__state.Entered = true;
			__state.Started = Stopwatch.GetTimestamp();
			long value = Interlocked.Increment(ref epoch);
			Interlocked.Exchange(ref currentEpoch, value);
			try
			{
				Interlocked.Exchange(ref currentGameTick, Find.TickManager.TicksGame);
			}
			catch
			{
				Interlocked.Exchange(ref currentGameTick, -1L);
			}
			Interlocked.Increment(ref epochCalls);
		}
	}

	public static void TickPostfix(EpochState __state)
	{
		if (__state.Entered && __state.Started != 0L)
		{
			long num = Stopwatch.GetTimestamp() - __state.Started;
			if (num >= 0)
			{
				Interlocked.Add(ref epochTicksTotal, num);
				UpdateMax(ref epochTicksMax, num);
			}
		}
	}

	internal static bool TryComputeRingKeys(int rootX, int rootZ, int ringSize, int[] xs, int[] zs, int[] ringKeys)
	{
		Interlocked.Increment(ref kernelAttempts);
		Interlocked.Increment(ref kernelRejected);
		return false;
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref epochCalls);
		long num2 = Interlocked.Read(ref kernelAccepted);
		double num3 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref epochTicksTotal) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num4 = ((num2 == 0L) ? 0.0 : ((double)Interlocked.Read(ref kernelItems) / (double)num2));
		double num5 = ((num2 == 0L) ? 0.0 : ((double)Interlocked.Read(ref kernelMainWaitTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num2));
		return "T26.1 engine epoch / zero-wait simulation: installed=" + installed + ", epoch=" + CurrentEpoch + ", tick=" + CurrentGameTick + ", epochCalls=" + num + ", avgEpochUs=" + num3.ToString("F1") + ", maxEpochMs=" + ((double)Interlocked.Read(ref epochTicksMax) * 1000.0 / (double)Stopwatch.Frequency).ToString("F2") + ", kernels[attempt/accepted/inBudget/timeout/rejected/busy]=" + Interlocked.Read(ref kernelAttempts) + "/" + num2 + "/" + Interlocked.Read(ref kernelCompletedInBudget) + "/" + Interlocked.Read(ref kernelTimedOut) + "/" + Interlocked.Read(ref kernelRejected) + "/" + Interlocked.Read(ref kernelBusyBypass) + ", batches=" + Interlocked.Read(ref kernelBatches) + ", avgItems=" + num4.ToString("F1") + ", avgMainWaitUs=" + num5.ToString("F2") + ", maxMainWaitUs=" + ((double)Interlocked.Read(ref kernelMainWaitTicksMax) * 1000000.0 / (double)Stopwatch.Frequency).ToString("F2") + ", inFlight=" + Volatile.Read(ref inFlightKernels) + ", installFailures=" + installFailures + ". DoSingleTick remains on the Unity main thread; active same-call worker consumer=OFF; production simulation never waits/spins/joins workers.";
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
