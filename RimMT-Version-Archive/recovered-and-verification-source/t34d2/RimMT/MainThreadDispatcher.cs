using System;
using System.Collections.Concurrent;
using System.Threading;
using Verse;

namespace RimMT;

public static class MainThreadDispatcher
{
	private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

	private const int MaxQueuedCallbacks = 50000;

	private static int queued;

	private static int highWater;

	private static long enqueued;

	private static long inlineExecuted;

	private static long drained;

	private static long rejected;

	private static long failures;

	private static long drainCalls;

	private static long butterLogicalTickDeferred;

	private static long butterProbeFailureDeferred;

	public static int Queued => Volatile.Read(ref queued);

	public static int HighWater => Volatile.Read(ref highWater);

	public static long Enqueued => Interlocked.Read(ref enqueued);

	public static long InlineExecuted => Interlocked.Read(ref inlineExecuted);

	public static long Drained => Interlocked.Read(ref drained);

	public static long Rejected => Interlocked.Read(ref rejected);

	public static long Failures => Interlocked.Read(ref failures);

	public static long DrainCalls => Interlocked.Read(ref drainCalls);

	public static long ButterLogicalTickDeferred => Interlocked.Read(ref butterLogicalTickDeferred);

	public static long ButterProbeFailureDeferred => Interlocked.Read(ref butterProbeFailureDeferred);

	public static bool TryEnqueue(Action action)
	{
		if (action == null)
		{
			return false;
		}
		if (RimMTThreadGuard.IsMainThread)
		{
			bool flag = false;
			if (RuntimeCompatibility.ButterPlusPlusActive)
			{
				if (!RuntimeCompatibility.TryGetButterLogicalTickInProgress(out var inProgress))
				{
					flag = true;
					Interlocked.Increment(ref butterProbeFailureDeferred);
				}
				else if (inProgress)
				{
					flag = true;
					Interlocked.Increment(ref butterLogicalTickDeferred);
				}
			}
			if (!flag)
			{
				Interlocked.Increment(ref inlineExecuted);
				Run(action);
				return true;
			}
		}
		int num = Interlocked.Increment(ref queued);
		if (num > 50000)
		{
			Interlocked.Decrement(ref queued);
			Interlocked.Increment(ref rejected);
			return false;
		}
		UpdateHighWater(num);
		Queue.Enqueue(action);
		Interlocked.Increment(ref enqueued);
		return true;
	}

	internal static void Drain(int maxActions)
	{
		if (!RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		Interlocked.Increment(ref drainCalls);
		int i;
		for (i = 0; i < maxActions; i++)
		{
			if (!Queue.TryDequeue(out var result))
			{
				break;
			}
			Interlocked.Decrement(ref queued);
			Run(result);
		}
		if (i > 0)
		{
			Interlocked.Add(ref drained, i);
		}
	}

	internal static string Summary()
	{
		return "Dispatcher: queued=" + Queued + ", enqueued=" + Enqueued + ", drained=" + Drained + ", inline=" + InlineExecuted + ", butterLogicalTickDeferred=" + ButterLogicalTickDeferred + ", butterProbeFailureDeferred=" + ButterProbeFailureDeferred + ", rejected=" + Rejected + ", failures=" + Failures + ", drainCalls=" + DrainCalls + ", highWater=" + HighWater;
	}

	private static void UpdateHighWater(int value)
	{
		int num;
		while (value > (num = Volatile.Read(ref highWater)) && Interlocked.CompareExchange(ref highWater, value, num) != num)
		{
		}
	}

	private static void Run(Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			Log.Error("[RimMT] Main-thread callback failed: " + ex);
		}
	}
}
