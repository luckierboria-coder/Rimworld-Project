using System.Threading;

namespace RimMT2
{
    internal static class FabricMetrics
    {
        internal static void Submit()
        {
            Interlocked.Increment(ref Submitted);
        }

        internal static void Reject(ISnapshotWorkItem item)
        {
            Interlocked.Increment(ref QueueRejected);
            item?.OnQueueRejected();
        }

        internal static long Submitted;
        internal static long QueueRejected;
        internal static long WorkerCompleted;
        internal static long WorkerFailed;
        internal static long CommitSucceeded;
        internal static long CommitDiscarded;
        internal static long CommitFailed;
        internal static long WorkerTicks;
        internal static long CommitTicks;

        internal static void Reset()
        {
            Interlocked.Exchange(ref Submitted, 0);
            Interlocked.Exchange(ref QueueRejected, 0);
            Interlocked.Exchange(ref WorkerCompleted, 0);
            Interlocked.Exchange(ref WorkerFailed, 0);
            Interlocked.Exchange(ref CommitSucceeded, 0);
            Interlocked.Exchange(ref CommitDiscarded, 0);
            Interlocked.Exchange(ref CommitFailed, 0);
            Interlocked.Exchange(ref WorkerTicks, 0);
            Interlocked.Exchange(ref CommitTicks, 0);
        }
    }
}
