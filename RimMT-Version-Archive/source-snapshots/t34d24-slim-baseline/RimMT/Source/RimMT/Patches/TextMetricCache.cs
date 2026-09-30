using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimMT
{
    internal static class TextMetricCache
    {
        private const int MaxEntries = 8192;
        private static readonly object Sync = new object();
        private static readonly Dictionary<HeightKey, float> Heights = new Dictionary<HeightKey, float>();
        private static readonly Dictionary<SizeKey, Vector2> Sizes = new Dictionary<SizeKey, Vector2>();
        private static long hits;
        private static long misses;

        internal static long Hits { get { lock (Sync) return hits; } }
        internal static long Misses { get { lock (Sync) return misses; } }

        public static bool CalcHeightPrefix(string text, float width, ref float __result, ref bool __state)
        {
            __state = false;
            if (!FeatureGate.IsEnabled("ui.textCache")) return true;

            HeightKey key = new HeightKey(text, width, Text.Font, Text.WordWrap);
            lock (Sync)
            {
                if (Heights.TryGetValue(key, out __result))
                {
                    hits++;
                    __state = true;
                    return false;
                }
                misses++;
            }
            return true;
        }

        public static void CalcHeightPostfix(string text, float width, float __result, bool __state)
        {
            if (__state || !FeatureGate.IsEnabled("ui.textCache")) return;
            HeightKey key = new HeightKey(text, width, Text.Font, Text.WordWrap);
            lock (Sync)
            {
                if (Heights.Count >= MaxEntries) Heights.Clear();
                Heights[key] = __result;
            }
        }

        public static bool CalcSizePrefix(string text, ref Vector2 __result, ref bool __state)
        {
            __state = false;
            if (!FeatureGate.IsEnabled("ui.textCache")) return true;

            SizeKey key = new SizeKey(text, Text.Font);
            lock (Sync)
            {
                if (Sizes.TryGetValue(key, out __result))
                {
                    hits++;
                    __state = true;
                    return false;
                }
                misses++;
            }
            return true;
        }

        public static void CalcSizePostfix(string text, Vector2 __result, bool __state)
        {
            if (__state || !FeatureGate.IsEnabled("ui.textCache")) return;
            SizeKey key = new SizeKey(text, Text.Font);
            lock (Sync)
            {
                if (Sizes.Count >= MaxEntries) Sizes.Clear();
                Sizes[key] = __result;
            }
        }

        private struct HeightKey : IEquatable<HeightKey>
        {
            private readonly string text;
            private readonly float width;
            private readonly GameFont font;
            private readonly bool wordWrap;

            internal HeightKey(string text, float width, GameFont font, bool wordWrap)
            {
                this.text = text ?? string.Empty;
                this.width = width;
                this.font = font;
                this.wordWrap = wordWrap;
            }

            public bool Equals(HeightKey other)
            {
                return width.Equals(other.width) && font == other.font && wordWrap == other.wordWrap && string.Equals(text, other.text, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) { return obj is HeightKey && Equals((HeightKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = text.GetHashCode();
                    hash = hash * 397 ^ width.GetHashCode();
                    hash = hash * 397 ^ (int)font;
                    hash = hash * 397 ^ wordWrap.GetHashCode();
                    return hash;
                }
            }
        }

        private struct SizeKey : IEquatable<SizeKey>
        {
            private readonly string text;
            private readonly GameFont font;

            internal SizeKey(string text, GameFont font)
            {
                this.text = text ?? string.Empty;
                this.font = font;
            }

            public bool Equals(SizeKey other)
            {
                return font == other.font && string.Equals(text, other.text, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) { return obj is SizeKey && Equals((SizeKey)obj); }
            public override int GetHashCode() { unchecked { return text.GetHashCode() * 397 ^ (int)font; } }
        }
    }
}
