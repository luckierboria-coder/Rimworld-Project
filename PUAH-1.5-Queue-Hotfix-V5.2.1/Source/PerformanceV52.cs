using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace PUAHQueueHotfix.PerformanceV52
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Type workGiverType = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
                if (workGiverType == null)
                {
                    Log.Warning("[PUAH 1.5 Queue Hotfix V5.2.1] Pick Up And Haul WorkGiver type not found; performance layer disabled. V5.1 queue safety DLL remains unaffected.");
                    return;
                }

                MethodInfo target = AccessTools.Method(
                    workGiverType,
                    "FindClosestThing",
                    new Type[] { typeof(List<Thing>), typeof(IntVec3), typeof(int).MakeByRefType() });

                if (target == null)
                {
                    Log.Warning("[PUAH 1.5 Queue Hotfix V5.2.1] FindClosestThing target not found; performance layer disabled. V5.1 queue safety DLL remains unaffected.");
                    return;
                }

                MethodInfo positionGetterMethod = AccessTools.PropertyGetter(typeof(Thing), "Position");
                if (positionGetterMethod == null)
                {
                    Log.Warning("[PUAH 1.5 Queue Hotfix V5.2.1] Thing.Position getter not found; performance layer disabled. V5.1 queue safety DLL remains unaffected.");
                    return;
                }

                NearestSearch.PositionGetter = (ThingPositionGetter)Delegate.CreateDelegate(
                    typeof(ThingPositionGetter), positionGetterMethod);

                Harmony harmony = new Harmony("local.PUAH15.QueueHotfix.PerformanceV52");
                HarmonyMethod prefix = new HarmonyMethod(
                    typeof(NearestSearch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static));
                prefix.priority = Priority.First;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[PUAH 1.5 Queue Hotfix V5.2.1] Safe performance layer active: PUAH FindClosestThing uses an exact 16x16 spatial index. JobOnThing is NOT transpiled; original PUAH Sort/CanReach/validator/RemoveAt flow remains authoritative. V5.1 queue safety DLL is unchanged.");
            }
            catch (Exception e)
            {
                Log.Warning("[PUAH 1.5 Queue Hotfix V5.2.1] Performance layer failed to initialize and was disabled. V5.1 queue safety DLL remains unaffected. " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    internal delegate IntVec3 ThingPositionGetter(Thing thing);

    internal static class NearestSearch
    {
        private const int BucketSize = 16;

        internal static ThingPositionGetter PositionGetter;

        private static readonly ConditionalWeakTable<List<Thing>, SearchState> States =
            new ConditionalWeakTable<List<Thing>, SearchState>();

        private static long acceleratedCalls;
        private static long fallbacks;
        private static long indexBuilds;
        private static long indexRebuilds;
        private static long distanceChecks;
        private static long estimatedLinearChecksAvoided;

        public static bool Prefix(List<Thing> searchSet, IntVec3 center, ref int index, ref Thing __result)
        {
            if (searchSet == null || searchSet.Count == 0)
            {
                index = -1;
                __result = null;
                Interlocked.Increment(ref acceleratedCalls);
                return false;
            }

            if (PositionGetter == null)
            {
                Interlocked.Increment(ref fallbacks);
                return true;
            }

            try
            {
                SearchState state = States.GetValue(searchSet, delegate(List<Thing> list)
                {
                    Interlocked.Increment(ref indexBuilds);
                    return SearchState.Build(list);
                });

                if (state.ExpectedListCount != searchSet.Count)
                {
                    state.Rebuild(searchSet);
                    Interlocked.Increment(ref indexRebuilds);
                }

                Thing chosen;
                int chosenIndex;
                int checks;
                if (!state.TryTakeNearest(searchSet, center, out chosen, out chosenIndex, out checks))
                {
                    Interlocked.Increment(ref fallbacks);
                    return true;
                }

                __result = chosen;
                index = chosenIndex;
                Interlocked.Increment(ref acceleratedCalls);
                Interlocked.Add(ref distanceChecks, checks);

                int avoided = searchSet.Count - checks;
                if (avoided > 0)
                    Interlocked.Add(ref estimatedLinearChecksAvoided, avoided);

                return false;
            }
            catch
            {
                Interlocked.Increment(ref fallbacks);
                return true;
            }
        }

        internal static string Summary()
        {
            return "PUAH V5.2.1 performance: accelerated=" + Interlocked.Read(ref acceleratedCalls) +
                ", fallback=" + Interlocked.Read(ref fallbacks) +
                ", indexBuilds=" + Interlocked.Read(ref indexBuilds) +
                ", indexRebuilds=" + Interlocked.Read(ref indexRebuilds) +
                ", distanceChecks=" + Interlocked.Read(ref distanceChecks) +
                ", estimatedLinearChecksAvoided=" + Interlocked.Read(ref estimatedLinearChecksAvoided);
        }

        private sealed class SearchState
        {
            private readonly Dictionary<long, List<Entry>> buckets = new Dictionary<long, List<Entry>>();
            private readonly List<int> removedOriginalIndices = new List<int>();
            private Entry[] entriesByOriginalIndex;
            private int minBucketX;
            private int maxBucketX;
            private int minBucketZ;
            private int maxBucketZ;
            private bool hasBuckets;

            internal int ExpectedListCount { get; private set; }

            private SearchState() { }

            internal static SearchState Build(List<Thing> list)
            {
                SearchState state = new SearchState();
                state.Rebuild(list);
                return state;
            }

            internal void Rebuild(List<Thing> list)
            {
                buckets.Clear();
                removedOriginalIndices.Clear();
                hasBuckets = false;
                ExpectedListCount = list == null ? 0 : list.Count;
                entriesByOriginalIndex = new Entry[ExpectedListCount];

                if (list == null)
                    return;

                for (int i = 0; i < list.Count; i++)
                {
                    Thing thing = list[i];
                    if (thing == null)
                        continue;

                    Entry entry = new Entry(thing, i);
                    entriesByOriginalIndex[i] = entry;

                    IntVec3 pos = PositionGetter(thing);
                    int bx = BucketCoord(pos.x);
                    int bz = BucketCoord(pos.z);
                    long key = BucketKey(bx, bz);

                    List<Entry> bucket;
                    if (!buckets.TryGetValue(key, out bucket))
                    {
                        bucket = new List<Entry>();
                        buckets.Add(key, bucket);
                    }
                    bucket.Add(entry);

                    if (!hasBuckets)
                    {
                        minBucketX = maxBucketX = bx;
                        minBucketZ = maxBucketZ = bz;
                        hasBuckets = true;
                    }
                    else
                    {
                        if (bx < minBucketX) minBucketX = bx;
                        if (bx > maxBucketX) maxBucketX = bx;
                        if (bz < minBucketZ) minBucketZ = bz;
                        if (bz > maxBucketZ) maxBucketZ = bz;
                    }
                }
            }

            internal bool TryTakeNearest(List<Thing> currentList, IntVec3 center, out Thing chosen, out int currentIndex, out int checks)
            {
                chosen = null;
                currentIndex = -1;
                checks = 0;

                if (currentList == null)
                    return false;
                if (currentList.Count == 0)
                    return true;
                if (!hasBuckets)
                    return false;

                Entry best;
                int localChecks;
                if (!TryFindNearest(center, out best, out localChecks) || best == null)
                    return false;
                checks += localChecks;

                int removedBefore = CountRemovedBefore(best.OriginalIndex);
                int calculatedIndex = best.OriginalIndex - removedBefore;

                if (calculatedIndex < 0 || calculatedIndex >= currentList.Count ||
                    !Object.ReferenceEquals(currentList[calculatedIndex], best.Thing))
                {
                    Rebuild(currentList);
                    if (!TryFindNearest(center, out best, out localChecks) || best == null)
                        return false;
                    checks += localChecks;
                    calculatedIndex = best.OriginalIndex;

                    if (calculatedIndex < 0 || calculatedIndex >= currentList.Count ||
                        !Object.ReferenceEquals(currentList[calculatedIndex], best.Thing))
                        return false;
                }

                MarkRemoved(best.OriginalIndex);
                ExpectedListCount--;
                chosen = best.Thing;
                currentIndex = calculatedIndex;
                return true;
            }

            private bool TryFindNearest(IntVec3 center, out Entry best, out int checks)
            {
                best = null;
                checks = 0;
                long bestDistance = long.MaxValue;

                int centerBX = BucketCoord(center.x);
                int centerBZ = BucketCoord(center.z);
                int maxRing = Max4(
                    Math.Abs(centerBX - minBucketX),
                    Math.Abs(centerBX - maxBucketX),
                    Math.Abs(centerBZ - minBucketZ),
                    Math.Abs(centerBZ - maxBucketZ));

                for (int ring = 0; ring <= maxRing; ring++)
                {
                    if (ring == 0)
                    {
                        ProbeBucket(centerBX, centerBZ, center, ref best, ref bestDistance, ref checks);
                    }
                    else
                    {
                        int left = centerBX - ring;
                        int right = centerBX + ring;
                        int bottom = centerBZ - ring;
                        int top = centerBZ + ring;

                        for (int bx = left; bx <= right; bx++)
                        {
                            ProbeBucket(bx, bottom, center, ref best, ref bestDistance, ref checks);
                            ProbeBucket(bx, top, center, ref best, ref bestDistance, ref checks);
                        }

                        for (int bz = bottom + 1; bz <= top - 1; bz++)
                        {
                            ProbeBucket(left, bz, center, ref best, ref bestDistance, ref checks);
                            ProbeBucket(right, bz, center, ref best, ref bestDistance, ref checks);
                        }
                    }

                    if (best != null)
                    {
                        int leftBoundary = (centerBX - ring) * BucketSize;
                        int rightExclusive = (centerBX + ring + 1) * BucketSize;
                        int bottomBoundary = (centerBZ - ring) * BucketSize;
                        int topExclusive = (centerBZ + ring + 1) * BucketSize;

                        int dLeft = center.x - leftBoundary + 1;
                        int dRight = rightExclusive - center.x;
                        int dBottom = center.z - bottomBoundary + 1;
                        int dTop = topExclusive - center.z;
                        int minOutside = Math.Min(Math.Min(dLeft, dRight), Math.Min(dBottom, dTop));
                        long minOutsideSquared = (long)minOutside * minOutside;

                        if (minOutsideSquared > bestDistance)
                            break;
                    }
                }

                return best != null;
            }

            private void ProbeBucket(int bx, int bz, IntVec3 center, ref Entry best, ref long bestDistance, ref int checks)
            {
                List<Entry> entries;
                if (!buckets.TryGetValue(BucketKey(bx, bz), out entries))
                    return;

                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    if (entry.Removed || entry.Thing == null)
                        continue;

                    IntVec3 pos = PositionGetter(entry.Thing);
                    long dx = (long)center.x - pos.x;
                    long dz = (long)center.z - pos.z;
                    long distance = dx * dx + dz * dz;
                    checks++;

                    if (best == null || distance < bestDistance ||
                        (distance == bestDistance && entry.OriginalIndex < best.OriginalIndex))
                    {
                        best = entry;
                        bestDistance = distance;
                    }
                }
            }

            private void MarkRemoved(int originalIndex)
            {
                if (entriesByOriginalIndex != null && originalIndex >= 0 && originalIndex < entriesByOriginalIndex.Length)
                {
                    Entry entry = entriesByOriginalIndex[originalIndex];
                    if (entry != null)
                        entry.Removed = true;
                }

                int insert = removedOriginalIndices.BinarySearch(originalIndex);
                if (insert < 0)
                    removedOriginalIndices.Insert(~insert, originalIndex);
            }

            private int CountRemovedBefore(int originalIndex)
            {
                int lo = 0;
                int hi = removedOriginalIndices.Count;
                while (lo < hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    if (removedOriginalIndices[mid] < originalIndex)
                        lo = mid + 1;
                    else
                        hi = mid;
                }
                return lo;
            }

            private static int BucketCoord(int value)
            {
                if (value >= 0)
                    return value / BucketSize;
                return -((-value + BucketSize - 1) / BucketSize);
            }

            private static long BucketKey(int bx, int bz)
            {
                return ((long)bx << 32) ^ (uint)bz;
            }

            private static int Max4(int a, int b, int c, int d)
            {
                return Math.Max(Math.Max(a, b), Math.Max(c, d));
            }

            private sealed class Entry
            {
                internal readonly Thing Thing;
                internal readonly int OriginalIndex;
                internal bool Removed;

                internal Entry(Thing thing, int originalIndex)
                {
                    Thing = thing;
                    OriginalIndex = originalIndex;
                }
            }
        }
    }
}
