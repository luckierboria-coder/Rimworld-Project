using System.Collections.Generic;

namespace RimMT
{
    public static class FeatureGate
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, FeatureState> States = new Dictionary<string, FeatureState>();

        public static void Register(string id, bool enabledByDefault, string description)
        {
            lock (Sync)
            {
                if (!States.ContainsKey(id))
                    States.Add(id, new FeatureState(enabledByDefault, description));
            }
        }

        public static bool IsEnabled(string id)
        {
            lock (Sync)
            {
                FeatureState state;
                return States.TryGetValue(id, out state) && state.Enabled && !state.Suppressed;
            }
        }

        public static void Suppress(string id, string reason)
        {
            lock (Sync)
            {
                FeatureState state;
                if (!States.TryGetValue(id, out state))
                {
                    state = new FeatureState(false, string.Empty);
                    States.Add(id, state);
                }
                state.Suppressed = true;
                state.Reason = reason;
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
