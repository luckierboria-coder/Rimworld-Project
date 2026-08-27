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

        // V0.4.15: AutoResetEvent collapsed a ParallelFor wake burst into one signal.
        // A single worker could therefore drain dozens of short batches before the other
        // workers' 5 ms polling timeout expired. SemaphoreSlim preserves wake credits so
        // independent batches actually fan out across the worker pool.
        private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0, int.MaxValue);
        private readonly object enqueueSync = new object();
        private readonly Thread[] workers;
        private readonly int maxPending;
        private volatile bool running = true;
        private int pending;
        private int activeWorkers;
        private int peakActiveWorkers;
        private int highWaterPending;
        private long enqueued;
        private long completed;
        private long rejected;
        private long failures;
        private long wakeReleases;
        private long multiWakeCalls;
        private long parallelBatchesEnqueued;

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
            if (!running || action == null || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
            {
                Interlocked.Increment(ref rejected);
                return false;
            }

            lock (enqueueSync)
            {
                if (pending >= maxPending)
                {
                    Interlocked.Increment(ref rejected);
                    return false;
                }

                int nowPending = Interlocked.Increment(ref pending);
                Interlocked.Increment(ref enqueued);
                UpdateHighWater(nowPending);
                EnqueueReserved(new WorkItem(featureId, action), priority);
            }
            WakeWorkers(1);
            return true;
        }

        public bool ParallelFor(string featureId, int fromInclusive, int toExclusive, int batchSize, Action<int,int> body, Action onComplete = null, JobPriority priority = JobPriority.Normal)
        {
            if (body == null || toExclusive <= fromInclusive || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
            {
                Interlocked.Increment(ref rejected);
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
                    return false;
                }

                int nowPending = Interlocked.Add(ref pending, batches);
                Interlocked.Add(ref enqueued, batches);
                Interlocked.Add(ref parallelBatchesEnqueued, batches);
                UpdateHighWater(nowPending);

                for (int start = fromInclusive; start < toExclusive; start += batchSize)
                {
                    int s = start;
                    int e = Math.Min(start + batchSize, toExclusive);
                    EnqueueReserved(new WorkItem(featureId, () =>
                    {
                        body(s,e);
                        if (Interlocked.Decrement(ref remaining) == 0 && Volatile.Read(ref allQueued) == 1 && onComplete != null)
                            MainThreadDispatcher.TryEnqueue(onComplete);
                    }), priority);
                }
                Volatile.Write(ref allQueued, 1);
            }

            if (Volatile.Read(ref remaining) == 0 && onComplete != null)
                MainThreadDispatcher.TryEnqueue(onComplete);

            // Wake enough distinct sleepers to service this batch concurrently. We cap wake
            // credits at WorkerCount: workers that finish early keep draining the shared queue.
            WakeWorkers(Math.Min(batches, workers.Length));
            return true;
        }

        private void WakeWorkers(int count)
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
                // Intentionally fail-open: workers also poll every 5 ms and queued work remains
                // intact. With maxPending bounded and int.MaxValue semaphore capacity this is
                // defensive only.
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
                WorkItem item;
                if (!TryTake(out item))
                {
                    wakeSignal.Wait(5);
                    continue;
                }

                int active = Interlocked.Increment(ref activeWorkers);
                UpdatePeakActive(active);
                try
                {
                    item.Action();
                    Interlocked.Increment(ref completed);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    CircuitBreaker.RecordFailure(item.FeatureId, ex);
                    Log.Error("[RimMT] Worker exception in feature '" + item.FeatureId + "' on " + Thread.CurrentThread.Name + ": " + ex);
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                    Interlocked.Decrement(ref pending);
                }
            }
        }

        private bool TryTake(out WorkItem item)
        {
            if (high.TryDequeue(out item)) return true;
            if (normal.TryDequeue(out item)) return true;
            if ((!FeatureGate.IsEnabled("runtime.adaptiveBurst") || AdaptiveLoadBalancer.AllowBackground) && background.TryDequeue(out item)) return true;
            item = null;
            return false;
        }

        private void UpdateHighWater(int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref highWaterPending)))
            {
                if (Interlocked.CompareExchange(ref highWaterPending, value, observed) == observed)
                    break;
            }
        }

        private void UpdatePeakActive(int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref peakActiveWorkers)))
            {
                if (Interlocked.CompareExchange(ref peakActiveWorkers, value, observed) == observed)
                    break;
            }
        }

        private sealed class WorkItem
        {
            internal readonly string FeatureId;
            internal readonly Action Action;
            internal WorkItem(string featureId, Action action)
            {
                FeatureId = featureId;
                Action = action;
            }
        }
    }
}
