using System;
using System.Collections.Generic;

namespace RimMT
{
    // T27 extends the existing V0.4.14 persistent map fabric with a pure worker-side
    // distance-plan builder. FabricEntry.Thing is treated as an opaque identity token:
    // worker code never dereferences Thing or any Verse/Unity state.
    internal static partial class PersistentMapSearchFabric
    {
        internal sealed partial class SourceSnapshot
        {
            internal DistancePlan BuildDistancePlan(int rootX, int rootZ)
            {
                List<DistancePlanCandidate> candidates = new List<DistancePlanCandidate>(Count);
                for (int i = 0; i < buckets.Length; i++)
                {
                    FabricEntry[] entries = buckets[i];
                    for (int j = 0; j < entries.Length; j++)
                    {
                        FabricEntry entry = entries[j];
                        long dx = (long)entry.X - rootX;
                        long dz = (long)entry.Z - rootZ;
                        candidates.Add(new DistancePlanCandidate(entry, dx * dx + dz * dz));
                    }
                }

                if (candidates.Count > 1)
                    candidates.Sort(DistancePlanCandidateComparer.Instance);

                DistancePlanEntry[] ordered = new DistancePlanEntry[candidates.Count];
                Thing[] things = new Thing[candidates.Count];
                for (int i = 0; i < candidates.Count; i++)
                {
                    DistancePlanCandidate candidate = candidates[i];
                    FabricEntry entry = candidate.Entry;
                    ordered[i] = new DistancePlanEntry(entry.Thing, entry.X, entry.Z, entry.SourceIndex, candidate.DistanceSquared);
                    things[i] = entry.Thing;
                }
                return new DistancePlan(MapId, Width, Height, rootX, rootZ, ordered, things);
            }
        }

        internal sealed class DistancePlan
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int RootX;
            internal readonly int RootZ;
            internal readonly DistancePlanEntry[] Entries;
            internal readonly Thing[] OrderedThings;

            internal int Count { get { return Entries == null ? 0 : Entries.Length; } }

            internal DistancePlan(int mapId, int width, int height, int rootX, int rootZ,
                DistancePlanEntry[] entries, Thing[] orderedThings)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                RootX = rootX;
                RootZ = rootZ;
                Entries = entries;
                OrderedThings = orderedThings;
            }
        }

        internal struct DistancePlanEntry
        {
            internal readonly Thing Thing;
            internal readonly int X;
            internal readonly int Z;
            internal readonly int SourceIndex;
            internal readonly long DistanceSquared;

            internal DistancePlanEntry(Thing thing, int x, int z, int sourceIndex, long distanceSquared)
            {
                Thing = thing;
                X = x;
                Z = z;
                SourceIndex = sourceIndex;
                DistanceSquared = distanceSquared;
            }
        }

        private struct DistancePlanCandidate
        {
            internal readonly FabricEntry Entry;
            internal readonly long DistanceSquared;

            internal DistancePlanCandidate(FabricEntry entry, long distanceSquared)
            {
                Entry = entry;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class DistancePlanCandidateComparer : IComparer<DistancePlanCandidate>
        {
            internal static readonly DistancePlanCandidateComparer Instance = new DistancePlanCandidateComparer();

            public int Compare(DistancePlanCandidate a, DistancePlanCandidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                if (distance != 0) return distance;
                return a.Entry.SourceIndex.CompareTo(b.Entry.SourceIndex);
            }
        }
    }
}
