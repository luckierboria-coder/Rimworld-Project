using System;
using System.Collections.Concurrent;
using System.Threading;
using Verse;

namespace RimMT
{
    public sealed class JobScheduler
    {
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

        // Production-only counters deliberately exclude diagnostics.selfTest so a manual CPU test
        // cannot make normal gameplay appear to saturate all workers. These are cheap aggregate
        // counters only; no per-item stopwatch/profiler is installed.
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

        public int WorkerCount { get { return workers.Length; } }
        public int Pending { get { return Volatile.Read(ref pending); } }
        public int ActiveWorkers { get { return Volatile.Read(ref activeWorkers); } }
        public int PeakActiveWorkers { get { return Volatile.Read(ref peakActiveWorkers); } }
        public int HighWaterPending { get { return Volatile.Read(ref highWaterPending); } }
        public long Enqueued { get { return Interlocked.Read(ref enqueued); } }
        public long Completed { get { return Interlocked.Read(ref completed); } }
        public long Rejected { get { return Interlocked.Read(ref rejected); } }
        public long Failures { get { return Interlocked.Read(ref failures); } }
        public long WakeReleases { get { return Interlocked.Read(ref wakeReleases); } }
        public long MultiWakeCalls { get { return Interlocked.Read(ref multiWakeCalls); } }
        public long ParallelBatchesEnqueued { get { return Interlocked.Read(ref parallelBatchesEnqueued); } }
        public long TimeoutPollClaims { get { return Interlocked.Read(ref timeoutPollClaims); } }

        public int ProductionPending { get { return Volatile.Read(ref productionPending); } }
        public int ProductionActiveWorkers { get { return Volatile.Read(ref productionActiveWorkers); } }
        public int ProductionPeakActiveWorkers { get { return Volatile.Read(ref productionPeakActiveWorkers); } }
        public int ProductionHighWaterPending { get { return Volatile.Read(ref productionHighWaterPending); } }
        public long ProductionEnqueued { get { return Interlocked.Read(ref productionEnqueued); } }
        public long ProductionCompleted { get { return Interlocked.Read(ref productionCompleted); } }
        public long ProductionRejected { get { return Interlocked.Read(ref productionRejected); } }
        public long ProductionFailures { get { return Interlocked.Read(ref productionFailures); } }
        public long ProductionParallelBatches { get { return Interlocked.Read(ref productionParallelBatches); } }
        public long ProductionConcurrencySamples { get { return Interlocked.Read(ref productionConcurrencySamples); } }
        public double ProductionAverageActiveWorkers
        {
            get
            {
                long samples = Interlocked.Read(ref productionConcurrencySamples);
                return samples <= 0 ? 0.0 : Interlocked.Read(ref productionActiveWorkerSamples) / (double)samples;
            }
        }
        public double ProductionWorkerUtilizationPercent
        {
            get
            {
                if (workers.Length <= 0) return 0.0;
                return ProductionAverageActiveWorkers * 100.0 / workers.Length;
            }
        }

        public JobScheduler(int workerCount, int maxPendingJobs)
        {
            if (workerCount < 1) workerCount = 1;
            maxPending = Math.Max(1024, maxPendingJobs);
            workers = new Thread[workerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                int index = i;
                workers[i] = new Thread(() => WorkerLoop(index));
                workers[i].IsBackground = true;
                workers[i].Name = "RimMT-Worker-" + index;
                workers[i].Start();
            }
        }

