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
        private static int paritySamplesLogged;

        private static long observedCalls;
        private static long pawnOverloadCalls;
        private static long traverseOverloadCalls;
        private static long otherOverloadCalls;
        private static long scheduled;
        private static long completed;
        private static long matched;
        private static long mismatched;
        private static long stale;
        private static long unsupported;
        private static long throttled;
        private static long snapshotBuilds;
        private static long terrainCostSnapshots;
        private static long nodesExpanded;
        private static long workerFailures;

        private static long foundParity;
        private static long foundMismatch;
        private static long workerLegal;
        private static long workerIllegal;
        private static long vanillaSnapshotLegal;
        private static long vanillaSnapshotIllegal;
        private static long workerEndpointMatch;
        private static long workerEndpointMismatch;
        private static long vanillaEndpointMatch;
        private static long vanillaEndpointMismatch;
        private static long costComparable;
        private static long sameSnapshotCost;
        private static long workerCheaper;
        private static long workerCostlier;
        private static long costWithinOnePct;
        private static long costWithinFivePct;
        private static long totalAbsCostDelta;
        private static long maxAbsCostDelta;
        private static long totalAbsCostDeltaBps;
        private static long maxAbsCostDeltaBps;
        private static long nodeComparable;
        private static long totalAbsNodeDelta;
        private static long maxAbsNodeDelta;
        private static long divergenceComparable;
        private static long totalSharedPrefixFromStart;
        private static long minSharedPrefixFromStart = long.MaxValue;

        private static long rejectedFeatureDisabled;
        private static long rejectedNotPlaying;
        private static long rejectedBadArgs;
        private static long rejectedInvalidTarget;
        private static long rejectedTargetThing;
        private static long rejectedEndMode;
        private static long rejectedCustomTuning;
        private static long rejectedBashDoors;
        private static long rejectedBashFences;
        private static long rejectedTraverseMode;
        private static long rejectedInvalidMap;
        private static long rejectedShortDistance;
        private static long rejectedNoScheduler;
        private static long rejectedSnapshot;
        private static long rejectedTrackedLimit;
        private static long rejectedScheduler;

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
            Interlocked.Increment(ref observedCalls);
            if (args != null && args.Length > 2)
            {
                if (args[2] is TraverseParms)
                    Interlocked.Increment(ref traverseOverloadCalls);
                else if (args[2] is Pawn)
                    Interlocked.Increment(ref pawnOverloadCalls);
                else
                    Interlocked.Increment(ref otherOverloadCalls);
            }
            else
            {
                Interlocked.Increment(ref otherOverloadCalls);
            }

            if (!FeatureGate.IsEnabled("parallel.pathSnapshot"))
            {
                Interlocked.Increment(ref rejectedFeatureDisabled);
                return 0;
            }
            if (finder == null || args == null || args.Length < 4)
                return RejectUnsupported(ref rejectedBadArgs);
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return RejectUnsupported(ref rejectedNotPlaying);

            // The Pawn overload normally delegates to the TraverseParms overload. Scheduling only
            // the latter avoids duplicate snapshots and duplicate worker A* work.
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
                return RejectUnsupported(ref rejectedBadArgs);
            }

            if (!start.IsValid || !dest.IsValid)
                return RejectUnsupported(ref rejectedInvalidTarget);
            if (dest.HasThing)
                return RejectUnsupported(ref rejectedTargetThing);
            if (endMode != PathEndMode.OnCell)
                return RejectUnsupported(ref rejectedEndMode);
            if (args.Length >= 5 && args[4] != null)
                return RejectUnsupported(ref rejectedCustomTuning);
            if (traverseParms.canBashDoors)
                return RejectUnsupported(ref rejectedBashDoors);
            if (traverseParms.canBashFences)
                return RejectUnsupported(ref rejectedBashFences);
            if (traverseParms.mode != TraverseMode.ByPawn && traverseParms.mode != TraverseMode.NoPassClosedDoors)
                return RejectUnsupported(ref rejectedTraverseMode);

            Pawn pawn = traverseParms.pawn;
            Map map = pawn == null ? null : pawn.Map;
            if (map == null && PathFinderMapField != null)
                map = PathFinderMapField.GetValue(finder) as Map;
            if (map == null || map.Disposed || !start.InBounds(map) || !dest.Cell.InBounds(map))
                return RejectUnsupported(ref rejectedInvalidMap);

            int dx = Math.Abs(start.x - dest.Cell.x);
            int dz = Math.Abs(start.z - dest.Cell.z);
            if (Math.Max(dx, dz) < MinPathDistance)
            {
                Interlocked.Increment(ref rejectedShortDistance);
                return 0;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Interlocked.Increment(ref rejectedNoScheduler);
                return 0;
            }

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
                Interlocked.Increment(ref rejectedSnapshot);
                Interlocked.Increment(ref unsupported);
                CircuitBreaker.RecordFailure("parallel.pathSnapshot", ex);
                Log.Warning("[RimMT] parallel.pathSnapshot snapshot capture failed; vanilla pathing continues. " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }

            if (snapshot == null)
            {
                Interlocked.Decrement(ref inFlight);
                return RejectUnsupported(ref rejectedSnapshot);
            }

            if (Requests.Count >= MaxTrackedRequests)
            {
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref throttled);
                Interlocked.Increment(ref rejectedTrackedLimit);
                return 0;
            }

            int requestId = Interlocked.Increment(ref nextRequestId);
            if (requestId == 0)
                requestId = Interlocked.Increment(ref nextRequestId);

            // RimWorld 1.5 changed pawn movement ticks to float. Vanilla keeps those values as
            // float while composing the per-cell step and only rounds after adding knownCost.
            // Preserve that exact ordering instead of truncating the pawn speed before worker A*.
            float moveCardinal = pawn == null ? 13f : Math.Max(1f, pawn.TicksPerMoveCardinal);
            float moveDiagonal = pawn == null ? 18f : Math.Max(1f, pawn.TicksPerMoveDiagonal);
            bool drafted = pawn != null && pawn.Drafted;

            PathRequest request = new PathRequest(
                requestId,
                map.uniqueID,
                snapshot,
                start.x + start.z * snapshot.Width,
                dest.Cell.x + dest.Cell.z * snapshot.Width,
                moveCardinal,
                moveDiagonal,
                drafted);
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
                Interlocked.Increment(ref rejectedScheduler);
                return 0;
            }

            long acceptedCount = Interlocked.Increment(ref scheduled);
            if (acceptedCount == 1)
                Log.Message("[RimMT] parallel.pathSnapshot accepted its first real worker task. V0.4.4 cost model includes float pawn move ticks with Vanilla-compatible rounding plus drafted/non-drafted terrain extras; vanilla remains authoritative.");
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
                if (nodes != null && nodes.Count > 0)
                {
                    result.NodesReversed = new int[nodes.Count];
                    int hash = 17;
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        IntVec3 cell = nodes[i];
                        int index = cell.x + cell.z * request.Snapshot.Width;
                        result.NodesReversed[i] = index;
                        unchecked { hash = hash * 31 + index; }
                    }
                    result.PathHash = hash;
                }
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

            long comparable = Interlocked.Read(ref costComparable);
            double avgCostDelta = comparable <= 0 ? 0.0 : Interlocked.Read(ref totalAbsCostDelta) / (double)comparable;
            double avgCostPct = comparable <= 0 ? 0.0 : Interlocked.Read(ref totalAbsCostDeltaBps) / (double)comparable / 100.0;
            double maxCostPct = Interlocked.Read(ref maxAbsCostDeltaBps) / 100.0;

            long nodeCount = Interlocked.Read(ref nodeComparable);
            double avgNodeDelta = nodeCount <= 0 ? 0.0 : Interlocked.Read(ref totalAbsNodeDelta) / (double)nodeCount;

            long divergenceCount = Interlocked.Read(ref divergenceComparable);
            double avgSharedPrefix = divergenceCount <= 0 ? 0.0 : Interlocked.Read(ref totalSharedPrefixFromStart) / (double)divergenceCount;
            long minShared = Interlocked.Read(ref minSharedPrefixFromStart);
            if (minShared == long.MaxValue) minShared = 0;

            return "Path snapshot worker: scheduled=" + Scheduled +
                ", completed=" + done +
                ", inFlight=" + InFlight +
                ", exactGeometry=" + exact +
                " (" + exactPct.ToString("F1") + "%)" +
                ", mismatch=" + Mismatched +
                ", stale=" + Stale +
                ", unsupported=" + Unsupported +
                ", throttled=" + Throttled +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ", snapshots=" + SnapshotBuilds +
                ", terrainCostSnapshots=" + Interlocked.Read(ref terrainCostSnapshots) +
                ", nodesExpanded=" + NodesExpanded +
                "\nPath cost model V0.4.4: pathGrid + float pawnMoveTicks/Vanilla rounding + draftedTerrainExtra; dynamic avoid/allowedArea/pawnCollision/building/blueprint/lord costs remain vanilla-only" +
                "\nPath parity: foundParity=" + Interlocked.Read(ref foundParity) +
                ", foundMismatch=" + Interlocked.Read(ref foundMismatch) +
                ", workerLegal=" + Interlocked.Read(ref workerLegal) +
                ", workerIllegal=" + Interlocked.Read(ref workerIllegal) +
                ", vanillaSnapshotLegal=" + Interlocked.Read(ref vanillaSnapshotLegal) +
                ", vanillaSnapshotIllegal=" + Interlocked.Read(ref vanillaSnapshotIllegal) +
                ", workerEndpointMatch=" + Interlocked.Read(ref workerEndpointMatch) +
                ", workerEndpointMismatch=" + Interlocked.Read(ref workerEndpointMismatch) +
                ", vanillaEndpointMatch=" + Interlocked.Read(ref vanillaEndpointMatch) +
                ", vanillaEndpointMismatch=" + Interlocked.Read(ref vanillaEndpointMismatch) +
                "\nPath cost parity: comparable=" + comparable +
                ", sameCost=" + Interlocked.Read(ref sameSnapshotCost) +
                ", workerCheaper=" + Interlocked.Read(ref workerCheaper) +
                ", workerCostlier=" + Interlocked.Read(ref workerCostlier) +
                ", within1pct=" + Interlocked.Read(ref costWithinOnePct) +
                ", within5pct=" + Interlocked.Read(ref costWithinFivePct) +
                ", avgAbsDelta=" + avgCostDelta.ToString("F1") +
                ", maxAbsDelta=" + Interlocked.Read(ref maxAbsCostDelta) +
                ", avgAbsDeltaPct=" + avgCostPct.ToString("F2") + "%" +
                ", maxAbsDeltaPct=" + maxCostPct.ToString("F2") + "%" +
                "\nPath geometry parity: nodeComparable=" + nodeCount +
                ", avgAbsNodeDelta=" + avgNodeDelta.ToString("F2") +
                ", maxAbsNodeDelta=" + Interlocked.Read(ref maxAbsNodeDelta) +
                ", divergenceSamples=" + divergenceCount +
                ", avgSharedPrefixFromStart=" + avgSharedPrefix.ToString("F2") +
                ", minSharedPrefixFromStart=" + minShared +
                "\nPath snapshot ingress: observed=" + Interlocked.Read(ref observedCalls) +
                ", pawnOverload=" + Interlocked.Read(ref pawnOverloadCalls) +
                ", traverseParmsOverload=" + Interlocked.Read(ref traverseOverloadCalls) +
                ", otherOverload=" + Interlocked.Read(ref otherOverloadCalls) +
                "\nPath snapshot rejects: featureDisabled=" + Interlocked.Read(ref rejectedFeatureDisabled) +
                ", notPlaying=" + Interlocked.Read(ref rejectedNotPlaying) +
                ", badArgs=" + Interlocked.Read(ref rejectedBadArgs) +
                ", invalidTarget=" + Interlocked.Read(ref rejectedInvalidTarget) +
                ", targetThing=" + Interlocked.Read(ref rejectedTargetThing) +
                ", endMode=" + Interlocked.Read(ref rejectedEndMode) +
                ", customTuning=" + Interlocked.Read(ref rejectedCustomTuning) +
                ", bashDoors=" + Interlocked.Read(ref rejectedBashDoors) +
                ", bashFences=" + Interlocked.Read(ref rejectedBashFences) +
                ", traverseMode=" + Interlocked.Read(ref rejectedTraverseMode) +
                ", invalidMap=" + Interlocked.Read(ref rejectedInvalidMap) +
                ", shortDistance=" + Interlocked.Read(ref rejectedShortDistance) +
                ", noScheduler=" + Interlocked.Read(ref rejectedNoScheduler) +
                ", snapshot=" + Interlocked.Read(ref rejectedSnapshot) +
                ", trackedLimit=" + Interlocked.Read(ref rejectedTrackedLimit) +
                ", schedulerRejected=" + Interlocked.Read(ref rejectedScheduler);
        }

        private static int RejectUnsupported(ref long counter)
        {
            Interlocked.Increment(ref counter);
            Interlocked.Increment(ref unsupported);
            return 0;
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

                TerrainDef[] topGrid = map.terrainGrid.topGrid;
                int[] draftedExtra = new int[source.Length];
                int[] nonDraftedExtra = new int[source.Length];
                int terrainLength = topGrid == null ? 0 : Math.Min(topGrid.Length, source.Length);
                for (int i = 0; i < terrainLength; i++)
                {
                    TerrainDef terrain = topGrid[i];
                    if (terrain == null)
                        continue;
                    draftedExtra[i] = terrain.extraDraftedPerceivedPathCost;
                    nonDraftedExtra[i] = terrain.extraNonDraftedPerceivedPathCost;
                }

                PathSnapshot snapshot = new PathSnapshot(map.uniqueID, map.Size.x, map.Size.z, generation, costs, draftedExtra, nonDraftedExtra);
                Snapshots[key] = snapshot;
                Interlocked.Increment(ref snapshotBuilds);
                Interlocked.Increment(ref terrainCostSnapshots);
                return snapshot;
            }
        }

        private static void RunWorker(PathRequest request)
        {
            try
            {
                WorkerResult result = FindPath(request, Scratch.Value);
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
            catch (Exception ex)
            {
                PathRequest removed;
                if (Requests.TryRemove(request.Id, out removed))
                    Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure("parallel.pathSnapshot", ex);
                string message = "[RimMT] parallel.pathSnapshot worker failed; request was discarded and vanilla remains authoritative. " + ex.GetType().Name + ": " + ex.Message;
                MainThreadDispatcher.TryEnqueue(delegate { Log.Warning(message); });
            }
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
            long done = Interlocked.Increment(ref completed);

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
            }
            else
            {
                AnalyzeParity(request, worker, vanilla);

                bool same = worker.Found == vanilla.Found;
                if (same && worker.Found)
                    same = worker.NodeCount == vanilla.NodeCount && worker.PathHash == vanilla.PathHash;

                if (same)
                    Interlocked.Increment(ref matched);
                else
                    Interlocked.Increment(ref mismatched);
            }

            if (done == 8 || done == 32 || done == 128)
            {
                MainThreadDispatcher.TryEnqueue(delegate
                {
                    Log.Message("[RimMT] parallel.pathSnapshot reached " + done + " completed paired validations. " + Summary());
                });
            }
        }

        private static void AnalyzeParity(PathRequest request, WorkerResult worker, VanillaResult vanilla)
        {
            bool foundSame = worker.Found == vanilla.Found;
            if (foundSame) Interlocked.Increment(ref foundParity);
            else Interlocked.Increment(ref foundMismatch);

            if (!worker.Found && !vanilla.Found)
                return;

            PathEvaluation workerEval = EvaluatePath(request, worker.NodesReversed);
            if (worker.Found)
            {
                if (workerEval.Legal) Interlocked.Increment(ref workerLegal);
                else Interlocked.Increment(ref workerIllegal);
                if (workerEval.EndpointMatch) Interlocked.Increment(ref workerEndpointMatch);
                else Interlocked.Increment(ref workerEndpointMismatch);
            }

            PathEvaluation vanillaEval = EvaluatePath(request, vanilla.NodesReversed);
            if (vanilla.Found)
            {
                if (vanillaEval.Legal) Interlocked.Increment(ref vanillaSnapshotLegal);
                else Interlocked.Increment(ref vanillaSnapshotIllegal);
                if (vanillaEval.EndpointMatch) Interlocked.Increment(ref vanillaEndpointMatch);
                else Interlocked.Increment(ref vanillaEndpointMismatch);
            }

            if (!worker.Found || !vanilla.Found)
            {
                LogParitySample(request, worker, vanilla, workerEval, vanillaEval, -1);
                return;
            }

            long nodeDelta = Math.Abs((long)worker.NodeCount - vanilla.NodeCount);
            Interlocked.Increment(ref nodeComparable);
            Interlocked.Add(ref totalAbsNodeDelta, nodeDelta);
            UpdateMax(ref maxAbsNodeDelta, nodeDelta);

            int sharedPrefix = SharedPrefixFromStart(worker.NodesReversed, vanilla.NodesReversed);
            Interlocked.Increment(ref divergenceComparable);
            Interlocked.Add(ref totalSharedPrefixFromStart, sharedPrefix);
            UpdateMin(ref minSharedPrefixFromStart, sharedPrefix);

            if (workerEval.Legal && vanillaEval.Legal && workerEval.EndpointMatch && vanillaEval.EndpointMatch)
            {
                long delta = (long)workerEval.Cost - vanillaEval.Cost;
                long absDelta = Math.Abs(delta);
                long denominator = Math.Max(1, vanillaEval.Cost);
                long deltaBps = absDelta * 10000L / denominator;

                Interlocked.Increment(ref costComparable);
                Interlocked.Add(ref totalAbsCostDelta, absDelta);
                Interlocked.Add(ref totalAbsCostDeltaBps, deltaBps);
                UpdateMax(ref maxAbsCostDelta, absDelta);
                UpdateMax(ref maxAbsCostDeltaBps, deltaBps);

                if (delta == 0) Interlocked.Increment(ref sameSnapshotCost);
                else if (delta < 0) Interlocked.Increment(ref workerCheaper);
                else Interlocked.Increment(ref workerCostlier);

                if (deltaBps <= 100) Interlocked.Increment(ref costWithinOnePct);
                if (deltaBps <= 500) Interlocked.Increment(ref costWithinFivePct);
            }

            if (worker.PathHash != vanilla.PathHash || worker.NodeCount != vanilla.NodeCount)
                LogParitySample(request, worker, vanilla, workerEval, vanillaEval, sharedPrefix);
        }

        private static void LogParitySample(PathRequest request, WorkerResult worker, VanillaResult vanilla, PathEvaluation workerEval, PathEvaluation vanillaEval, int sharedPrefix)
        {
            int slot = Interlocked.Increment(ref paritySamplesLogged);
            if (slot > 6)
                return;

            string message = "[RimMT] Path parity sample #" + slot +
                ": request=" + request.Id +
                ", moveTicks=" + request.MoveCardinal.ToString("F2") + "/" + request.MoveDiagonal.ToString("F2") +
                ", drafted=" + request.Drafted +
                ", found(worker/vanilla)=" + worker.Found + "/" + vanilla.Found +
                ", legal(worker/vanillaSnapshot)=" + workerEval.Legal + "/" + vanillaEval.Legal +
                ", endpoints(worker/vanilla)=" + workerEval.EndpointMatch + "/" + vanillaEval.EndpointMatch +
                ", cost(worker/vanillaSnapshot)=" + workerEval.Cost + "/" + vanillaEval.Cost +
                ", nodes(worker/vanilla)=" + worker.NodeCount + "/" + vanilla.NodeCount +
                ", sharedPrefixFromStart=" + sharedPrefix +
                ". Vanilla remains authoritative.";
            MainThreadDispatcher.TryEnqueue(delegate { Log.Message(message); });
        }

        private static PathEvaluation EvaluatePath(PathRequest request, int[] nodesReversed)
        {
            PathSnapshot snapshot = request.Snapshot;
            PathEvaluation evaluation = new PathEvaluation();
            if (nodesReversed == null || nodesReversed.Length == 0)
                return evaluation;

            evaluation.EndpointMatch = nodesReversed[0] == request.DestIndex && nodesReversed[nodesReversed.Length - 1] == request.StartIndex;
            int cost = 0;
            for (int i = nodesReversed.Length - 1; i > 0; i--)
            {
                int from = nodesReversed[i];
                int to = nodesReversed[i - 1];
                if (from < 0 || from >= snapshot.Costs.Length || to < 0 || to >= snapshot.Costs.Length)
                    return evaluation;
                if (snapshot.Costs[to] >= PathGrid.ImpassableCost)
                    return evaluation;

                int fromX = from % snapshot.Width;
                int fromZ = from / snapshot.Width;
                int toX = to % snapshot.Width;
                int toZ = to / snapshot.Width;
                int dx = Math.Abs(toX - fromX);
                int dz = Math.Abs(toZ - fromZ);
                if (dx > 1 || dz > 1 || (dx == 0 && dz == 0))
                    return evaluation;

                bool diagonal = dx == 1 && dz == 1;
                if (diagonal)
                {
                    int orthogonalA = toX + fromZ * snapshot.Width;
                    int orthogonalB = fromX + toZ * snapshot.Width;
                    if (snapshot.Costs[orthogonalA] >= PathGrid.ImpassableCost || snapshot.Costs[orthogonalB] >= PathGrid.ImpassableCost)
                        return evaluation;
                }

                cost = RoundToIntEven(cost + StepCost(request, to, diagonal));
                if (cost < 0)
                    return evaluation;
            }

            evaluation.Legal = true;
            evaluation.Cost = cost;
            return evaluation;
        }

        private static int SharedPrefixFromStart(int[] worker, int[] vanilla)
        {
            if (worker == null || vanilla == null)
                return 0;

            int wi = worker.Length - 1;
            int vi = vanilla.Length - 1;
            int shared = 0;
            while (wi >= 0 && vi >= 0 && worker[wi] == vanilla[vi])
            {
                shared++;
                wi--;
                vi--;
            }
            return shared;
        }

        private static void UpdateMax(ref long target, long value)
        {
            long current = Interlocked.Read(ref target);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        private static void UpdateMin(ref long target, long value)
        {
            long current = Interlocked.Read(ref target);
            while (value < current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        private static WorkerResult FindPath(PathRequest request, PathScratch scratch)
        {
            PathSnapshot snapshot = request.Snapshot;
            int startIndex = request.StartIndex;
            int destIndex = request.DestIndex;
            WorkerResult result = new WorkerResult();
            if (startIndex < 0 || startIndex >= snapshot.Costs.Length || destIndex < 0 || destIndex >= snapshot.Costs.Length ||
                snapshot.Costs[startIndex] >= PathGrid.ImpassableCost || snapshot.Costs[destIndex] >= PathGrid.ImpassableCost)
                return result;

            if (startIndex == destIndex)
            {
                result.Found = true;
                result.NodeCount = 1;
                result.NodesReversed = new[] { startIndex };
                result.PathCost = 0;
                unchecked { result.PathHash = 17 * 31 + startIndex; }
                return result;
            }

            scratch.Begin(snapshot.Costs.Length);
            scratch.SetG(startIndex, 0, -1);
            scratch.Heap.Push(startIndex, Heuristic(request, startIndex), 0);

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
                    result.PathCost = current.G;
                    BuildResultPath(ref result, scratch, startIndex, destIndex);
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

                    float step = StepCost(request, next, diagonal);
                    int newG = RoundToIntEven(current.G + step);
                    if (!scratch.HasG(next) || newG < scratch.GetG(next))
                    {
                        scratch.SetG(next, newG, cur);
                        int priority = newG + Heuristic(request, next);
                        scratch.Heap.Push(next, priority, newG);
                    }
                }
            }

            result.NodesExpanded = expanded;
            return result;
        }

        private static float StepCost(PathRequest request, int index, bool diagonal)
        {
            PathSnapshot snapshot = request.Snapshot;
            int terrainExtra = request.Drafted ? snapshot.DraftedTerrainExtra[index] : snapshot.NonDraftedTerrainExtra[index];
            return (diagonal ? request.MoveDiagonal : request.MoveCardinal) + snapshot.Costs[index] + terrainExtra;
        }

        private static void BuildResultPath(ref WorkerResult result, PathScratch scratch, int startIndex, int destIndex)
        {
            int count = 0;
            int cur = destIndex;
            while (cur >= 0 && count <= scratch.Capacity)
            {
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
                result.PathCost = 0;
                result.NodesReversed = null;
                return;
            }

            int[] nodes = new int[count];
            int hash = 17;
            cur = destIndex;
            for (int i = 0; i < count; i++)
            {
                nodes[i] = cur;
                unchecked { hash = hash * 31 + cur; }
                if (cur == startIndex)
                    break;
                cur = scratch.GetParent(cur);
            }

            result.NodeCount = count;
            result.PathHash = hash;
            result.NodesReversed = nodes;
        }

        private static int Heuristic(PathRequest request, int index)
        {
            int width = request.Snapshot.Width;
            int destIndex = request.DestIndex;
            int x = index % width;
            int z = index / width;
            int dx = Math.Abs(x - destIndex % width);
            int dz = Math.Abs(z - destIndex / width);
            int diagonal = Math.Min(dx, dz);
            int straight = Math.Max(dx, dz) - diagonal;
            int cardinalCost = RoundToIntEven(request.MoveCardinal);
            int diagonalCost = RoundToIntEven(request.MoveDiagonal);
            int safeDiagonal = Math.Min(diagonalCost, cardinalCost * 2);
            return diagonal * safeDiagonal + straight * cardinalCost;
        }

        // Unity's Mathf.RoundToInt uses nearest-integer rounding with .5 ties to even. Worker
        // threads must not call Unity APIs, so mirror the same rule with System.Math.
        private static int RoundToIntEven(float value)
        {
            return (int)Math.Round((double)value, MidpointRounding.ToEven);
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
            internal readonly int[] DraftedTerrainExtra;
            internal readonly int[] NonDraftedTerrainExtra;

            internal PathSnapshot(int mapId, int width, int height, int generation, int[] costs, int[] draftedTerrainExtra, int[] nonDraftedTerrainExtra)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                Generation = generation;
                Costs = costs;
                DraftedTerrainExtra = draftedTerrainExtra;
                NonDraftedTerrainExtra = nonDraftedTerrainExtra;
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
            internal readonly float MoveCardinal;
            internal readonly float MoveDiagonal;
            internal readonly bool Drafted;
            internal bool HasWorker;
            internal bool HasVanilla;
            internal WorkerResult Worker;
            internal VanillaResult Vanilla;

            internal PathRequest(int id, int mapId, PathSnapshot snapshot, int startIndex, int destIndex, float moveCardinal, float moveDiagonal, bool drafted)
            {
                Id = id;
                MapId = mapId;
                Snapshot = snapshot;
                StartIndex = startIndex;
                DestIndex = destIndex;
                MoveCardinal = moveCardinal;
                MoveDiagonal = moveDiagonal;
                Drafted = drafted;
            }
        }

        private struct WorkerResult
        {
            internal bool Found;
            internal bool Stale;
            internal int NodeCount;
            internal int PathHash;
            internal int NodesExpanded;
            internal int PathCost;
            internal int[] NodesReversed;
        }

        private struct VanillaResult
        {
            internal bool Found;
            internal int NodeCount;
            internal int PathHash;
            internal int[] NodesReversed;
        }

        private struct PathEvaluation
        {
            internal bool Legal;
            internal bool EndpointMatch;
            internal int Cost;
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
                seen[index] = stamp;
                g[index] = value;
                parent[index] = parentIndex;
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
