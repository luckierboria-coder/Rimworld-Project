using System;
using System.Text;
using System.Threading;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T15 coordinator. Slow DetermineNextJob observation happens only inside T2 deep windows.
    /// Harmony mutation is deferred to RimMT's existing TickManager frame boundary.
    /// Exactly one bounded detail session is allowed per runtime so diagnostic cost cannot become resident.
    /// </summary>
    internal static class WorkGiverDeepAttribution093T15
    {
        private const long TriggerUs = 20000L;
        private const int MaxStartAttempts = 3;

        private static long triggerCount;
        private static long maxTriggerUs;
        private static int requested;
        private static int captureStarted;
        private static int captureCompleted;
        private static int startAttempts;
        private static int startFailures;
        private static long startFrame = -1L;
        private static long completionFrame = -1L;
        private static int startTick = -1;
        private static int completionTick = -1;

        internal static void ObserveDetermine(long us)
        {
            if (us < TriggerUs || !RimMTThreadGuard.IsMainThread) return;
            triggerCount++;
            if (us > maxTriggerUs) maxTriggerUs = us;

            if (Volatile.Read(ref captureStarted) != 0 || WorkGiverDetailPatches.CaptureActive)
                return;
            // T24 production: retain the lightweight trigger counters only. The 438-method
            // detail Harmony burst is diagnostic-only and is no longer auto-requested.
            return;
        }

        internal static void OnMainThreadFrame()
        {
            if (!RimMTThreadGuard.IsMainThread) return;

            // Existing detail profiler removes its temporary Harmony detours only here.
            WorkGiverDetailPatches.OnMainThreadFrame();

            if (Volatile.Read(ref captureStarted) != 0)
            {
                if (Volatile.Read(ref captureCompleted) == 0 &&
                    !WorkGiverDetailPatches.CaptureActive && WorkGiverDetailPatches.PackagesRemaining == 0)
                {
                    Interlocked.Exchange(ref captureCompleted, 1);
                    completionFrame = RimMTRuntime.MainThreadFrames;
                    completionTick = CurrentTick();
                }
                return;
            }

            if (Interlocked.Exchange(ref requested, 0) == 0)
                return;

            int attempt = Interlocked.Increment(ref startAttempts);
            bool started = false;
            try { started = WorkGiverDetailPatches.StartCapture(); }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T15 deferred WorkGiver detail start failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (started)
            {
                Interlocked.Exchange(ref captureStarted, 1);
                startFrame = RimMTRuntime.MainThreadFrames;
                startTick = CurrentTick();
                Log.Message("[RimMT] T15 WorkGiver deep attribution burst started after sampled DetermineNextJob >=20ms. Temporary detail detours will auto-remove after the bounded package window.");
            }
            else
            {
                Interlocked.Increment(ref startFailures);
                if (attempt < MaxStartAttempts)
                    Log.Warning("[RimMT] T15 WorkGiver detail burst could not start; a later >=20ms sampled DetermineNextJob may retry. attempt=" + attempt + "/" + MaxStartAttempts + ".");
            }
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("T15 WorkGiver deep attribution coordinator: trigger>=20ms, triggers=").Append(triggerCount)
              .Append(", maxTriggerMs=").Append((maxTriggerUs / 1000.0).ToString("F2"))
              .Append(", autoCapture=OFF, requested=").Append(Volatile.Read(ref requested) != 0)
              .Append(", captureStarted=").Append(Volatile.Read(ref captureStarted) != 0)
              .Append(", captureActive=").Append(WorkGiverDetailPatches.CaptureActive)
              .Append(", packagesRemaining=").Append(WorkGiverDetailPatches.PackagesRemaining)
              .Append(", captureCompleted=").Append(Volatile.Read(ref captureCompleted) != 0)
              .Append(", startAttempts=").Append(startAttempts)
              .Append(", startFailures=").Append(startFailures)
              .Append(", startFrame/tick=").Append(startFrame).Append('/').Append(startTick)
              .Append(", completionFrame/tick=").Append(completionFrame).Append('/').Append(completionTick)
              .Append(". Trigger uses T2's existing sampled DetermineNextJob Stopwatch; Harmony mutation is deferred to the frame boundary; one bounded session per runtime; measurement-only.");
            return sb.ToString();
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }
    }
}