        public bool TryEnqueue(string featureId, JobPriority priority, Action action)
        {
            bool production = IsProductionFeature(featureId);
            if (!running || action == null || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
            {
                Interlocked.Increment(ref rejected);
                if (production) Interlocked.Increment(ref productionRejected);
                return false;
            }

            lock (enqueueSync)
            {
                if (pending >= maxPending)
                {
                    Interlocked.Increment(ref rejected);
                    if (production) Interlocked.Increment(ref productionRejected);
                    return false;
                }

                int nowPending = Interlocked.Increment(ref pending);
                Interlocked.Increment(ref enqueued);
                UpdateHighWater(ref highWaterPending, nowPending);

                if (production)
                {
                    int productionNowPending = Interlocked.Increment(ref productionPending);
                    Interlocked.Increment(ref productionEnqueued);
                    UpdateHighWater(ref productionHighWaterPending, productionNowPending);
                }

                EnqueueReserved(new WorkItem(featureId, action, production), priority);
            }
            ReleaseWakeCredits(1);
            return true;
        }

        public bool ParallelFor(string featureId, int fromInclusive, int toExclusive, int batchSize, Action<int,int> body, Action onComplete = null, JobPriority priority = JobPriority.Normal)
        {
            bool production = IsProductionFeature(featureId);
            if (body == null || toExclusive <= fromInclusive || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
            {
                Interlocked.Increment(ref rejected);
                if (production) Interlocked.Increment(ref productionRejected);
                return false;
            }

            if (batchSize <= 0) batchSize = 256;
            int count = toExclusive - fromInclusive;
            int batches = (count + batchSize - 1) / batchSize;
            int remaining = batches;
            int allQueued = 0;

            lock (enqueueSync)
            {
                if (!running || pending + batches > maxPending || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
                {
                    Interlocked.Increment(ref rejected);
                    if (production) Interlocked.Increment(ref productionRejected);
                    return false;
                }

                int nowPending = Interlocked.Add(ref pending, batches);
                Interlocked.Add(ref enqueued, batches);
                Interlocked.Add(ref parallelBatchesEnqueued, batches);
                UpdateHighWater(ref highWaterPending, nowPending);

                if (production)
                {
                    int productionNowPending = Interlocked.Add(ref productionPending, batches);
                    Interlocked.Add(ref productionEnqueued, batches);
                    Interlocked.Add(ref productionParallelBatches, batches);
                    UpdateHighWater(ref productionHighWaterPending, productionNowPending);
                }

                for (int start = fromInclusive; start < toExclusive; start += batchSize)
                {
                    int s = start;
                    int e = Math.Min(start + batchSize, toExclusive);
                    EnqueueReserved(new WorkItem(featureId, () =>
                    {
                        body(s,e);
                        if (Interlocked.Decrement(ref remaining) == 0 && Volatile.Read(ref allQueued) == 1 && onComplete != null)
                            MainThreadDispatcher.TryEnqueue(onComplete);
                    }, production), priority);
                }
                Volatile.Write(ref allQueued, 1);
            }

            if (Volatile.Read(ref remaining) == 0 && onComplete != null)
                MainThreadDispatcher.TryEnqueue(onComplete);

            ReleaseWakeCredits(batches);
            return true;
        }

        internal void SampleProductionConcurrency()
        {
            Interlocked.Increment(ref productionConcurrencySamples);
            Interlocked.Add(ref productionActiveWorkerSamples, Volatile.Read(ref productionActiveWorkers));
        }

        private static bool IsProductionFeature(string featureId)
        {
            return !string.Equals(featureId, "diagnostics.selfTest", StringComparison.Ordinal);
        }

        private void ReleaseWakeCredits(int count)
        {
            if (count <= 0)
                return;
            if (count > 1)
                Interlocked.Increment(ref multiWakeCalls);
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
                case JobPriority.High: high.Enqueue(item); break;
                case JobPriority.Background: background.Enqueue(item); break;
                default: normal.Enqueue(item); break;
            }
        }

        private void WorkerLoop(int workerIndex)
        {
            while (running)
            {
                bool signaled = wakeSignal.Wait(5);
                WorkItem item;
                bool backgroundSlot;
                if (!TryTake(out item, out backgroundSlot))
                    continue;

                if (!signaled)
                    Interlocked.Increment(ref timeoutPollClaims);

                int active = Interlocked.Increment(ref activeWorkers);
                UpdatePeak(ref peakActiveWorkers, active);

                if (item.Production)
                {
                    int productionActive = Interlocked.Increment(ref productionActiveWorkers);
                    UpdatePeak(ref productionPeakActiveWorkers, productionActive);
                }

                try
                {
                    item.Action();
                    Interlocked.Increment(ref completed);
                    if (item.Production) Interlocked.Increment(ref productionCompleted);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    if (item.Production) Interlocked.Increment(ref productionFailures);
                    CircuitBreaker.RecordFailure(item.FeatureId, ex);
                    Log.Error("[RimMT] Worker exception in feature '" + item.FeatureId + "' on " + Thread.CurrentThread.Name + ": " + ex);
                }
                finally
                {
                    if (backgroundSlot) Interlocked.Decrement(ref activeBackgroundWorkers);
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
            if (high.TryDequeue(out item)) return true;
            if (normal.TryDequeue(out item)) return true;

            if (!FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                return background.TryDequeue(out item);

            int budget = AdaptiveLoadBalancer.BackgroundConcurrencyBudget(workers.Length);
            if (budget <= 0 || !TryAcquireBackgroundSlot(budget))
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
            while (true)
            {
                int current = Volatile.Read(ref activeBackgroundWorkers);
                if (current >= budget) return false;
                if (Interlocked.CompareExchange(ref activeBackgroundWorkers, current + 1, current) == current)
                    return true;
            }
        }

        private static void UpdateHighWater(ref int field, int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

        private static void UpdatePeak(ref int field, int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

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
    }
}
