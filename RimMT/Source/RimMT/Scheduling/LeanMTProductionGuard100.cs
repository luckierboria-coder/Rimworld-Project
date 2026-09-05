using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// V0.10.0 route-correction guard.
    ///
    /// RimMT's production rule is now explicit: already-published worker results may keep serving
    /// hot paths, but new main-thread snapshot/capture work must never form a burst while the game
    /// is already under pressure. This guard does not change ReachProfile authority or parity
    /// semantics. It only caps how many new profile captures may enter EnsureProfileScheduled in
    /// one main-thread frame.
    ///
    /// Low:      up to 4 new captures / frame
    /// Normal:   up to 2 new captures / frame
    /// High:     up to 1 new capture / frame
    /// Critical: 0 new captures; existing published profiles remain usable and Vanilla handles misses
    /// </summary>
    internal static class LeanMTProductionGuard100
    {
        private static bool reachProfileGuardPatched;
        private static long frame = -1L;
        private static int admittedThisFrame;

        private static long admittedLow;
        private static long admittedNormal;
        private static long admittedHigh;
        private static long deferredLow;
        private static long deferredNormal;
        private static long deferredHigh;
        private static long deferredCritical;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(AggressiveReachabilityProfilesV17), "EnsureProfileScheduled");
                if (target == null)
                {
                    Log.Warning("[RimMT] Lean MT ReachProfile capture guard unavailable: EnsureProfileScheduled not found.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(LeanMTProductionGuard100), nameof(ReachProfileSchedulePrefix));
                prefix.priority = Priority.First;
                harmony.Patch(target, prefix: prefix);
                reachProfileGuardPatched = true;
                Log.Message("[RimMT] V0.10.0 Lean MT capture guard active: ReachProfile new-capture caps Low=4, Normal=2, High=1, Critical=0 per main-thread frame. Published profiles remain usable.");
            }
            catch (Exception ex)
            {
                reachProfileGuardPatched = false;
                Log.Warning("[RimMT] Lean MT capture guard failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool ReachProfileSchedulePrefix()
        {
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            LoadPressure pressure = AdaptiveLoadBalancer.Pressure;
            if (pressure == LoadPressure.Critical)
            {
                Interlocked.Increment(ref deferredCritical);
                return false;
            }

            long now = RimMTRuntime.MainThreadFrames;
            if (frame != now)
            {
                frame = now;
                admittedThisFrame = 0;
            }

            int cap;
            switch (pressure)
            {
                case LoadPressure.Low: cap = 4; break;
                case LoadPressure.Normal: cap = 2; break;
                case LoadPressure.High: cap = 1; break;
                default: cap = 1; break;
            }

            if (admittedThisFrame >= cap)
            {
                switch (pressure)
                {
                    case LoadPressure.Low: Interlocked.Increment(ref deferredLow); break;
                    case LoadPressure.Normal: Interlocked.Increment(ref deferredNormal); break;
                    case LoadPressure.High: Interlocked.Increment(ref deferredHigh); break;
                }
                return false;
            }

            admittedThisFrame++;
            switch (pressure)
            {
                case LoadPressure.Low: Interlocked.Increment(ref admittedLow); break;
                case LoadPressure.Normal: Interlocked.Increment(ref admittedNormal); break;
                case LoadPressure.High: Interlocked.Increment(ref admittedHigh); break;
            }
            return true;
        }

        internal static string Summary()
        {
            return "Lean MT capture guard: patched=" + reachProfileGuardPatched +
                ", admitted=[Low=" + admittedLow + ",Normal=" + admittedNormal + ",High=" + admittedHigh + "]" +
                ", deferred=[Low=" + deferredLow + ",Normal=" + deferredNormal + ",High=" + deferredHigh + ",Critical=" + deferredCritical + "]" +
                ", currentFrameAdmissions=" + admittedThisFrame + ".";
        }
    }
}
