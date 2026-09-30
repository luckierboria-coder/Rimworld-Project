using System;
using System.Collections.Concurrent;
using System.Threading;
using Verse;

namespace RimMT
{
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

        public static int Queued { get { return Volatile.Read(ref queued); } }
        public static int HighWater { get { return Volatile.Read(ref highWater); } }
        public static long Enqueued { get { return Interlocked.Read(ref enqueued); } }
        public static long InlineExecuted { get { return Interlocked.Read(ref inlineExecuted); } }
        public static long Drained { get { return Interlocked.Read(ref drained); } }
        public static long Rejected { get { return Interlocked.Read(ref rejected); } }
        public static long Failures { get { return Interlocked.Read(ref failures); } }
        public static long DrainCalls { get { return Interlocked.Read(ref drainCalls); } }
        public static long ButterLogicalTickDeferred { get { return Interlocked.Read(ref butterLogicalTickDeferred); } }
        public static long ButterProbeFailureDeferred { get { return Interlocked.Read(ref butterProbeFailureDeferred); } }

        public static bool TryEnqueue(Action action)
        {
            if (action == null) return false;

            if (RimMTThreadGuard.IsMainThread)
            {
                bool deferForButter = false;
                if (RuntimeCompatibility.ButterPlusPlusActive)
                {
                    bool logicalTickInProgress;
                    if (!RuntimeCompatibility.TryGetButterLogicalTickInProgress(out logicalTickInProgress))
                    {
                        deferForButter = true;
                        Interlocked.Increment(ref butterProbeFailureDeferred);
                    }
                    else if (logicalTickInProgress)
                    {
                        deferForButter = true;
                        Interlocked.Increment(ref butterLogicalTickDeferred);
                    }
                }

                if (!deferForButter)
                {
                    Interlocked.Increment(ref inlineExecuted);
                    Run(action);
                    return true;
                }

                // Being on the main thread is not enough when Butter++ has split one logical tick
                // across rendered frames. Queue the callback until TickManagerPatch._midTickStarted
                // becomes false. Probe-read failures are also fail-closed and remain queued.
            }

            int now = Interlocked.Increment(ref queued);
            if (now > MaxQueuedCallbacks)
            {
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref rejected);
                return false;
            }

            UpdateHighWater(now);
            Queue.Enqueue(action);
            Interlocked.Increment(ref enqueued);
            return true;
        }

        internal static void Drain(int maxActions)
        {
            if (!RimMTThreadGuard.IsMainThread) return;
            Interlocked.Increment(ref drainCalls);

            int count = 0;
            Action action;
            while (count < maxActions && Queue.TryDequeue(out action))
            {
                Interlocked.Decrement(ref queued);
                Run(action);
                count++;
            }
            if (count > 0)
                Interlocked.Add(ref drained, count);
        }

        internal static string Summary()
        {
            return "Dispatcher: queued=" + Queued +
                ", enqueued=" + Enqueued +
                ", drained=" + Drained +
                ", inline=" + InlineExecuted +
                ", butterLogicalTickDeferred=" + ButterLogicalTickDeferred +
                ", butterProbeFailureDeferred=" + ButterProbeFailureDeferred +
                ", rejected=" + Rejected +
                ", failures=" + Failures +
                ", drainCalls=" + DrainCalls +
                ", highWater=" + HighWater;
        }

        private static void UpdateHighWater(int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref highWater)))
            {
                if (Interlocked.CompareExchange(ref highWater, value, observed) == observed)
                    break;
            }
        }

        private static void Run(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Error("[RimMT] Main-thread callback failed: " + ex);
            }
        }
    }
}
