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
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly Thread[] workers;
        private readonly int maxPending;
        private volatile bool running = true;
        private int pending;

        public int WorkerCount => workers.Length;
        public int Pending => Volatile.Read(ref pending);

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
            if (!running || action == null || !FeatureGate.IsEnabled(featureId)) return false;
            if (CircuitBreaker.IsOpen(featureId)) return false;

            int now = Interlocked.Increment(ref pending);
            if (now > maxPending)
            {
                Interlocked.Decrement(ref pending);
                return false;
            }

            WorkItem item = new WorkItem(featureId, action);
            switch (priority)
            {
                case JobPriority.High: high.Enqueue(item); break;
                case JobPriority.Background: background.Enqueue(item); break;
                default: normal.Enqueue(item); break;
            }
            signal.Set();
            return true;
        }

        public bool ParallelFor(string featureId, int fromInclusive, int toExclusive, int batchSize,
            Action<int, int> body, Action onComplete = null, JobPriority priority = JobPriority.Normal)
        {
            if (body == null || toExclusive <= fromInclusive) return false;
            if (!FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId)) return false;
            if (batchSize <= 0) batchSize = 256;

            int count = toExclusive - fromInclusive;
            int batches = (count + batchSize - 1) / batchSize;
            int remaining = batches;
            int accepted = 0;
            int allQueued = 0;

            for (int start = fromInclusive; start < toExclusive; start += batchSize)
            {
                int s = start;
                int e = Math.Min(start + batchSize, toExclusive);
                bool queued = TryEnqueue(featureId, priority, () =>
                {
                    body(s, e);
                    if (Interlocked.Decrement(ref remaining) == 0
                        && Volatile.Read(ref allQueued) == 1
                        && onComplete != null)
                    {
                        MainThreadDispatcher.TryEnqueue(onComplete);
                    }
                });

                if (!queued)
                    break;
                accepted++;
            }

            if (accepted == batches)
            {
                Volatile.Write(ref allQueued, 1);
                if (Volatile.Read(ref remaining) == 0 && onComplete != null)
                    MainThreadDispatcher.TryEnqueue(onComplete);
                return true;
            }

            // Fail closed. A parallel feature must only use this API for isolated/pure work.
            // If every batch cannot be accepted, the caller must keep authoritative game state
            // on its vanilla/main-thread path and ignore any speculative partial result.
            return false;
        }

        private void WorkerLoop(int workerIndex)
        {
            while (running)
            {
                WorkItem item;
                if (!TryTake(out item))
                {
                    signal.WaitOne(5);
                    continue;
                }

                try
                {
                    item.Action();
                }
                catch (Exception ex)
                {
                    CircuitBreaker.RecordFailure(item.FeatureId, ex);
                    Log.Error("[RimMT] Worker exception in feature '" + item.FeatureId + "' on "
                        + Thread.CurrentThread.Name + ": " + ex);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }
        }

        private bool TryTake(out WorkItem item)
        {
            if (high.TryDequeue(out item)) return true;
            if (normal.TryDequeue(out item)) return true;
            if (background.TryDequeue(out item)) return true;
            return false;
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
