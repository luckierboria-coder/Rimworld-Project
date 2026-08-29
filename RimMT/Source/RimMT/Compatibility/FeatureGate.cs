using System;
using System.Collections.Generic;
using System.Threading;

namespace RimMT
{
    public static class FeatureGate
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, FeatureState> States = new Dictionary<string, FeatureState>();

        // JR1.1 rolling ReachProfile safety needs a cheap per-thread way to make one live
        // Reachability.CanReach call bypass profile authority without mutating the persistent
        // feature setting or patching AggressiveReachabilityProfiles.Prefix itself.
        [ThreadStatic] private static int reachProfileForceDisableDepth;

        internal static void PushReachProfileForceDisable()
        {
            reachProfileForceDisableDepth++;
        }

        internal static void PopReachProfileForceDisable()
        {
            if (reachProfileForceDisableDepth > 0)
                reachProfileForceDisableDepth--;
        }

        public static void Register(string id, bool enabledByDefault, string description)
        {
            lock (Sync)
            {
                if (!States.ContainsKey(id))
                    States.Add(id, new FeatureState(enabledByDefault, description));
            }
        }

        public static void SetEnabled(string id, bool enabled)
        {
            lock (Sync)
            {
                FeatureState state;
                if (!States.TryGetValue(id, out state))
                {
                    state = new FeatureState(enabled, string.Empty);
                    States.Add(id, state);
                }
                state.Enabled = enabled;
            }
        }

        public static bool IsEnabled(string id)
        {
            if (reachProfileForceDisableDepth > 0 &&
                string.Equals(id, AggressiveReachabilityProfiles.FeatureId, StringComparison.Ordinal))
                return false;

            lock (Sync)
            {
                FeatureState state;
                return States.TryGetValue(id, out state) && state.Enabled && !state.Suppressed;
            }
        }

        public static void Suppress(string id, string reason)
        {
            // JR1.1 replaces only the old ReachProfile lifetime-16 parity fuse. All other
            // suppressions (compatibility failures, circuit breakers and emergency hard fuse)
            // retain normal FeatureGate semantics.
            if (ReachProfileRollingFuse0419.InterceptLegacySuppress(id, reason))
                return;

            lock (Sync)
            {
                FeatureState state;
                if (!States.TryGetValue(id, out state))
                {
                    state = new FeatureState(false, string.Empty);
                    States.Add(id, state);
                }
                state.Suppressed = true;
                state.Reason = reason ?? string.Empty;
            }
        }

        internal static Dictionary<string, FeatureState> Snapshot()
        {
            lock (Sync)
                return new Dictionary<string, FeatureState>(States);
        }

        public sealed class FeatureState
        {
            public bool Enabled;
            public bool Suppressed;
            public string Description;
            public string Reason;

            internal FeatureState(bool enabled, string description)
            {
                Enabled = enabled;
                Description = description ?? string.Empty;
                Reason = string.Empty;
            }
        }
    }
}
