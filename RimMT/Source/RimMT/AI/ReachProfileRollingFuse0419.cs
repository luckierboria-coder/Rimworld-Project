using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JR1.1 long-run ReachProfile safety controller.
    //
    // JR1 tried to patch AggressiveReachabilityProfiles.Prefix itself. Mono/Harmony produced an
    // InvalidProgramException for that self-patched dynamic wrapper. JR1.1 therefore never
    // patches the profile Prefix/Postfix methods.
    //
    // Instead, this controller brackets the outer Reachability.CanReach call. It reads the
    // existing ReachProfile shadow-sample and mismatch counters before/after the call, so it can
    // feed the rolling windows without touching prediction internals. During a soft cooldown a
    // ThreadStatic FeatureGate override makes AggressiveReachabilityProfiles defer that call to
    // the live fully-patched Reachability path. After cooldown, probation uses the profile's
    // normal shadow samples; 256 clean samples restore Normal mode.
    internal static class ReachProfileRollingFuse0419
    {
        private const int GlobalWindowSamples = 8192;
        private const int GlobalMismatchLimit = 8;
        private const long GlobalCooldownFrames = 3600;
        private const int ProbationSamples = 256;
        private const int EmergencyWindowSamples = 256;
        private const int EmergencyMismatchLimit = 16;

        private static readonly bool[] GlobalWindow = new bool[GlobalWindowSamples];
        private static readonly bool[] EmergencyWindow = new bool[EmergencyWindowSamples];

        private static int globalPos;
        private static int globalCount;
        private static int globalMismatches;
        private static int emergencyPos;
        private static int emergencyCount;
        private static int emergencyMismatches;

        private static FuseMode mode;
        private static long cooldownUntilFrame;
        private static int probationRemaining;
        private static int probationMatches;
        private static long softFuses;
        private static long probationPasses;
        private static long probationFailures;
        private static long hardFuses;
        private static long totalSamples;
        private static long totalMismatches;
        private static long forcedLiveCooldown;
        private static long legacyFuseIntercepts;
        private static long counterReadFailures;

        private static FieldInfo legacyMismatchField;
        private static FieldInfo shadowSamplesField;
        private static FieldInfo mismatchReachableToFalseField;
        private static FieldInfo mismatchUnreachableToTrueField;
        private static bool installed;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null || installed)
                return;

            try
            {
                MethodBase reachability = AccessTools.Method(
                    typeof(Reachability),
                    nameof(Reachability.CanReach),
                    new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });

                if (reachability == null)
                {
                    Log.Warning("[RimMT] ReachProfile rolling fuse JR1.1 unavailable: Reachability.CanReach not found.");
                    return;
                }

                legacyMismatchField = AccessTools.Field(typeof(AggressiveReachabilityProfiles), "parityMismatches");
                shadowSamplesField = AccessTools.Field(typeof(AggressiveReachabilityProfiles), "shadowSamples");
                mismatchReachableToFalseField = AccessTools.Field(typeof(AggressiveReachabilityProfiles), "mismatchReachableToFalse");
                mismatchUnreachableToTrueField = AccessTools.Field(typeof(AggressiveReachabilityProfiles), "mismatchUnreachableToTrue");

                if (legacyMismatchField == null || shadowSamplesField == null ||
                    mismatchReachableToFalseField == null || mismatchUnreachableToTrueField == null)
                {
                    Log.Warning("[RimMT] ReachProfile rolling fuse JR1.1 unavailable: ReachProfile telemetry fields not found.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(ReachProfileRollingFuse0419), nameof(ReachabilityPrefix));
                prefix.priority = Priority.First;
                HarmonyMethod postfix = new HarmonyMethod(typeof(ReachProfileRollingFuse0419), nameof(ReachabilityPostfix));
                postfix.priority = Priority.Last;
                HarmonyMethod finalizer = new HarmonyMethod(typeof(ReachProfileRollingFuse0419), nameof(ReachabilityFinalizer));
                finalizer.priority = Priority.Last;
                harmony.Patch(reachability, prefix: prefix, postfix: postfix, finalizer: finalizer);

                installed = true;
                Log.Message("[RimMT] ReachProfile rolling fuse JR1.1 active without self-patching profile methods: rolling window=8192/8, soft cooldown=3600 frames, probation=256 clean native shadow samples, emergency hard fuse=16/256. Legacy lifetime-16 suppression is intercepted.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] ReachProfile rolling fuse JR1.1 install failed; legacy ReachProfile safety remains. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Called by FeatureGate.Suppress before it commits suppression.
        internal static bool InterceptLegacySuppress(string id, string reason)
        {
            if (!installed || !string.Equals(id, AggressiveReachabilityProfiles.FeatureId, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(reason) || reason.IndexOf("reachability parity fuse", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            legacyFuseIntercepts++;
            try
            {
                if (legacyMismatchField != null)
                    legacyMismatchField.SetValue(null, 0L);
            }
            catch
            {
                counterReadFailures++;
            }

            Log.Warning("[RimMT] ReachProfile legacy lifetime mismatch fuse intercepted; rolling JR1.1 safety remains in control and ReachProfile is not permanently disabled.");
            return true;
        }

        public static void ReachabilityPrefix(ref ReachCallState __state)
        {
            __state = default(ReachCallState);
            if (!installed || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            UpdateMode();

            try
            {
                __state.ShadowBefore = ReadLong(shadowSamplesField);
                __state.MismatchBefore = ReadMismatchTotal();
                __state.Counted = true;
            }
            catch
            {
                counterReadFailures++;
            }

            if (mode == FuseMode.Cooldown && FeatureGate.IsEnabled(AggressiveReachabilityProfiles.FeatureId))
            {
                FeatureGate.PushReachProfileForceDisable();
                __state.ForcedLive = true;
                forcedLiveCooldown++;
            }
        }

        public static void ReachabilityPostfix(ReachCallState __state)
        {
            FinishCall(__state);
        }

        public static Exception ReachabilityFinalizer(Exception __exception, ReachCallState __state)
        {
            // Harmony runs finalizers even if the original/prefix chain throws. Pop the temporary
            // gate here as a second safety path. FinishCall is idempotent for the gate via depth.
            if (__state.ForcedLive)
                FeatureGate.PopReachProfileForceDisable();
            return __exception;
        }

        private static void FinishCall(ReachCallState state)
        {
            if (state.ForcedLive)
                FeatureGate.PopReachProfileForceDisable();

            if (!state.Counted || mode == FuseMode.Cooldown || mode == FuseMode.HardFused)
                return;

            try
            {
                long shadowAfter = ReadLong(shadowSamplesField);
                if (shadowAfter <= state.ShadowBefore)
                    return;

                long mismatchAfter = ReadMismatchTotal();
                long sampleDelta = shadowAfter - state.ShadowBefore;
                long mismatchDelta = mismatchAfter - state.MismatchBefore;
                if (mismatchDelta < 0) mismatchDelta = 0;

                // A single CanReach call should add at most one native ReachProfile sample, but
                // process deltas defensively in case another patch causes nested samples.
                for (long i = 0; i < sampleDelta; i++)
                    ObserveSample(i < mismatchDelta);
            }
            catch
            {
                counterReadFailures++;
            }
        }

        private static long ReadLong(FieldInfo field)
        {
            object value = field.GetValue(null);
            return value == null ? 0L : Convert.ToInt64(value);
        }

        private static long ReadMismatchTotal()
        {
            return ReadLong(mismatchReachableToFalseField) + ReadLong(mismatchUnreachableToTrueField);
        }

        private static void ObserveSample(bool mismatch)
        {
            totalSamples++;
            if (mismatch)
                totalMismatches++;

            if (mode == FuseMode.HardFused)
                return;

            PushWindow(GlobalWindow, ref globalPos, ref globalCount, ref globalMismatches, mismatch);
            PushWindow(EmergencyWindow, ref emergencyPos, ref emergencyCount, ref emergencyMismatches, mismatch);

            if (emergencyCount >= EmergencyWindowSamples && emergencyMismatches >= EmergencyMismatchLimit)
            {
                mode = FuseMode.HardFused;
                hardFuses++;
                FeatureGate.Suppress(AggressiveReachabilityProfiles.FeatureId,
                    "JR1.1 emergency ReachProfile hard fuse: " + emergencyMismatches + "/" + emergencyCount + " sampled mismatches");
                Log.Warning("[RimMT] ReachProfile HARD FUSE JR1.1: " + emergencyMismatches + "/" + emergencyCount + " mismatches in the emergency window. Vanilla Reachability is authoritative for the rest of this run.");
                return;
            }

            if (mode == FuseMode.Probation)
            {
                if (mismatch)
                {
                    probationFailures++;
                    EnterCooldown("probation shadow mismatch");
                    return;
                }

                probationMatches++;
                if (probationRemaining > 0)
                    probationRemaining--;
                if (probationRemaining <= 0)
                {
                    mode = FuseMode.Normal;
                    probationPasses++;
                    ClearGlobalWindow();
                    Log.Message("[RimMT] ReachProfile JR1.1 probation passed 256 clean native shadow samples; normal rolling mode restored.");
                }
                return;
            }

            if (mode == FuseMode.Normal && globalCount >= GlobalWindowSamples && globalMismatches >= GlobalMismatchLimit)
                EnterCooldown("rolling mismatch density " + globalMismatches + "/" + globalCount);
        }

        private static void UpdateMode()
        {
            if (mode != FuseMode.Cooldown)
                return;
            if (RimMTRuntime.MainThreadFrames < cooldownUntilFrame)
                return;

            mode = FuseMode.Probation;
            probationRemaining = ProbationSamples;
            probationMatches = 0;
            ClearGlobalWindow();
            Log.Message("[RimMT] ReachProfile JR1.1 soft cooldown ended; entering 256-clean-shadow-sample probation.");
        }

        private static void EnterCooldown(string reason)
        {
            mode = FuseMode.Cooldown;
            cooldownUntilFrame = RimMTRuntime.MainThreadFrames + GlobalCooldownFrames;
            probationRemaining = 0;
            probationMatches = 0;
            softFuses++;
            ClearGlobalWindow();
            ClearEmergencyWindow();
            Log.Warning("[RimMT] ReachProfile SOFT FUSE JR1.1: " + reason + ". Profile authority is bypassed for 3600 main-thread frames, then 256 clean native shadow samples are required.");
        }

        private static void PushWindow(bool[] window, ref int pos, ref int count, ref int mismatchCount, bool mismatch)
        {
            if (count < window.Length)
            {
                window[pos] = mismatch;
                if (mismatch) mismatchCount++;
                count++;
                pos = (pos + 1) % window.Length;
                return;
            }

            if (window[pos]) mismatchCount--;
            window[pos] = mismatch;
            if (mismatch) mismatchCount++;
            pos = (pos + 1) % window.Length;
        }

        private static void ClearGlobalWindow()
        {
            Array.Clear(GlobalWindow, 0, GlobalWindow.Length);
            globalPos = 0;
            globalCount = 0;
            globalMismatches = 0;
        }

        private static void ClearEmergencyWindow()
        {
            Array.Clear(EmergencyWindow, 0, EmergencyWindow.Length);
            emergencyPos = 0;
            emergencyCount = 0;
            emergencyMismatches = 0;
        }

        internal static string Summary()
        {
            return "ReachProfile rolling fuse JR1.1: installed=" + installed +
                ", mode=" + mode +
                ", totalSamples=" + totalSamples +
                ", totalMismatches=" + totalMismatches +
                ", globalWindow=" + globalMismatches + "/" + globalCount +
                ", emergencyWindow=" + emergencyMismatches + "/" + emergencyCount +
                ", softFuses=" + softFuses +
                ", cooldownUntilFrame=" + cooldownUntilFrame +
                ", probationRemaining=" + probationRemaining +
                ", probationMatches=" + probationMatches +
                ", probationPasses=" + probationPasses +
                ", probationFailures=" + probationFailures +
                ", hardFuses=" + hardFuses +
                ", forcedLiveCooldown=" + forcedLiveCooldown +
                ", legacyFuseIntercepts=" + legacyFuseIntercepts +
                ", counterReadFailures=" + counterReadFailures +
                ". No self-patching of AggressiveReachabilityProfiles methods; cooldown uses a temporary per-call feature gate and probation uses native shadow samples.";
        }

        internal struct ReachCallState
        {
            internal bool Counted;
            internal bool ForcedLive;
            internal long ShadowBefore;
            internal long MismatchBefore;
        }

        private enum FuseMode
        {
            Normal,
            Cooldown,
            Probation,
            HardFused
        }
    }
}
