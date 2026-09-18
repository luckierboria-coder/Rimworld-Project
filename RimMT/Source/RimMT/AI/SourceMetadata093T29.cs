using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T29 package-local source metadata foundation.
    ///
    /// This layer never performs a ListerThings lookup and never initiates a source scan on a miss.
    /// A consumer that already had to scan an IList may publish the Thing reference + PositionHeld
    /// values it observed. Later consumers inside the same synchronous JobGiver_Work package can
    /// reuse that captured metadata after a bounded live fingerprint validation.
    ///
    /// No validator result, Reachability result, reservation, Job, priority, map mutation, or
    /// cross-package decision is cached here. Any uncertainty invalidates the entry and fails open.
    /// </summary>
    internal static class SourceMetadata093T29
    {
        internal const string FeatureId = "ai.jobSourceMetadata";

        private const int MaxSourcesPerPackage = 32;
        private const int MaxSourceCount = 8192;
        private const int FingerprintSamples = 8;

        private static readonly object StateKey = new object();

        private static long lookups;
        private static long hits;
        private static long misses;
        private static long publishes;
        private static long replacements;
        private static long validationChecks;
        private static long validationMismatches;
        private static long invalidations;
        private static long scopeBypass;
        private static long shapeBypass;
        private static long sizeBypass;
        private static long capacityBypass;
        private static long publishRejected;

        internal static bool TryGetValid(IList source, out ThingListMetadata metadata)
        {
            metadata = null;
            Interlocked.Increment(ref lookups);

            if (!RimMTThreadGuard.IsMainThread || !JobSearchPackageContext093T28.InScope)
            {
                Interlocked.Increment(ref scopeBypass);
                return false;
            }

            if (source == null)
            {
                Interlocked.Increment(ref shapeBypass);
                return false;
            }

            int count;
            try { count = source.Count; }
            catch
            {
                Interlocked.Increment(ref shapeBypass);
                return false;
            }

            if (count < 0 || count > MaxSourceCount)
            {
                Interlocked.Increment(ref sizeBypass);
                return false;
            }

            PackageState state = GetState();
            if (state == null)
            {
                Interlocked.Increment(ref capacityBypass);
                return false;
            }

            ThingListMetadata existing;
            if (!state.Sources.TryGetValue(source, out existing) || existing == null)
            {
                Interlocked.Increment(ref misses);
                return false;
            }

            if (!Validate(source, existing))
            {
                state.Sources.Remove(source);
                Interlocked.Increment(ref validationMismatches);
                Interlocked.Increment(ref invalidations);
                Interlocked.Increment(ref misses);
                return false;
            }

            metadata = existing;
            Interlocked.Increment(ref hits);
            return true;
        }

        internal static ThingListMetadata PublishCaptured(IList source, SourceItem[] items)
        {
            if (!RimMTThreadGuard.IsMainThread || !JobSearchPackageContext093T28.InScope)
            {
                Interlocked.Increment(ref scopeBypass);
                return null;
            }

            if (source == null || items == null)
            {
                Interlocked.Increment(ref shapeBypass);
                Interlocked.Increment(ref publishRejected);
                return null;
            }

            int count;
            try { count = source.Count; }
            catch
            {
                Interlocked.Increment(ref shapeBypass);
                Interlocked.Increment(ref publishRejected);
                return null;
            }

            if (count != items.Length || count < 0 || count > MaxSourceCount)
            {
                Interlocked.Increment(ref sizeBypass);
                Interlocked.Increment(ref publishRejected);
                return null;
            }

            ThingListMetadata candidate = new ThingListMetadata(
                items,
                JobSearchPackageContext093T28.CurrentGeneration);

            // O(8), not O(n): catches an immediate source mutation without adding another
            // full scan to the consumer's first-use path.
            if (!Validate(source, candidate))
            {
                Interlocked.Increment(ref validationMismatches);
                Interlocked.Increment(ref publishRejected);
                return null;
            }

            PackageState state = GetState();
            if (state == null)
            {
                Interlocked.Increment(ref capacityBypass);
                Interlocked.Increment(ref publishRejected);
                return null;
            }

            ThingListMetadata prior;
            if (state.Sources.TryGetValue(source, out prior))
            {
                state.Sources[source] = candidate;
                Interlocked.Increment(ref replacements);
            }
            else
            {
                if (state.Sources.Count >= MaxSourcesPerPackage)
                {
                    Interlocked.Increment(ref capacityBypass);
                    Interlocked.Increment(ref publishRejected);
                    return null;
                }
                state.Sources.Add(source, candidate);
            }

            Interlocked.Increment(ref publishes);
            return candidate;
        }

        internal static bool IsCurrent(IList source, ThingListMetadata expected)
        {
            if (source == null || expected == null ||
                !RimMTThreadGuard.IsMainThread || !JobSearchPackageContext093T28.InScope)
                return false;

            PackageState state = JobSearchPackageContext093T28.GetModuleState<PackageState>(StateKey);
            if (state == null) return false;

            ThingListMetadata current;
            if (!state.Sources.TryGetValue(source, out current) ||
                !ReferenceEquals(current, expected))
                return false;

            if (Validate(source, expected))
                return true;

            state.Sources.Remove(source);
            Interlocked.Increment(ref validationMismatches);
            Interlocked.Increment(ref invalidations);
            return false;
        }

        private static bool Validate(IList source, ThingListMetadata metadata)
        {
            Interlocked.Increment(ref validationChecks);
            if (source == null || metadata == null) return false;

            int count;
            try { count = source.Count; }
            catch { return false; }

            if (count != metadata.Count) return false;
            if (metadata.Generation != JobSearchPackageContext093T28.CurrentGeneration)
                return false;

            int sampleCount = Math.Min(FingerprintSamples, count);
            for (int s = 0; s < sampleCount; s++)
            {
                int index = sampleCount == 1
                    ? 0
                    : (int)((long)s * (count - 1) / (sampleCount - 1));

                Thing currentThing;
                try { currentThing = source[index] as Thing; }
                catch { return false; }

                SourceItem captured = metadata.Items[index];
                if (!ReferenceEquals(currentThing, captured.Thing) ||
                    currentThing == null ||
                    !currentThing.Spawned ||
                    currentThing.PositionHeld != captured.Position)
                    return false;
            }

            return true;
        }

        private static PackageState GetState()
        {
            return JobSearchPackageContext093T28.GetOrCreateModuleState(
                StateKey,
                delegate { return new PackageState(); });
        }

        internal static string Summary()
        {
            long localLookups = Interlocked.Read(ref lookups);
            long localHits = Interlocked.Read(ref hits);
            double hitRate = localLookups == 0
                ? 0.0
                : localHits * 100.0 / localLookups;

            return "T29 source metadata foundation: active=" + JobSearchPackageContext093T28.Installed +
                ", lookups=" + localLookups +
                ", hits=" + localHits +
                ", misses=" + Interlocked.Read(ref misses) +
                ", hitRate=" + hitRate.ToString("F2") + "%" +
                ", publishes=" + Interlocked.Read(ref publishes) +
                ", replacements=" + Interlocked.Read(ref replacements) +
                ", validation[checks/mismatches]=" + Interlocked.Read(ref validationChecks) + "/" +
                Interlocked.Read(ref validationMismatches) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", bypass[scope/shape/size/capacity]=" +
                Interlocked.Read(ref scopeBypass) + "/" +
                Interlocked.Read(ref shapeBypass) + "/" +
                Interlocked.Read(ref sizeBypass) + "/" +
                Interlocked.Read(ref capacityBypass) +
                ", publishRejected=" + Interlocked.Read(ref publishRejected) +
                ", maxSourcesPerPackage=" + MaxSourcesPerPackage +
                ", maxSourceCount=" + MaxSourceCount +
                ", fingerprintSamples=" + FingerprintSamples +
                ". Package-local only; captures Thing reference + PositionHeld from scans a consumer already performed. " +
                "No ListerThings lookup, validator, Reachability, reservation, Job, priority, or cross-package gameplay result is cached.";
        }

        internal struct SourceItem
        {
            internal readonly Thing Thing;
            internal readonly IntVec3 Position;

            internal SourceItem(Thing thing, IntVec3 position)
            {
                Thing = thing;
                Position = position;
            }
        }

        internal sealed class ThingListMetadata
        {
            internal readonly SourceItem[] Items;
            internal readonly int Count;
            internal readonly long Generation;

            internal ThingListMetadata(SourceItem[] items, long generation)
            {
                Items = items;
                Count = items == null ? 0 : items.Length;
                Generation = generation;
            }
        }

        private sealed class PackageState
        {
            internal readonly Dictionary<object, ThingListMetadata> Sources =
                new Dictionary<object, ThingListMetadata>(ReferenceEqualityComparer.Instance);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
