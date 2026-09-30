using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.18 safety tightening driven by the 15-minute V0.4.17.2 runtime trace:
    // 162,378 sampled reachability predictions produced eight predTrue/liveFalse mismatches
    // and zero predFalse/liveTrue mismatches. Until positive parity reaches a much larger
    // zero-mismatch sample, only proven-unreachable profile results are allowed to skip
    // Vanilla. Cheap ReachabilityImmediate true results are still preserved.
    internal static class ReachProfileSafety0418
    {
        private static long positiveProfileResultsForcedToVanilla;
        private static long immediateTruePreserved;
        private static long immediateProbeFailures;
        private static long patchFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodBase target = AccessTools.Method(typeof(AggressiveReachabilityProfiles), nameof(AggressiveReachabilityProfiles.Prefix));
                if (target == null)
                {
                    Interlocked.Increment(ref patchFailures);
                    Log.Warning("[RimMT] V0.4.18 reach-profile positive-authority guard unavailable: AggressiveReachabilityProfiles.Prefix not found.");
                    return;
                }

                HarmonyMethod postfix = new HarmonyMethod(typeof(ReachProfileSafety0418), nameof(PrefixPostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(target, postfix: postfix);

                Log.Message("[RimMT] V0.4.18 reach-profile safety guard active: profile Unreachable may remain authoritative; non-immediate profile Reachable is forced through live Vanilla CanReach confirmation.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] V0.4.18 reach-profile positive-authority guard patch failed. Existing V0.4.16 parity fuse remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // This patches RimMT's own AggressiveReachabilityProfiles.Prefix method, not
        // Reachability.CanReach directly. The original Prefix return value is __result.
        // Argument __6 is the ref bool result that the Prefix intends to supply to CanReach.
        // If the profile is about to short-circuit CanReach with true, make the Prefix return
        // true instead so Harmony executes live Vanilla CanReach. Authoritative false is kept.
        public static void PrefixPostfix(
            IntVec3 __0,
            LocalTargetInfo __1,
            PathEndMode __2,
            TraverseParms __3,
            Map __4,
            ref bool __6,
            ref bool __result)
        {
            // true means AggressiveReachabilityProfiles already chose Vanilla execution.
            if (__result)
                return;

            // Keep proven-unreachable authority. The observed V0.4.17.2 trace had no
            // predFalse/liveTrue mismatches, and this is the safe hard-negative direction.
            if (!__6)
                return;

            // Preserve the exact cheap immediate-reachability shortcut. It is not a profile
            // prediction and therefore is not implicated by the observed positive mismatches.
            try
            {
                Pawn pawn = __3.pawn;
                if (__4 != null && !__4.Disposed &&
                    ReachabilityImmediate.CanReachImmediate(__0, __1, __4, __2, pawn))
                {
                    Interlocked.Increment(ref immediateTruePreserved);
                    return;
                }
            }
            catch
            {
                // A failed immediate probe must fail toward Vanilla, never toward an
                // authoritative positive result.
                Interlocked.Increment(ref immediateProbeFailures);
            }

            __result = true;
            Interlocked.Increment(ref positiveProfileResultsForcedToVanilla);
        }

        internal static string Summary()
        {
            return "Reach-profile V0.4.18 positive guard: forcedVanillaPositive=" +
                Interlocked.Read(ref positiveProfileResultsForcedToVanilla) +
                ", immediateTruePreserved=" + Interlocked.Read(ref immediateTruePreserved) +
                ", immediateProbeFailures=" + Interlocked.Read(ref immediateProbeFailures) +
                ", patchFailures=" + Interlocked.Read(ref patchFailures) +
                ". Policy: profile false may short-circuit; profile true requires live Vanilla confirmation.";
        }
    }
}
