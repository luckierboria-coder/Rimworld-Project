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

        public static bool TryEnqueue(Action action)
        {
            if (action == null) return false;

            if (RimMTThreadGuard.IsMainThread)
            {
                Run(action);
                return true;
            }

            int now = Interlocked.Increment(ref queued);
            if (now > MaxQueuedCallbacks)
            {
                Interlocked.Decrement(ref queued);
                return false;
            }

            Queue.Enqueue(action);
            return true;
        }

        internal static void Drain(int maxActions)
        {
            if (!RimMTThreadGuard.IsMainThread) return;

            int drained = 0;
            Action action;
            while (drained < maxActions && Queue.TryDequeue(out action))
            {
                Interlocked.Decrement(ref queued);
                Run(action);
                drained++;
            }
        }

        private static void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("[RimMT] Main-thread callback failed: " + ex); }
        }
    }
}
