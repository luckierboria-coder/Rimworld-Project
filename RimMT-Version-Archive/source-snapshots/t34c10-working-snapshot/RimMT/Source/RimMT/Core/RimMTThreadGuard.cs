using System;
using System.Threading;

namespace RimMT
{
    public static class RimMTThreadGuard
    {
        private static int mainThreadId = -1;

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;

        internal static void InitializeMainThread()
        {
            if (mainThreadId < 0)
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public static void AssertMainThread(string operation)
        {
            if (!IsMainThread)
                throw new InvalidOperationException("RimMT main-thread-only operation called from worker thread: " + operation);
        }

        public static void AssertWorkerThread(string operation)
        {
            if (IsMainThread)
                throw new InvalidOperationException("RimMT worker-only operation called from main thread: " + operation);
        }
    }
}
