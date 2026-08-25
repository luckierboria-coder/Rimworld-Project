using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class PathSnapshotWorker
    {
        private const int MinPathDistance = 32;
        private const int MaxTrackedRequests = 256;
        private const int SearchLimit = 160000;
        private static readonly FieldInfo PathFinderMapField = AccessTools.Field(typeof(PathFinder), "map");
        private static readonly object SnapshotSync = new object();
        private static readonly Dictionary<SnapshotKey, PathSnapshot> Snapshots = new Dictionary<SnapshotKey, PathSnapshot>();
        private static readonly ConcurrentDictionary<int, PathRequest> Requests = new ConcurrentDictionary<int, PathRequest>();
        private static readonly ThreadLocal<PathScratch> Scratch = new ThreadLocal<PathScratch>(() => new PathScratch());

        private static int nextRequestId;
        private static int inFlight;
        private static long scheduled;
        private static long completed;
        private static long matched;
        private static long mismatched;
        private static long stale;
        private static long unsupported;
        private static long throttled;
        private static long snapshotBuilds;
        private static long nodesExpanded;

        internal static long Scheduled { get { return Interlocked.Read(ref scheduled); } }
        internal static long Completed { get { return Interlocked.Read(ref completed); } }
        internal static long Matched { get { return Interlocked.Read(ref matched); } }
        internal static long Mismatched { get { return Interlocked.Read(ref mismatched); } }
        internal static long Stale { get { return Interlocked.Read(ref stale); } }
        internal static long Unsupported { get { return Interlocked.Read(ref unsupported); } }
        internal static long Throttled { get { return Interlocked.Read(ref throttled); } }
        internal static long SnapshotBuilds { get { return Interlocked.Read(ref snapshotBuilds); } }
        internal static long NodesExpanded { get { return Interlocked.Read(ref nodesExpanded); } }
        internal static int InFlight { get { return Volatile.Read(ref inFlight); } }

        internal static int TrySchedule(PathFinder finder, object[] args)
        {
            if (!FeatureGate.IsEnabled("parallel.pathSnapshot") || finder == null || args == null || args.Length < 4)
                return 0;
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
            {
                Interlocked.Increment(ref unsupported);
                return 0;
            }

            // The Pawn overload immediately calls the TraverseParms overload. Schedule only the latter
            // so one logical FindPath request creates at most one worker task.
            if (!(args[2] is TraverseParms))
                return 0;

            IntVec3 start;
            LocalTargetInfo dest;
            TraverseParms traverseParms;
            PathEndMode endMode;
            try
            {
                start = (IntVec3)args[0];
                dest = (LocalTargetInfo)args[1];
                traverseParms = (TraverseParms)args[2];
                endMode = (PathEndMode)args[3];
            }
            catch
            {
                Interlocked.Increment(ref unsupported);
                return 0;
            }

            if (!start.IsValid || !dest.IsValid || dest.HasThing || endMode != PathEndMode.OnCell)
            {
                Interlocked.Increment(ref unsupported);
                return 0;
            }
            if (args.Length >= 5 && args[4] != null)
            {
                Interlocked.Increment(ref unsupported);
                return 0;
            }
            if (traverseParms.canBashDoors || traverseParms.canBashFences ||
                (traverseParms.mode != TraverseMode.ByPawn && traverseParms.mode != TraverseMode.NoPassClosedDoors))
            {
                Interlocked.Increment(ref unsupported);
                return 0;
            }

            Map map = traverseParms.pawn == null ? null : traverseParms.pawn.Map;
            if (map == null && PathFinderMapField != null)
                map = PathFinderMapField.GetValue(finder) as Map;
            if (map == null || map.Disposed || !start.InBounds(map) || !dest.Cell.InBounds(map))
            {
                Interlocked.Increment(ref unsupported);
                return 0;
            }

            int dx = Math.Abs(start.x - dest.Cell.x);
            int dz = Math.Abs(start.z - dest.Cell.z);
            if (Math.Max(dx, dz) < MinPathDistance)
                return 0;

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
                return 0;

            int limit = Math.Max(2, scheduler.WorkerCount * 2);
            int nowInFlight = Interlocked.Increment(ref inFlight);
            if (nowInFlight > limit)
            {
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref throttled);
                return 0;
            }

            PathSnapshot snapshot;
            try
            {
                snapshot = GetSnapshot(map, traverseParms);
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref unsupported);
                CircuitBreaker.RecordFailure("parallel.pathSnapshot", ex);
                Log.Warning("[RimMT] parallel.pathSnapshot snapshot capture failed; vanilla pathing continues. " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }

            if (snapshot == null)
            {
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref unsupported);
                return 0;
            }

            if (Requests.Count >= MaxTrackedRequests)
            {
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref throttled);
                return 0;
            }

            int requestId = Interlocked.Increment(ref nextRequestId);
            if (requestId == 0)
                requestId = Interlocked.Increment(ref nextRequestId);

            PathRequest request = new PathRequest(
                requestId,
                map.uniqueID,
                snapshot,
                start.x + start.z * snapshot.Width,
                dest.Cell.x + dest.Cell.z * snapshot.Width);
            Requests[requestId] = request;

            bool accepted = scheduler.TryEnqueue("parallel.pathSnapshot", JobPriority.Normal, delegate
            {
                RunWorker(request);
            });
            if (!accepted)
            {
                PathRequest ignored;
                Requests.TryRemove(requestId, out ignored);
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref throttled);
                return 0;
            }

            Interlocked.Increment(ref scheduled);
            return requestId;
        }

        internal static void RecordVanilla(int requestId, PawnPath path)
        {
            if (requestId == 0 || path == null)
                return;

            PathRequest request;
            if (!Requests.TryGetValue(requestId, out request))
                return;

            VanillaResult result = new VanillaResult();
            result.Found = path.Found;
            if (result.Found)
            {
                List<IntVec3> nodes = path.NodesReversed;
                result.NodeCount = nodes == null ? 0 : nodes.Count;
                int hash = 17;
                if (nodes != null)
                {
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        IntVec3 cell = nodes[i];
                        int index = cell.x + cell.z * request.Snapshot.Width;
                        unchecked { hash = hash * 31 + index; }
                    }
                }
                result.PathHash = hash;
            }

            lock (request.Sync)
            {
                request.Vanilla = result;
                request.HasVanilla = true;
            }
            TryFinalize(request);
        }

        internal static string Summary()
        {
            long done = Completed;
            long exact = Matched;
            double exactPct = done <= 0 ? 0.0 : exact * 100.0 / done;
            return "Path snapshot worker: scheduled=" + Scheduled +
                ", completed=" + done +
                ", inFlight=" + InFlight +
                ", exactGeometry=" + exact +
                " (" + exactPct.ToString("F1") + "%)" +
                ", mismatch=" + Mismatched +
                ", stale=" + Stale +
                ", unsupported=" + Unsupported +
                ", throttled=" + Throttled +
                ", snapshots=" + SnapshotBuilds +
                ", nodesExpanded=" + NodesExpanded;
        }

        private static PathSnapshot GetSnapshot(Map map, TraverseParms traverseParms)
        {
            int generation = ReachabilityNoCache.TopologyGeneration;
            bool fenceBlocked = traverseParms.fenceBlocked && !traverseParms.canBashFences;
            SnapshotKey key = new SnapshotKey(map.uniqueID, fenceBlocked);

            lock (SnapshotSync)
            {
                PathSnapshot existing;
                if (Snapshots.TryGetValue(key, out existing) && existing.Generation == generation &&
                    existing.Width == map.Size.x && existing.Height == map.Size.z)
                    return existing;

                PathingContext context = map.pathing.For(traverseParms);
                int[] source = context.pathGrid.pathGrid;
                int[] costs = new int[source.Length];
                Array.Copy(source, costs, source.Length);
                PathSnapshot snapshot = new PathSnapshot(map.uniqueID, map.Size.x, map.Size.z, generation, costs);
                Snapshots[key] = snapshot;
                Interlocked.Increment(ref snapshotBuilds);
                return snapshot;
            }
        }

        private static void RunWorker(PathRequest request)
        {
            WorkerResult result = FindPath(request.Snapshot, request.StartIndex, request.DestIndex, Scratch.Value);
            Interlocked.Add(ref nodesExpanded, result.NodesExpanded);

            if (ReachabilityNoCache.TopologyGeneration != request.Snapshot.Generation)
                result.Stale = true;

            lock (request.Sync)
            {
                request.Worker = result;
                request.HasWorker = true;
            }
            TryFinalize(request);
        }

        private static void TryFinalize(PathRequest request)
        {
            bool ready;
            lock (request.Sync)
                ready = request.HasWorker && request.HasVanilla;
            if (!ready)
                return;

            PathRequest removed;
            if (!Requests.TryRemove(request.Id, out removed))
                return;

            Interlocked.Decrement(ref inFlight);
            Interlocked.Increment(ref completed);

            WorkerResult worker;
            VanillaResult vanilla;
            lock (request.Sync)
            {
                worker = request.Worker;
                vanilla = request.Vanilla;
            }

            if (worker.Stale)
            {
                Interlocked.Increment(ref stale);
                return;
            }

            bool same = worker.Found == vanilla.Found;
            if (same && worker.Found)
                same = worker.NodeCount == vanilla.NodeCount && worker.PathHash == vanilla.PathHash;

            if (same)
                Interlocked.Increment(ref matched);
            else
                Interlocked.Increment(ref mismatched);
        }

        private static WorkerResult FindPath(PathSnapshot snapshot, int startIndex, int destIndex, PathScratch scratch)
        {
            WorkerResult result = new WorkerResult();
            if (startIndex < 0 || startIndex >= snapshot.Costs.Length || destIndex < 0 || destIndex >= snapshot.Costs.Length ||
                snapshot.Costs[startIndex] >= PathGrid.ImpassableCost || snapshot.Costs[destIndex] >= PathGrid.ImpassableCost)
                return result;

            if (startIndex == destIndex)
            {
                result.Found = true;
                result.NodeCount = 1;
                unchecked { result.PathHash = 17 * 31 + startIndex; }
                return result;
            }

            scratch.Begin(snapshot.Costs.Length);
            scratch.SetG(startIndex, 0, -1);
            scratch.Heap.Push(startIndex, Heuristic(startIndex, destIndex, snapshot.Width), 0);

            int expanded = 0;
            int width = snapshot.Width;
            int height = snapshot.Height;
            int[] costs = snapshot.Costs;

            while (scratch.Heap.Count > 0 && expanded < SearchLimit)
            {
                HeapEntry current = scratch.Heap.Pop();
                int cur = current.Index;
                if (!scratch.IsCurrent(cur, current.G) || scratch.IsClosed(cur))
                    continue;

                scratch.Close(cur);
                expanded++;
                if (cur == destIndex)
                {
                    result.Found = true;
                    result.NodesExpanded = expanded;
                    BuildResultPath(result, scratch, startIndex, destIndex);
                    return result;
                }

                int x = cur % width;
                int z = cur / width;
                for (int dir = 0; dir < 8; dir++)
                {
                    int nx = x + Dx[dir];
                    int nz = z + Dz[dir];
                    if ((uint)nx >= (uint)width || (uint)nz >= (uint)height)
                        continue;

                    int next = nx + nz * width;
                    if (costs[next] >= PathGrid.ImpassableCost || scratch.IsClosed(next))
                        continue;

                    bool diagonal = dir >= 4;
                    if (diagonal)
                    {
                        int orthogonalA = nx + z * width;
                        int orthogonalB = x + nz * width;
                        if (costs[orthogonalA] >= PathGrid.ImpassableCost || costs[orthogonalB] >= PathGrid.ImpassableCost)
                            continue;
                    }

                    int step = (diagonal ? 18 : 13) + costs[next];
                    int newG = current.G + step;
                    if (!scratch.HasG(next) || newG < scratch.GetG(next))
                    {
                        scratch.SetG(next, newG, cur);
                        int priority = newG + Heuristic(next, destIndex, width);
                        scratch.Heap.Push(next, priority, newG);
                    }
                }
            }

            result.NodesExpanded = expanded;
            return result;
        }

        private static void BuildResultPath(WorkerResult result, PathScratch scratch, int startIndex, int destIndex)
        {
            int count = 0;
            int hash = 17;
            int cur = destIndex;
            while (cur >= 0 && count <= scratch.Capacity)
            {
                unchecked { hash = hash * 31 + cur; }
                count++;
                if (cur == startIndex)
                    break;
                cur = scratch.GetParent(cur);
            }

            if (cur != startIndex)
            {
                result.Found = false;
                result.NodeCount = 0;
                result.PathHash = 0;
                return;
            }

            result.NodeCount = count;
            result.PathHash = hash;
        }

        private static int Heuristic(int index, int destIndex, int width)
        {
            int x = index % width;
            int z = index / width;
            int dx = Math.Abs(x - destIndex % width);
            int dz = Math.Abs(z - destIndex / width);
            int diagonal = Math.Min(dx, dz);
            int straight = Math.Max(dx, dz) - diagonal;
            return diagonal * 18 + straight * 13;
        }

        private static readonly int[] Dx = { 0, 1, 0, -1, 1, 1, -1, -1 };
        private static readonly int[] Dz = { -1, 0, 1, 0, -1, 1, 1, -1 };

        private struct SnapshotKey : IEquatable<SnapshotKey>
        {
            private readonly int mapId;
            private readonly bool fenceBlocked;
            internal SnapshotKey(int mapId, bool fenceBlocked) { this.mapId = mapId; this.fenceBlocked = fenceBlocked; }
            public bool Equals(SnapshotKey other) { return mapId == other.mapId && fenceBlocked == other.fenceBlocked; }
            public override bool Equals(object obj) { return obj is SnapshotKey && Equals((SnapshotKey)obj); }
            public override int GetHashCode() { unchecked { return mapId * 397 ^ (fenceBlocked ? 1 : 0); } }
        }

        private sealed class PathSnapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly int Generation;
            internal readonly int[] Costs;
            internal PathSnapshot(int mapId, int width, int height, int generation, int[] costs)
            {
                MapId = mapId; Width = width; Height = height; Generation = generation; Costs = costs;
            }
        }

        private sealed class PathRequest
        {
            internal readonly object Sync = new object();
            internal readonly int Id;
            internal readonly int MapId;
            internal readonly PathSnapshot Snapshot;
            internal readonly int StartIndex;
            internal readonly int DestIndex;
            internal bool HasWorker;
            internal bool HasVanilla;
            internal WorkerResult Worker;
            internal VanillaResult Vanilla;
            internal PathRequest(int id, int mapId, PathSnapshot snapshot, int startIndex, int destIndex)
            {
                Id = id; MapId = mapId; Snapshot = snapshot; StartIndex = startIndex; DestIndex = destIndex;
            }
        }

        private struct WorkerResult
        {
            internal bool Found;
            internal bool Stale;
            internal int NodeCount;
            internal int PathHash;
            internal int NodesExpanded;
        }

        private struct VanillaResult
        {
            internal bool Found;
            internal int NodeCount;
            internal int PathHash;
        }

        private sealed class PathScratch
        {
            private int[] g = new int[0];
            private int[] parent = new int[0];
            private int[] seen = new int[0];
            private int[] closed = new int[0];
            private int stamp;
            internal readonly MinHeap Heap = new MinHeap();
            internal int Capacity { get { return g.Length; } }

            internal void Begin(int capacity)
            {
                if (g.Length < capacity)
                {
                    g = new int[capacity];
                    parent = new int[capacity];
                    seen = new int[capacity];
                    closed = new int[capacity];
                    stamp = 0;
                }
                stamp++;
                if (stamp == int.MaxValue)
                {
                    Array.Clear(seen, 0, seen.Length);
                    Array.Clear(closed, 0, closed.Length);
                    stamp = 1;
                }
                Heap.Clear();
            }

            internal bool HasG(int index) { return seen[index] == stamp; }
            internal int GetG(int index) { return g[index]; }
            internal int GetParent(int index) { return parent[index]; }
            internal bool IsCurrent(int index, int value) { return seen[index] == stamp && g[index] == value; }
            internal bool IsClosed(int index) { return closed[index] == stamp; }
            internal void Close(int index) { closed[index] = stamp; }
            internal void SetG(int index, int value, int parentIndex)
            {
                seen[index] = stamp; g[index] = value; parent[index] = parentIndex;
            }
        }

        private struct HeapEntry
        {
            internal int Index;
            internal int Priority;
            internal int G;
            internal HeapEntry(int index, int priority, int g) { Index = index; Priority = priority; G = g; }
        }

        private sealed class MinHeap
        {
            private HeapEntry[] data = new HeapEntry[256];
            private int count;
            internal int Count { get { return count; } }
            internal void Clear() { count = 0; }

            internal void Push(int index, int priority, int g)
            {
                if (count == data.Length)
                    Array.Resize(ref data, data.Length * 2);
                int i = count++;
                HeapEntry entry = new HeapEntry(index, priority, g);
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (data[parent].Priority <= priority)
                        break;
                    data[i] = data[parent];
                    i = parent;
                }
                data[i] = entry;
            }

            internal HeapEntry Pop()
            {
                HeapEntry root = data[0];
                HeapEntry tail = data[--count];
                if (count == 0)
                    return root;

                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1;
                    if (left >= count)
                        break;
                    int right = left + 1;
                    int child = right < count && data[right].Priority < data[left].Priority ? right : left;
                    if (data[child].Priority >= tail.Priority)
                        break;
                    data[i] = data[child];
                    i = child;
                }
                data[i] = tail;
                return root;
            }
        }
    }
}
