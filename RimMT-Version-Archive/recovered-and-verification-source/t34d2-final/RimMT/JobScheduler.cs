using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Verse;

namespace RimMT;

public sealed class JobScheduler
{
	private sealed class WorkItem
	{
		internal readonly string FeatureId;

		internal readonly Action Action;

		internal readonly bool Production;

		internal WorkItem(string featureId, Action action, bool production)
		{
			FeatureId = featureId;
			Action = action;
			Production = production;
		}
	}

	private readonly ConcurrentQueue<WorkItem> high = new ConcurrentQueue<WorkItem>();

	private readonly ConcurrentQueue<WorkItem> normal = new ConcurrentQueue<WorkItem>();

	private readonly ConcurrentQueue<WorkItem> background = new ConcurrentQueue<WorkItem>();

	private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0, int.MaxValue);

	private readonly object enqueueSync = new object();

	private readonly Thread[] workers;

	private readonly int maxPending;

	private volatile bool running = true;

	private int pending;

	private int activeWorkers;

	private int peakActiveWorkers;

	private int highWaterPending;

	private int activeBackgroundWorkers;

	private long enqueued;

	private long completed;

	private long rejected;

	private long failures;

	private long wakeReleases;

	private long multiWakeCalls;

	private long parallelBatchesEnqueued;

	private long timeoutPollClaims;

	private int productionPending;

	private int productionActiveWorkers;

	private int productionPeakActiveWorkers;

	private int productionHighWaterPending;

	private long productionEnqueued;

	private long productionCompleted;

	private long productionRejected;

	private long productionFailures;

	private long productionParallelBatches;

	private long productionConcurrencySamples;

	private long productionActiveWorkerSamples;

	private long productionBusyTicks;

	private long productionFirstStartTimestamp;

	public int WorkerCount => workers.Length;

	public int Pending => Volatile.Read(ref pending);

	public int ActiveWorkers => Volatile.Read(ref activeWorkers);

	public int PeakActiveWorkers => Volatile.Read(ref peakActiveWorkers);

	public int HighWaterPending => Volatile.Read(ref highWaterPending);

	public long Enqueued => Interlocked.Read(ref enqueued);

	public long Completed => Interlocked.Read(ref completed);

	public long Rejected => Interlocked.Read(ref rejected);

	public long Failures => Interlocked.Read(ref failures);

	public long WakeReleases => Interlocked.Read(ref wakeReleases);

	public long MultiWakeCalls => Interlocked.Read(ref multiWakeCalls);

	public long ParallelBatchesEnqueued => Interlocked.Read(ref parallelBatchesEnqueued);

	public long TimeoutPollClaims => Interlocked.Read(ref timeoutPollClaims);

	public int ProductionPending => Volatile.Read(ref productionPending);

	public int ProductionActiveWorkers => Volatile.Read(ref productionActiveWorkers);

	public int ProductionPeakActiveWorkers => Volatile.Read(ref productionPeakActiveWorkers);

	public int ProductionHighWaterPending => Volatile.Read(ref productionHighWaterPending);

	public long ProductionEnqueued => Interlocked.Read(ref productionEnqueued);

	public long ProductionCompleted => Interlocked.Read(ref productionCompleted);

	public long ProductionRejected => Interlocked.Read(ref productionRejected);

	public long ProductionFailures => Interlocked.Read(ref productionFailures);

	public long ProductionParallelBatches => Interlocked.Read(ref productionParallelBatches);

	public long ProductionConcurrencySamples => Volatile.Read(ref productionConcurrencySamples);

	public double ProductionAverageActiveWorkers
	{
		get
		{
			long num = Volatile.Read(ref productionConcurrencySamples);
			if (num > 0)
			{
				return (double)Volatile.Read(ref productionActiveWorkerSamples) / (double)num;
			}
			return 0.0;
		}
	}

	public double ProductionWorkerUtilizationPercent
	{
		get
		{
			if (workers.Length == 0)
			{
				return 0.0;
			}
			return ProductionAverageActiveWorkers * 100.0 / (double)workers.Length;
		}
	}

	public double ProductionBusyMilliseconds => (double)Interlocked.Read(ref productionBusyTicks) * 1000.0 / (double)Stopwatch.Frequency;

	public double ProductionBusyWindowMilliseconds
	{
		get
		{
			long num = Interlocked.Read(ref productionFirstStartTimestamp);
			if (num <= 0)
			{
				return 0.0;
			}
			long num2 = Stopwatch.GetTimestamp() - num;
			if (num2 > 0)
			{
				return (double)num2 * 1000.0 / (double)Stopwatch.Frequency;
			}
			return 0.0;
		}
	}

	public double ProductionBusyAverageWorkers
	{
		get
		{
			long num = Interlocked.Read(ref productionFirstStartTimestamp);
			if (num <= 0)
			{
				return 0.0;
			}
			long num2 = Stopwatch.GetTimestamp() - num;
			if (num2 <= 0)
			{
				return 0.0;
			}
			return (double)Interlocked.Read(ref productionBusyTicks) / (double)num2;
		}
	}

	public double ProductionBusyUtilizationPercent
	{
		get
		{
			if (workers.Length == 0)
			{
				return 0.0;
			}
			return ProductionBusyAverageWorkers * 100.0 / (double)workers.Length;
		}
	}

	public JobScheduler(int workerCount, int maxPendingJobs)
	{
		if (workerCount < 1)
		{
			workerCount = 1;
		}
		maxPending = Math.Max(1024, maxPendingJobs);
		workers = new Thread[workerCount];
		for (int i = 0; i < workers.Length; i++)
		{
			int index = i;
			workers[i] = new Thread((ThreadStart)delegate
			{
				WorkerLoop(index);
			});
			workers[i].IsBackground = true;
			workers[i].Name = "RimMT-Worker-" + index;
			workers[i].Start();
		}
	}

	public bool TryEnqueue(string featureId, JobPriority priority, Action action)
	{
		bool flag = IsProductionFeature(featureId);
		if (!running || action == null || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
		{
			Interlocked.Increment(ref rejected);
			if (flag)
			{
				Interlocked.Increment(ref productionRejected);
			}
			return false;
		}
		lock (enqueueSync)
		{
			if (pending >= maxPending)
			{
				Interlocked.Increment(ref rejected);
				if (flag)
				{
					Interlocked.Increment(ref productionRejected);
				}
				return false;
			}
			int value = Interlocked.Increment(ref pending);
			Interlocked.Increment(ref enqueued);
			UpdateHighWater(ref highWaterPending, value);
			if (flag)
			{
				int value2 = Interlocked.Increment(ref productionPending);
				Interlocked.Increment(ref productionEnqueued);
				UpdateHighWater(ref productionHighWaterPending, value2);
			}
			EnqueueReserved(new WorkItem(featureId, action, flag), priority);
		}
		ReleaseWakeCredits(1);
		return true;
	}

	public bool ParallelFor(string featureId, int fromInclusive, int toExclusive, int batchSize, Action<int, int> body, Action onComplete = null, JobPriority priority = JobPriority.Normal)
	{
		bool flag = IsProductionFeature(featureId);
		if (body == null || toExclusive <= fromInclusive || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
		{
			Interlocked.Increment(ref rejected);
			if (flag)
			{
				Interlocked.Increment(ref productionRejected);
			}
			return false;
		}
		if (batchSize <= 0)
		{
			batchSize = 256;
		}
		int remaining;
		int num = (remaining = (toExclusive - fromInclusive + batchSize - 1) / batchSize);
		int allQueued = 0;
		lock (enqueueSync)
		{
			if (!running || pending + num > maxPending || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
			{
				Interlocked.Increment(ref rejected);
				if (flag)
				{
					Interlocked.Increment(ref productionRejected);
				}
				return false;
			}
			int value = Interlocked.Add(ref pending, num);
			Interlocked.Add(ref enqueued, num);
			Interlocked.Add(ref parallelBatchesEnqueued, num);
			UpdateHighWater(ref highWaterPending, value);
			if (flag)
			{
				int value2 = Interlocked.Add(ref productionPending, num);
				Interlocked.Add(ref productionEnqueued, num);
				Interlocked.Add(ref productionParallelBatches, num);
				UpdateHighWater(ref productionHighWaterPending, value2);
			}
			for (int i = fromInclusive; i < toExclusive; i += batchSize)
			{
				int s = i;
				int e = Math.Min(i + batchSize, toExclusive);
				EnqueueReserved(new WorkItem(featureId, delegate
				{
					body(s, e);
					if (Interlocked.Decrement(ref remaining) == 0 && Volatile.Read(ref allQueued) == 1 && onComplete != null)
					{
						MainThreadDispatcher.TryEnqueue(onComplete);
					}
				}, flag), priority);
			}
			Volatile.Write(ref allQueued, 1);
		}
		if (Volatile.Read(ref remaining) == 0 && onComplete != null)
		{
			MainThreadDispatcher.TryEnqueue(onComplete);
		}
		ReleaseWakeCredits(num);
		return true;
	}

	internal void SampleProductionConcurrency()
	{
		productionConcurrencySamples++;
		productionActiveWorkerSamples += Volatile.Read(ref productionActiveWorkers);
	}

	private static bool IsProductionFeature(string featureId)
	{
		return !string.Equals(featureId, "diagnostics.selfTest", StringComparison.Ordinal);
	}

	private void ReleaseWakeCredits(int count)
	{
		if (count <= 0)
		{
			return;
		}
		if (count > 1)
		{
			Interlocked.Increment(ref multiWakeCalls);
		}
		Interlocked.Add(ref wakeReleases, count);
		try
		{
			wakeSignal.Release(count);
		}
		catch (SemaphoreFullException)
		{
		}
	}

	private void EnqueueReserved(WorkItem item, JobPriority priority)
	{
		switch (priority)
		{
		case JobPriority.High:
			high.Enqueue(item);
			break;
		case JobPriority.Background:
			background.Enqueue(item);
			break;
		default:
			normal.Enqueue(item);
			break;
		}
	}

	private void WorkerLoop(int workerIndex)
	{
		while (running)
		{
			bool flag = wakeSignal.Wait(5);
			if (!TryTake(out var item, out var backgroundSlot))
			{
				continue;
			}
			if (!flag)
			{
				Interlocked.Increment(ref timeoutPollClaims);
			}
			int value = Interlocked.Increment(ref activeWorkers);
			UpdatePeak(ref peakActiveWorkers, value);
			long num = 0L;
			if (item.Production)
			{
				int value2 = Interlocked.Increment(ref productionActiveWorkers);
				UpdatePeak(ref productionPeakActiveWorkers, value2);
				num = Stopwatch.GetTimestamp();
				Interlocked.CompareExchange(ref productionFirstStartTimestamp, num, 0L);
			}
			try
			{
				item.Action();
				Interlocked.Increment(ref completed);
				if (item.Production)
				{
					Interlocked.Increment(ref productionCompleted);
				}
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref failures);
				if (item.Production)
				{
					Interlocked.Increment(ref productionFailures);
				}
				CircuitBreaker.RecordFailure(item.FeatureId, ex);
				Log.Error("[RimMT] Worker exception in feature '" + item.FeatureId + "' on " + Thread.CurrentThread.Name + ": " + ex);
			}
			finally
			{
				if (item.Production && num != 0L)
				{
					long num2 = Stopwatch.GetTimestamp() - num;
					if (num2 > 0)
					{
						Interlocked.Add(ref productionBusyTicks, num2);
					}
				}
				if (backgroundSlot)
				{
					Interlocked.Decrement(ref activeBackgroundWorkers);
				}
				if (item.Production)
				{
					Interlocked.Decrement(ref productionActiveWorkers);
					Interlocked.Decrement(ref productionPending);
				}
				Interlocked.Decrement(ref activeWorkers);
				Interlocked.Decrement(ref pending);
			}
		}
	}

	private bool TryTake(out WorkItem item, out bool backgroundSlot)
	{
		backgroundSlot = false;
		if (high.TryDequeue(out item))
		{
			return true;
		}
		if (normal.TryDequeue(out item))
		{
			return true;
		}
		if (!FeatureGate.IsEnabled("runtime.adaptiveBurst"))
		{
			return background.TryDequeue(out item);
		}
		int num = AdaptiveLoadBalancer.BackgroundConcurrencyBudget(workers.Length);
		if (num <= 0 || !TryAcquireBackgroundSlot(num))
		{
			item = null;
			return false;
		}
		if (background.TryDequeue(out item))
		{
			backgroundSlot = true;
			return true;
		}
		Interlocked.Decrement(ref activeBackgroundWorkers);
		return false;
	}

	private bool TryAcquireBackgroundSlot(int budget)
	{
		int num;
		do
		{
			num = Volatile.Read(ref activeBackgroundWorkers);
			if (num >= budget)
			{
				return false;
			}
		}
		while (Interlocked.CompareExchange(ref activeBackgroundWorkers, num + 1, num) != num);
		return true;
	}

	private static void UpdateHighWater(ref int field, int value)
	{
		int num;
		while (value > (num = Volatile.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}

	private static void UpdatePeak(ref int field, int value)
	{
		int num;
		while (value > (num = Volatile.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
