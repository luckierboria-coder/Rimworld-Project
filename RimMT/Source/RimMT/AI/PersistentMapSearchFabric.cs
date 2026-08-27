using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.14: persistent per-map search fabric.
    //
    // V0.4.13 rebuilt a spatial index for each IList and invalidated it whenever any
    // member moved. Runtime proof showed that model was thread-safe but ineffective:
    // worker builds succeeded, while source position churn destroyed reuse and broad
    // no-result queries still performed hundreds of live Reachability/validator calls.
    //
    // The fabric changes ownership of position maintenance. A source registers only
    // its membership/order. Positions are then maintained incrementally from ThingGrid
    // register/deregister events. Workers consume immutable (Thing ref + integer cell)
    // events and publish immutable source bucket snapshots. The worker never dereferences
    // Thing/Map state. The main thread never waits for publication.
    internal static class PersistentMapSearchFabric
    {
        private const string FeatureId = "parallel.jobPartition";
        internal const int BucketSize = 12;
        private const int MaxSourcesPerMap = 96;

        private static readonly ConditionalWeakTable<Map, MapState> States =
            new ConditionalWeakTable<Map, MapState>();

        private static long sourceRegistrations;
        private static long sourceRegistrationRejected;
        private static long trackedAdds;
        private static long gridUpserts;
        private static long gridRemoves;
        private static long workerBatches;
        private static long workerEvents;
        private static long snapshotsPublished;
        private static long schedulerRejected;
        private static long snapshotHits;
        private static long snapshotMisses;
        private static long staleGenerationBypasses;
        private static long incompleteSourceBypasses;
        private static long failures;
        private static long publishTicks;
        private static long publishTicksMax;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodBase register = AccessTools.Method(
                    typeof(ThingGrid), "RegisterInCell", new Type[] { typeof(Thing), typeof(IntVec3) });
                MethodBase deregister = AccessTools.Method(
                    typeof(ThingGrid), "DeregisterInCell", new Type[] { typeof(Thing), typeof(IntVec3) });

                if (register == null || deregister == null)
                {
                    Log.Warning("[RimMT] V0.4.14 map fabric ThingGrid hooks unavailable; persistent source views stay fail-closed.");
                    return;
                }

                HarmonyMethod registerPostfix = new HarmonyMethod(typeof(PersistentMapSearchFabric), nameof(RegisterInCellPostfix));
                registerPostfix.priority = Priority.Last;
                HarmonyMethod deregisterPrefix = new HarmonyMethod(typeof(PersistentMapSearchFabric), nameof(DeregisterInCellPrefix));
                deregisterPrefix.priority = Priority.First;

                harmony.Patch(register, postfix: registerPostfix);
                harmony.Patch(deregister, prefix: deregisterPrefix);

                Log.Message("[RimMT] V0.4.14 persistent map search fabric installed. Tracked source positions are maintained incrementally on worker cores; the main thread never waits for fabric publication.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] V0.4.14 map fabric hooks failed; GenClosest remains fail-closed. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void RegisterInCellPostfix(Thing t, IntVec3 c)
        {
            if (t == null || !RimMTThreadGuard.IsMainThread)
                return;

            Map map = t.MapHeld;
            if (map == null || map.Disposed)
                return;

            MapState state;
            if (!States.TryGetValue(map, out state) || !state.TrackedThings.Contains(t))
                return;

            QueueEvent(state, FabricEvent.Upsert(NextGeneration(state), t, c.x, c.z));
            Interlocked.Increment(ref gridUpserts);
        }

        public static void DeregisterInCellPrefix(Thing t, IntVec3 c)
        {
            if (t == null || !RimMTThreadGuard.IsMainThread)
                return;

            Map map = t.MapHeld;
            if (map == null || map.Disposed)
                return;

            MapState state;
            if (!States.TryGetValue(map, out state) || !state.TrackedThings.Contains(t))
                return;

            QueueEvent(state, FabricEvent.Remove(NextGeneration(state), t));
            Interlocked.Increment(ref gridRemoves);
        }

        internal static bool RegisterOrUpdateSource(Map map, int sourceId, Thing[] members)
        {
            if (map == null || map.Disposed || members == null || members.Length == 0 ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return false;

            MapState state = States.GetValue(map, CreateMapState);
            if (state.KnownSources.Add(sourceId) && state.KnownSources.Count > MaxSourcesPerMap)
            {
                state.KnownSources.Remove(sourceId);
                Interlocked.Increment(ref sourceRegistrationRejected);
                return false;
            }

            int count = members.Length;
            Thing[] copy = new Thing[count];
            int[] xs = new int[count];
            int[] zs = new int[count];

            for (int i = 0; i < count; i++)
            {
                Thing thing = members[i];
                if (thing == null || thing is Pawn || !thing.Spawned || thing.MapHeld != map)
                    return false;

                IntVec3 pos = thing.Position;
                if (!pos.IsValid || !pos.InBounds(map))
                    return false;

                copy[i] = thing;
                xs[i] = pos.x;
                zs[i] = pos.z;
                if (state.TrackedThings.Add(thing))
                    Interlocked.Increment(ref trackedAdds);
            }

            long generation = NextGeneration(state);
            QueueEvent(state, FabricEvent.Source(generation, sourceId, copy, xs, zs));
            Interlocked.Increment(ref sourceRegistrations);
            return true;
        }

        internal static void NotifyDetectedPosition(Map map, Thing thing, IntVec3 pos)
        {
            if (map == null || thing == null || map.Disposed || !RimMTThreadGuard.IsMainThread)
                return;

            MapState state;
            if (!States.TryGetValue(map, out state) || !state.TrackedThings.Contains(thing))
                return;

            QueueEvent(state, FabricEvent.Upsert(NextGeneration(state), thing, pos.x, pos.z));
            Interlocked.Increment(ref gridUpserts);
        }

        internal static bool TryGetSourceSnapshot(Map map, int sourceId, out SourceSnapshot source)
        {
            source = null;
            if (map == null || map.Disposed)
                return false;

            MapState state;
            if (!States.TryGetValue(map, out state))
            {
                Interlocked.Increment(ref snapshotMisses);
                return false;
            }

            MapFabricSnapshot published = Volatile.Read(ref state.Published);
            if (published == null)
            {
                Interlocked.Increment(ref snapshotMisses);
                return false;
            }

            long required = Volatile.Read(ref state.MainGeneration);
            if (published.AppliedGeneration != required)
            {
                Interlocked.Increment(ref staleGenerationBypasses);
                return false;
            }

            if (!published.Sources.TryGetValue(sourceId, out source) || source == null)
            {
                Interlocked.Increment(ref snapshotMisses);
                return false;
            }

            if (!source.Complete)
            {
                Interlocked.Increment(ref incompleteSourceBypasses);
                source = null;
                return false;
            }

            Interlocked.Increment(ref snapshotHits);
            return true;
        }

        private static MapState CreateMapState(Map map)
        {
            return new MapState(map.uniqueID, map.Size.x, map.Size.z);
        }

        private static long NextGeneration(MapState state)
        {
            return Interlocked.Increment(ref state.MainGeneration);
        }

        private static void QueueEvent(MapState state, FabricEvent ev)
        {
            state.Events.Enqueue(ev);
            if (Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) != 0)
                return;

            ScheduleDrain(state);
        }

        private static void ScheduleDrain(MapState state)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
                return;
            }

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate
            {
                DrainWorker(state);
            });

            if (!accepted)
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
            }
        }

        private static void DrainWorker(MapState state)
        {
            try
            {
                int processed = 0;
                FabricEvent ev;
                while (state.Events.TryDequeue(out ev))
                {
                    state.Model.Apply(ev);
                    processed++;
                }

                if (processed > 0)
                {
                    long started = Stopwatch.GetTimestamp();
                    MapFabricSnapshot snapshot = state.Model.BuildSnapshot();
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    Interlocked.Add(ref publishTicks, elapsed);
                    UpdateMax(ref publishTicksMax, elapsed);
                    Volatile.Write(ref state.Published, snapshot);
                    Interlocked.Increment(ref workerBatches);
                    Interlocked.Add(ref workerEvents, processed);
                    Interlocked.Increment(ref snapshotsPublished);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] V0.4.14 map fabric worker failure; published data is ignored until a later synchronized snapshot. " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                if (!state.Events.IsEmpty && Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) == 0)
                    ScheduleDrain(state);
            }
        }

        internal static string Summary()
        {
            long publishes = Interlocked.Read(ref snapshotsPublished);
            double avgPublishUs = publishes == 0 ? 0.0 :
                (Interlocked.Read(ref publishTicks) * 1000000.0 / Stopwatch.Frequency) / publishes;
            double maxPublishUs = Interlocked.Read(ref publishTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Persistent map search fabric V0.4.14: sourceRegisters=" + Interlocked.Read(ref sourceRegistrations) +
                ", sourceRegisterRejected=" + Interlocked.Read(ref sourceRegistrationRejected) +
                ", trackedAdds=" + Interlocked.Read(ref trackedAdds) +
                ", gridUpserts=" + Interlocked.Read(ref gridUpserts) +
                ", gridRemoves=" + Interlocked.Read(ref gridRemoves) +
                ", workerBatches=" + Interlocked.Read(ref workerBatches) +
                ", workerEvents=" + Interlocked.Read(ref workerEvents) +
                ", published=" + publishes +
                ", snapshotHits=" + Interlocked.Read(ref snapshotHits) +
                ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) +
                ", staleGenerationBypass=" + Interlocked.Read(ref staleGenerationBypasses) +
                ", incompleteSourceBypass=" + Interlocked.Read(ref incompleteSourceBypasses) +
                ", avgPublishUs=" + avgPublishUs.ToString("F2") +
                ", maxPublishUs=" + maxPublishUs.ToString("F2") +
                ", schedulerRejected=" + Interlocked.Read(ref schedulerRejected) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Worker snapshots contain only Thing references plus primitive positions/source order; no Verse object is dereferenced off-thread.";
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen)
                    break;
            }
        }

        private sealed class MapState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly HashSet<Thing> TrackedThings = new HashSet<Thing>();
            internal readonly HashSet<int> KnownSources = new HashSet<int>();
            internal readonly ConcurrentQueue<FabricEvent> Events = new ConcurrentQueue<FabricEvent>();
            internal readonly WorkerModel Model;
            internal long MainGeneration;
            internal int WorkerScheduled;
            internal MapFabricSnapshot Published;

            internal MapState(int mapId, int width, int height)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                Model = new WorkerModel(mapId, width, height);
            }
        }

        private enum EventKind
        {
            Upsert,
            Remove,
            Source
        }

        private sealed class FabricEvent
        {
            internal EventKind Kind;
            internal long Generation;
            internal Thing Thing;
            internal int X;
            internal int Z;
            internal int SourceId;
            internal Thing[] Members;
            internal int[] Xs;
            internal int[] Zs;

            internal static FabricEvent Upsert(long generation, Thing thing, int x, int z)
            {
                return new FabricEvent { Kind = EventKind.Upsert, Generation = generation, Thing = thing, X = x, Z = z };
            }

            internal static FabricEvent Remove(long generation, Thing thing)
            {
                return new FabricEvent { Kind = EventKind.Remove, Generation = generation, Thing = thing };
            }

            internal static FabricEvent Source(long generation, int sourceId, Thing[] members, int[] xs, int[] zs)
            {
                return new FabricEvent
                {
                    Kind = EventKind.Source,
                    Generation = generation,
                    SourceId = sourceId,
                    Members = members,
                    Xs = xs,
                    Zs = zs
                };
            }
        }

        private struct PositionEntry
        {
            internal int X;
            internal int Z;
            internal PositionEntry(int x, int z) { X = x; Z = z; }
        }

        private sealed class SourceModel
        {
            internal Thing[] Members;
            internal SourceModel(Thing[] members) { Members = members; }
        }

        private sealed class WorkerModel
        {
            private readonly int mapId;
            private readonly int width;
            private readonly int height;
            private readonly Dictionary<Thing, PositionEntry> positions = new Dictionary<Thing, PositionEntry>();
            private readonly Dictionary<int, SourceModel> sources = new Dictionary<int, SourceModel>();
            private long appliedGeneration;

            internal WorkerModel(int mapId, int width, int height)
            {
                this.mapId = mapId;
                this.width = width;
                this.height = height;
            }

            internal void Apply(FabricEvent ev)
            {
                if (ev == null)
                    return;

                appliedGeneration = ev.Generation;
                switch (ev.Kind)
                {
                    case EventKind.Upsert:
                        if (ev.Thing != null)
                            positions[ev.Thing] = new PositionEntry(ev.X, ev.Z);
                        break;
                    case EventKind.Remove:
                        if (ev.Thing != null)
                            positions.Remove(ev.Thing);
                        break;
                    case EventKind.Source:
                        if (ev.Members == null)
                            break;
                        for (int i = 0; i < ev.Members.Length; i++)
                        {
                            Thing thing = ev.Members[i];
                            if (thing != null)
                                positions[thing] = new PositionEntry(ev.Xs[i], ev.Zs[i]);
                        }
                        sources[ev.SourceId] = new SourceModel(ev.Members);
                        break;
                }
            }

            internal MapFabricSnapshot BuildSnapshot()
            {
                Dictionary<int, SourceSnapshot> published = new Dictionary<int, SourceSnapshot>(sources.Count);
                foreach (KeyValuePair<int, SourceModel> pair in sources)
                    published[pair.Key] = BuildSourceSnapshot(pair.Value);
                return new MapFabricSnapshot(mapId, width, height, appliedGeneration, published);
            }

            private SourceSnapshot BuildSourceSnapshot(SourceModel source)
            {
                int cols = Math.Max(1, (width + BucketSize - 1) / BucketSize);
                int rows = Math.Max(1, (height + BucketSize - 1) / BucketSize);
                List<FabricEntry>[] temp = new List<FabricEntry>[cols * rows];
                bool complete = true;

                for (int i = 0; i < source.Members.Length; i++)
                {
                    Thing thing = source.Members[i];
                    PositionEntry pos;
                    if (thing == null || !positions.TryGetValue(thing, out pos) ||
                        pos.X < 0 || pos.Z < 0 || pos.X >= width || pos.Z >= height)
                    {
                        complete = false;
                        continue;
                    }

                    int key = (pos.X / BucketSize) + (pos.Z / BucketSize) * cols;
                    List<FabricEntry> list = temp[key];
                    if (list == null)
                        temp[key] = list = new List<FabricEntry>();
                    list.Add(new FabricEntry(thing, pos.X, pos.Z, i));
                }

                FabricEntry[][] buckets = new FabricEntry[temp.Length][];
                for (int i = 0; i < temp.Length; i++)
                    buckets[i] = temp[i] == null ? EmptyEntries : temp[i].ToArray();

                return new SourceSnapshot(mapId, width, height, cols, rows, source.Members.Length, complete, buckets);
            }
        }

        private static readonly FabricEntry[] EmptyEntries = new FabricEntry[0];

        private sealed class MapFabricSnapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long AppliedGeneration;
            internal readonly Dictionary<int, SourceSnapshot> Sources;

            internal MapFabricSnapshot(int mapId, int width, int height, long appliedGeneration, Dictionary<int, SourceSnapshot> sources)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                AppliedGeneration = appliedGeneration;
                Sources = sources;
            }
        }

        internal sealed class SourceSnapshot
        {
            private readonly int bucketCols;
            private readonly int bucketRows;
            private readonly FabricEntry[][] buckets;

            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int Count;
            internal readonly bool Complete;

            internal SourceSnapshot(int mapId, int width, int height, int bucketCols, int bucketRows, int count, bool complete, FabricEntry[][] buckets)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                Count = count;
                Complete = complete;
                this.bucketCols = bucketCols;
                this.bucketRows = bucketRows;
                this.buckets = buckets;
            }

            internal int EstimateCandidates(IntVec3 root, float maxDistance, int stopAfter)
            {
                if (stopAfter < 1)
                    stopAfter = 1;
                float maxDistanceSquared = maxDistance * maxDistance;
                int total = 0;
                for (int bz = 0; bz < bucketRows; bz++)
                {
                    for (int bx = 0; bx < bucketCols; bx++)
                    {
                        FabricEntry[] entries = buckets[bx + bz * bucketCols];
                        if (entries.Length == 0)
                            continue;
                        if (MinimumDistanceToBucketSquared(root, bx, bz) > maxDistanceSquared)
                            continue;
                        total += entries.Length;
                        if (total > stopAfter)
                            return total;
                    }
                }
                return total;
            }

            internal bool TryFindClosest(
                IntVec3 root,
                Map map,
                PathEndMode peMode,
                TraverseParms traverseParams,
                float maxDistance,
                Predicate<Thing> validator,
                int maxLiveChecks,
                out Thing chosen,
                out int visited,
                out int bucketsSeen,
                out int reaches,
                out int validations,
                out bool staleDetected)
            {
                chosen = null;
                visited = 0;
                bucketsSeen = 0;
                reaches = 0;
                validations = 0;
                staleDetected = false;

                if (map == null || map.uniqueID != MapId || map.Size.x != Width || map.Size.z != Height || !root.InBounds(map))
                    return false;

                float maxDistanceSquared = maxDistance * maxDistance;
                float bestDistanceSquared = float.MaxValue;
                int bestSourceIndex = int.MaxValue;
                int rootBucketX = root.x / BucketSize;
                int rootBucketZ = root.z / BucketSize;
                int maxRing = Math.Max(
                    Math.Max(rootBucketX, bucketCols - 1 - rootBucketX),
                    Math.Max(rootBucketZ, bucketRows - 1 - rootBucketZ));

                List<Candidate> ringCandidates = CandidateScratch.Value;
                for (int ring = 0; ring <= maxRing; ring++)
                {
                    ringCandidates.Clear();
                    int minBx = Math.Max(0, rootBucketX - ring);
                    int maxBx = Math.Min(bucketCols - 1, rootBucketX + ring);
                    int minBz = Math.Max(0, rootBucketZ - ring);
                    int maxBz = Math.Min(bucketRows - 1, rootBucketZ + ring);

                    AddRingCandidates(root, ring, minBx, maxBx, minBz, maxBz, maxDistanceSquared, ringCandidates, ref bucketsSeen);
                    ringCandidates.Sort(CandidateComparer.Instance);

                    for (int i = 0; i < ringCandidates.Count; i++)
                    {
                        Candidate candidate = ringCandidates[i];
                        if (candidate.DistanceSquared > bestDistanceSquared)
                            break;
                        if (visited >= maxLiveChecks)
                            return false;

                        FabricEntry entry = candidate.Entry;
                        Thing thing = entry.Thing;
                        visited++;
                        if (thing == null || !thing.Spawned || thing.MapHeld != map)
                        {
                            staleDetected = true;
                            return false;
                        }

                        IntVec3 live = thing.Position;
                        if (live.x != entry.X || live.z != entry.Z)
                        {
                            staleDetected = true;
                            NotifyDetectedPosition(map, thing, live);
                            return false;
                        }

                        reaches++;
                        if (!map.reachability.CanReach(root, (LocalTargetInfo)thing, peMode, traverseParams))
                            continue;

                        if (validator != null)
                        {
                            validations++;
                            if (!validator(thing))
                                continue;
                        }

                        chosen = thing;
                        bestDistanceSquared = candidate.DistanceSquared;
                        bestSourceIndex = entry.SourceIndex;
                    }

                    float outsideMin = MinimumOutsideDistanceSquared(root, minBx, maxBx, minBz, maxBz);
                    if (chosen != null && outsideMin > bestDistanceSquared)
                        return true;
                    if (chosen == null && outsideMin > maxDistanceSquared)
                        return true;
                }

                return true;
            }

            private void AddRingCandidates(
                IntVec3 root,
                int ring,
                int minBx,
                int maxBx,
                int minBz,
                int maxBz,
                float maxDistanceSquared,
                List<Candidate> output,
                ref int bucketsSeen)
            {
                if (ring == 0)
                {
                    AddBucket(root, root.x / BucketSize, root.z / BucketSize, maxDistanceSquared, output, ref bucketsSeen);
                    return;
                }

                for (int bx = minBx; bx <= maxBx; bx++)
                {
                    AddBucket(root, bx, minBz, maxDistanceSquared, output, ref bucketsSeen);
                    if (maxBz != minBz)
                        AddBucket(root, bx, maxBz, maxDistanceSquared, output, ref bucketsSeen);
                }
                for (int bz = minBz + 1; bz < maxBz; bz++)
                {
                    AddBucket(root, minBx, bz, maxDistanceSquared, output, ref bucketsSeen);
                    if (maxBx != minBx)
                        AddBucket(root, maxBx, bz, maxDistanceSquared, output, ref bucketsSeen);
                }
            }

            private void AddBucket(IntVec3 root, int bx, int bz, float maxDistanceSquared, List<Candidate> output, ref int bucketsSeen)
            {
                if (bx < 0 || bz < 0 || bx >= bucketCols || bz >= bucketRows)
                    return;
                FabricEntry[] entries = buckets[bx + bz * bucketCols];
                if (entries.Length == 0)
                    return;
                bucketsSeen++;
                for (int i = 0; i < entries.Length; i++)
                {
                    FabricEntry entry = entries[i];
                    long dx = root.x - entry.X;
                    long dz = root.z - entry.Z;
                    float distanceSquared = (float)(dx * dx + dz * dz);
                    if (distanceSquared <= maxDistanceSquared)
                        output.Add(new Candidate(entry, distanceSquared));
                }
            }

            private float MinimumDistanceToBucketSquared(IntVec3 root, int bx, int bz)
            {
                int minX = bx * BucketSize;
                int maxX = Math.Min(Width - 1, minX + BucketSize - 1);
                int minZ = bz * BucketSize;
                int maxZ = Math.Min(Height - 1, minZ + BucketSize - 1);
                long dx = root.x < minX ? minX - root.x : (root.x > maxX ? root.x - maxX : 0);
                long dz = root.z < minZ ? minZ - root.z : (root.z > maxZ ? root.z - maxZ : 0);
                return (float)(dx * dx + dz * dz);
            }

            private float MinimumOutsideDistanceSquared(IntVec3 root, int minBx, int maxBx, int minBz, int maxBz)
            {
                int minX = minBx * BucketSize;
                int maxX = Math.Min(Width - 1, (maxBx + 1) * BucketSize - 1);
                int minZ = minBz * BucketSize;
                int maxZ = Math.Min(Height - 1, (maxBz + 1) * BucketSize - 1);
                long best = long.MaxValue;

                if (minBx > 0) { long dx = root.x - (minX - 1); best = Math.Min(best, dx * dx); }
                if (maxBx < bucketCols - 1) { long dx = (maxX + 1) - root.x; best = Math.Min(best, dx * dx); }
                if (minBz > 0) { long dz = root.z - (minZ - 1); best = Math.Min(best, dz * dz); }
                if (maxBz < bucketRows - 1) { long dz = (maxZ + 1) - root.z; best = Math.Min(best, dz * dz); }
                return best == long.MaxValue ? float.MaxValue : (float)best;
            }
        }

        private struct FabricEntry
        {
            internal readonly Thing Thing;
            internal readonly int X;
            internal readonly int Z;
            internal readonly int SourceIndex;

            internal FabricEntry(Thing thing, int x, int z, int sourceIndex)
            {
                Thing = thing;
                X = x;
                Z = z;
                SourceIndex = sourceIndex;
            }
        }

        private struct Candidate
        {
            internal readonly FabricEntry Entry;
            internal readonly float DistanceSquared;
            internal Candidate(FabricEntry entry, float distanceSquared)
            {
                Entry = entry;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();
            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                if (distance != 0)
                    return distance;
                return a.Entry.SourceIndex.CompareTo(b.Entry.SourceIndex);
            }
        }

        private static readonly ThreadLocal<List<Candidate>> CandidateScratch =
            new ThreadLocal<List<Candidate>>(() => new List<Candidate>(128));
    }
}
