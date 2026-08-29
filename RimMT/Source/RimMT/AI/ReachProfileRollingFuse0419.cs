using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // Long-run ReachProfile safety controller for V0.4.19-JR1.
    // Replaces the old lifetime-accumulation semantics without changing the prediction engine.
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
        private static long forcedLiveProbation;
        private static long legacyFuseIntercepts;

        [ThreadStatic] private static bool forcedProbationPending;
        [ThreadStatic] private static bool forcedProbationPredicted;

        private static FieldInfo legacyMismatchField;
        private static bool installed;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null || installed)
                return;

            try
            {
                MethodBase profilePrefix = AccessTools.Method(typeof(AggressiveReachabilityProfiles), nameof(AggressiveReachabilityProfiles.Prefix));
                MethodBase profilePostfix = AccessTools.Method(typeof(AggressiveReachabilityProfiles), nameof(AggressiveReachabilityProfiles.Postfix));
                MethodBase reachability = AccessTools.Method(
                    typeof(Reachability),
                    nameof(Reachability.CanReach),
                    new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });

                if (profilePrefix == null || profilePostfix == null || reachability == null)
                {
                    Log.Warning("[RimMT] ReachProfile rolling fuse unavailable: required method not found.");
                    return;
                }

                HarmonyMethod prefixPostfix = new HarmonyMethod(typeof(ReachProfileRollingFuse0419), nameof(ProfilePrefixPostfix));
                prefixPostfix.priority = Priority.Last;
                harmony.Patch(profilePrefix, postfix: prefixPostfix);

                HarmonyMethod samplePostfix = new HarmonyMethod(typeof(ReachProfileRollingFuse0419), nameof(ProfileSamplePostfix));
                samplePostfix.priority = Priority.Last;
                harmony.Patch(profilePostfix, postfix: samplePostfix);

                HarmonyMethod finalReachPostfix = new HarmonyMethod(typeof(ReachProfileRollingFuse0419), nameof(ReachabilityFinalPostfix));
                finalReachPostfix.priority = Priority.Last;
                harmony.Patch(reachability, postfix: finalReachPostfix);

                legacyMismatchField = AccessTools.Field(typeof(AggressiveReachabilityProfiles), "parityMismatches");
                installed = true;
                Log.Message("[RimMT] ReachProfile rolling fuse active: window=8192/limit=8, softCooldown=3600 frames, probation=256 clean live validations, emergency hard fuse=16/256. Legacy lifetime-16 suppression is intercepted.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] ReachProfile rolling fuse install failed; legacy ReachProfile safety remains. " + ex.GetType().Name + ": " + ex.Message);
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
            catch { }

            Log.Warning("[RimMT] ReachProfile legacy lifetime mismatch fuse intercepted; rolling JR1 safety remains in control and ReachProfile is not permanently disabled.");
            return true;
        }

        // This patches AggressiveReachabilityProfiles.Prefix itself. Its object[] argument 6 is
        // the ref bool CanReach result and its method return value says whether Vanilla should run.
        public static void ProfilePrefixPostfix(object[] __args, ref bool __result)
        {
            forcedProbationPending = false;
            UpdateMode();

            if (mode == FuseMode.Normal || mode == FuseMode.HardFused || __args == null || __args.Length < 7)
                return;

            // Aggressive Prefix returning false means it intended to provide an authoritative
            // result (including cheap immediate success). During cooldown/probation, force the
            // live fully-patched CanReach path instead.
            if (!__result)
            {
                bool predicted;
                try { predicted = Convert.ToBoolean(__args[6]); }
                catch { return; }

                __result = true;
                if (mode == FuseMode.Cooldown)
                {
                    forcedLiveCooldown++;
                }
                else if (mode == FuseMode.Probation)
                {
                    forcedLiveProbation++;
                    forcedProbationPending = true;
                    forcedProbationPredicted = predicted;
                }
            }
        }

        // Existing ReachProfile shadow samples flow through here after its own Postfix has
        // compared prediction with live Vanilla.
        public static void ProfileSamplePostfix(bool __0, AggressiveReachabilityProfiles.ReachSampleState __1)
        {
            if (!installed || !__1.Active)
                return;

            bool mismatch = __0 != __1.Predicted;
            ObserveSample(mismatch);
        }

        // Handles authority predictions that JR1 forced live specifically for probation.
        public static void ReachabilityFinalPostfix(bool __result)
        {
            if (!forcedProbationPending)
                return;

            bool mismatch = __result != forcedProbationPredicted;
            forcedProbationPending = false;
            ObserveSample(mismatch);
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
                    "JR1 emergency ReachProfile hard fuse: " + emergencyMismatches + "/" + emergencyCount + " sampled mismatches");
                Log.Warning("[RimMT] ReachProfile HARD FUSE: " + emergencyMismatches + "/" + emergencyCount + " mismatches in the emergency window. Vanilla Reachability is authoritative for the rest of this run.");
                return;
            }

            if (mode == FuseMode.Probation)
            {
                if (mismatch)
                {
                    probationFailures++;
                    EnterCooldown("probation mismatch");
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
                    Log.Message("[RimMT] ReachProfile probation passed 256 live validations; profile authority restored.");
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
            Log.Message("[RimMT] ReachProfile soft cooldown ended; entering 256-sample live probation before authority resumes.");
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
            Log.Warning("[RimMT] ReachProfile SOFT FUSE: " + reason + ". Profile authority is forced live for 3600 frames, then 256 clean probation validations are required.");
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
            return "ReachProfile rolling fuse JR1: mode=" + mode +
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
                ", forcedLive(cooldown/probation)=" + forcedLiveCooldown + "/" + forcedLiveProbation +
                ", legacyFuseIntercepts=" + legacyFuseIntercepts +
                ". Lifetime-accumulation fuse is replaced by rolling density + recovery; emergency high-density mismatch still disables authority for the run.";
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
