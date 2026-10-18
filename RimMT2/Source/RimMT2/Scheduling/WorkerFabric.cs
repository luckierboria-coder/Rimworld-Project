using System;
using System.Collections.Generic;
using System.Diagnostics;
using Gilzoide.ManagedJobs;
using Unity.Jobs;
using Verse;

namespace RimMT2
{
    internal sealed class WorkerFabric : IDisposable
    {
        private sealed class SnapshotJob : IJob
        {
            private readonly ISnapshotWorkItem item;

            internal SnapshotJob(ISnapshotWorkItem item)
            {
                this.item = item;
            }

            public void Execute()
            {
                item.ExecuteOnWorker();
            }
        }

        private struct ScheduledWork
        {
            internal ISnapshotWorkItem item;
            internal JobHandle handle;
        }

        private const int QueueCapacity = 128;
        private readonly List<ScheduledWork> scheduled = new List<ScheduledWork>();
        private bool needsFlush;
        private bool disposed;

        internal int WorkerCount => UnityData.MaxJobWorkerCount;
        internal int PendingWork => scheduled.Count;
        internal int PendingCommits
        {
            get
            {
                int count = 0;
                for (int i = 0; i < scheduled.Count; i++)
                {
                    if (scheduled[i].handle.IsCompleted)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        internal bool TrySubmit(ISnapshotWorkItem item)
        {
            JobHandle ignored;
            return TrySubmit(item, default(JobHandle), out ignored);
        }

        internal bool TrySubmit(ISnapshotWorkItem item, JobHandle dependsOn, out JobHandle handle)
        {
            MainThreadGuard.RequireMainThread("WorkerFabric.TrySubmit");
            handle = default(JobHandle);
            if (item == null || disposed || scheduled.Count >= QueueCapacity)
            {
                FabricMetrics.Reject(item);
                return false;
            }

            try
            {
                handle = new ManagedJob(new SnapshotJob(item)).Schedule(dependsOn);
                scheduled.Add(new ScheduledWork { item = item, handle = handle });
                needsFlush = true;
                FabricMetrics.Submit();
                return true;
            }
            catch (Exception exception)
            {
                Verse.Log.Error("[RimMT 2.0] Unity job scheduling failed for " + item.ModuleName + ": " + exception);
                FabricMetrics.Reject(item);
                return false;
            }
        }

        internal void Flush()
        {
            MainThreadGuard.RequireMainThread("WorkerFabric.Flush");
            if (needsFlush)
            {
                JobHandle.ScheduleBatchedJobs();
                needsFlush = false;
            }
        }

        internal int DrainCommits(double budgetMilliseconds)
        {
            MainThreadGuard.RequireMainThread("WorkerFabric.DrainCommits");
            if (scheduled.Count == 0)
            {
                return 0;
            }
            long started = Stopwatch.GetTimestamp();
            long budgetTicks = Math.Max(1L, (long)(budgetMilliseconds * Stopwatch.Frequency / 1000.0));
            int count = 0;
            for (int i = scheduled.Count - 1; i >= 0 && Stopwatch.GetTimestamp() - started < budgetTicks; i--)
            {
                ScheduledWork work = scheduled[i];
                if (!work.handle.IsCompleted)
                {
                    continue;
                }
                work.handle.Complete();
                scheduled.RemoveAt(i);
                work.item.CommitOnMainThread();
                count++;
            }
            return count;
        }

        public void Dispose()
        {
            MainThreadGuard.RequireMainThread("WorkerFabric.Dispose");
            if (disposed)
            {
                return;
            }
            disposed = true;
            Flush();
            for (int i = 0; i < scheduled.Count; i++)
            {
                scheduled[i].handle.Complete();
            }
            scheduled.Clear();
        }
    }
}
