using System;
using System.Threading;

namespace RimMT2
{
    internal static class MainThreadGuard
    {
        private static int mainThreadId;

        internal static int MainThreadId => Volatile.Read(ref mainThreadId);

        internal static bool IsCaptured => MainThreadId != 0;

        internal static bool IsMainThread => IsCaptured && Thread.CurrentThread.ManagedThreadId == MainThreadId;

        internal static void CaptureFromUnityUpdate()
        {
            int currentThreadId = Thread.CurrentThread.ManagedThreadId;
            int capturedThreadId = Interlocked.CompareExchange(ref mainThreadId, currentThreadId, 0);
            if (capturedThreadId != 0 && capturedThreadId != currentThreadId)
            {
                throw new InvalidOperationException("RimMT 2.0 observed Root.Update on a different managed thread.");
            }
        }

        internal static void RequireMainThread(string operation)
        {
            if (!IsMainThread)
            {
                throw new InvalidOperationException("RimMT 2.0 main-thread-only operation reached a non-Unity thread: " + operation);
            }
        }
    }
}
