using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// PUAH 1.5 emits one expensive StoreUtility search for every candidate presented to
    /// WorkGiver_HaulToInventory.HasJobOnThing. C.11 snapshots only primitive storage-priority
    /// and acceptance facts in small main-thread slices. Workers compile those facts into a
    /// structural-negative set. Every negative is proved again against live storage settings on
    /// the main thread before the original method may be skipped.
    /// </summary>
    internal static class HaulToInventoryParallelEligibility093T34C11
    {
        internal const string FeatureId = "parallel.haulToInventoryEligibility";
        private const int MinSourceCount = 64;
        private const int CaptureSlice = 96;
        private const int MaxDestinations = 256;

        private static readonly ConditionalWeakTable<Map, MapState> Maps =
            new ConditionalWeakTable<Map, MapState>();
        private static MethodInfo hasJobMethod;
        private static bool installed;
        private static int authorityState;
        private static int gameplayAuthorityAudited;
        private static string targetAssembly = "absent";

        private static long sourceCalls;
        private static long sourceEligible;
        private static long hasJobCalls;
        private static long planLookups;
        private static long predictedNegatives;
        private static long authoritativeRejects;
        private static long liveRevocations;
        private static long captureSlices;
        private static long capturedCandidates;
        private static long capturedAcceptanceCells;
        private static long captureTotalUs;
        private static long captureMaxUs;
        private static long scheduled;
        private static long schedulerRejected;
        private static long built;
        private static long published;
        private static long workerCandidates;
        private static long workerFailures;
        private static long captureFailures;
        private static long authorityBypasses;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                Type type = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
                if (type == null)
                {
                    targetAssembly = "absent";
                    return;
                }

                targetAssembly = type.Assembly.GetName().Name + "/" + type.Assembly.GetName().Version;
                MethodInfo source = AccessTools.Method(type, "PotentialWorkThingsGlobal");
                hasJobMethod = AccessTools.Method(type, "HasJobOnThing");
                if (source == null || hasJobMethod == null)
                    throw new MissingMethodException(type.FullName, "PotentialWorkThingsGlobal/HasJobOnThing");

                authorityState = AuditAuthority(hasJobMethod) ? 1 : -1;
                harmony.Patch(source,
                    postfix: new HarmonyMethod(typeof(HaulToInventoryParallelEligibility093T34C11), nameof(SourcePostfix))
                    { priority = Priority.Last });
                harmony.Patch(hasJobMethod,
                    prefix: new HarmonyMethod(typeof(HaulToInventoryParallelEligibility093T34C11), nameof(HasJobPrefix))
                    { priority = Priority.First });
                installed = true;
                Log.Message("[RimMT] T34-C.11 PUAH storage-eligibility fabric installed for " + targetAssembly +
                    ": bounded primitive capture, worker negative compilation, live authoritative proof, no worker wait.");
            }
            catch (Exception ex)
            {
                installed = false;
                authorityState = -1;
                Log.Warning("[RimMT] T34-C.11 PUAH storage-eligibility fabric failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void SourcePostfix(Pawn pawn, IEnumerable<Thing> __result)
        {
            Interlocked.Increment(ref sourceCalls);
            if (!Ready() || pawn == null || pawn.Map == null || __result == null) return;

            IList<Thing> source = __result as IList<Thing>;
            if (source == null || source.Count < MinSourceCount) return;
            Interlocked.Increment(ref sourceEligible);

            Map map = pawn.Map;
            MapState state = Maps.GetValue(map, delegate(Map ignored) { return new MapState(); });
            int tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
            if (tick < 0 || state.LastCaptureTick == tick || Volatile.Read(ref state.Pending) != 0) return;

            List<IHaulDestination> destinations;
            try { destinations = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder; }
            catch
            {
                Interlocked.Increment(ref captureFailures);
                return;
            }
            if (destinations == null || destinations.Count == 0 || destinations.Count > MaxDestinations) return;

            int count = source.Count;
            if (state.SourceCount != count)
            {
                state.SourceCount = count;
                state.Cursor = 0;
            }
            int take = Math.Min(CaptureSlice, count);
            int start = state.Cursor;
            int destinationCount = destinations.Count;
            take = Math.Min(take, Math.Max(8, 4096 / destinationCount));
            int[] ids = new int[take];
            int[] currentPriorities = new int[take];
            byte[] valid = new byte[take];
            int[] destinationPriorities = new int[destinationCount];
            byte[] accepts = new byte[take * destinationCount];

            long captureStart = Stopwatch.GetTimestamp();
            try
            {
                for (int d = 0; d < destinationCount; d++)
                {
                    IHaulDestination destination = destinations[d];
                    StorageSettings settings = destination == null ? null : destination.GetStoreSettings();
                    destinationPriorities[d] = settings == null ? int.MinValue : (int)settings.Priority;
                }

                for (int i = 0; i < take; i++)
                {
                    int sourceIndex = (start + i) % count;
                    Thing thing = source[sourceIndex];
                    if (thing == null || thing.Destroyed || thing.MapHeld != map) continue;
                    ids[i] = thing.thingIDNumber;
                    currentPriorities[i] = (int)StoreUtility.CurrentStoragePriorityOf(thing);
                    valid[i] = 1;
                    int row = i * destinationCount;
                    for (int d = 0; d < destinationCount; d++)
                    {
                        IHaulDestination destination = destinations[d];
                        if (destination == null) continue;
                        try { if (destination.Accepts(thing)) accepts[row + d] = 1; }
                        catch { valid[i] = 0; break; }
                    }
                }
            }
            catch
            {
                RecordCaptureTime(captureStart);
                Interlocked.Increment(ref captureFailures);
                return;
            }
            RecordCaptureTime(captureStart);

            state.LastCaptureTick = tick;
            state.Cursor = (start + take) % count;
            Volatile.Write(ref state.Pending, 1);
            Interlocked.Increment(ref captureSlices);
            Interlocked.Add(ref capturedCandidates, take);
            Interlocked.Add(ref capturedAcceptanceCells, (long)take * destinationCount);

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            bool accepted = scheduler != null && scheduler.TryEnqueue(FeatureId, JobPriority.High, delegate
            {
                Build(state, ids, currentPriorities, destinationPriorities, accepts, valid, destinationCount);
            });
            if (accepted) Interlocked.Increment(ref scheduled);
            else
            {
                Volatile.Write(ref state.Pending, 0);
                Interlocked.Increment(ref schedulerRejected);
            }
        }

        public static bool HasJobPrefix(Pawn pawn, Thing thing, ref bool __result)
        {
            long call = Interlocked.Increment(ref hasJobCalls);
            if (!Ready() || pawn == null || thing == null || pawn.Map == null) return true;

            if (Interlocked.CompareExchange(ref gameplayAuthorityAudited, 1, 0) == 0 || (call & 4095L) == 0L)
            {
                if (!AuditAuthority(hasJobMethod))
                {
                    Volatile.Write(ref authorityState, -1);
                    Interlocked.Increment(ref authorityBypasses);
                    return true;
                }
            }

            MapState state;
            if (!Maps.TryGetValue(pawn.Map, out state) || state == null) return true;
            PredictionPlan plan = Volatile.Read(ref state.Plan);
            if (plan == null) return true;
            Interlocked.Increment(ref planLookups);
            if (Array.BinarySearch(plan.NegativeThingIds, thing.thingIDNumber) < 0) return true;
            Interlocked.Increment(ref predictedNegatives);

            if (!LiveProvesNoHigherPriorityDestination(pawn.Map, thing))
            {
                Interlocked.Increment(ref liveRevocations);
                return true;
            }

            __result = false;
            Interlocked.Increment(ref authoritativeRejects);
            return false;
        }

        private static bool Ready()
        {
            return installed && Volatile.Read(ref authorityState) > 0 && FeatureGate.IsEnabled(FeatureId) &&
                RimMTThreadGuard.IsMainThread && Current.ProgramState == ProgramState.Playing;
        }

        private static void Build(MapState state, int[] ids, int[] currentPriorities,
            int[] destinationPriorities, byte[] accepts, byte[] valid, int destinationCount)
        {
            try
            {
                int[] negatives = new int[ids.Length];
                int negativeCount = 0;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (valid[i] == 0 || ids[i] <= 0) continue;
                    bool hasHigher = false;
                    int row = i * destinationCount;
                    int current = currentPriorities[i];
                    for (int d = 0; d < destinationCount; d++)
                    {
                        if (destinationPriorities[d] > current && accepts[row + d] != 0)
                        {
                            hasHigher = true;
                            break;
                        }
                    }
                    if (!hasHigher) negatives[negativeCount++] = ids[i];
                }
                Array.Resize(ref negatives, negativeCount);
                Array.Sort(negatives);
                Interlocked.Add(ref workerCandidates, ids.Length);
                Interlocked.Increment(ref built);

                if (!MainThreadDispatcher.TryEnqueue(delegate { Publish(state, negatives); }))
                    Volatile.Write(ref state.Pending, 0);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref state.Pending, 0);
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
        }

        private static void Publish(MapState state, int[] negatives)
        {
            try
            {
                PredictionPlan prior = state.Plan;
                if (prior == null || prior.NegativeThingIds.Length == 0)
                {
                    state.Plan = new PredictionPlan(negatives);
                }
                else if (negatives.Length != 0)
                {
                    HashSet<int> merged = new HashSet<int>(prior.NegativeThingIds);
                    for (int i = 0; i < negatives.Length; i++) merged.Add(negatives[i]);
                    int[] ids = new int[merged.Count];
                    merged.CopyTo(ids);
                    Array.Sort(ids);
                    state.Plan = new PredictionPlan(ids);
                }
                Interlocked.Increment(ref published);
            }
            finally { Volatile.Write(ref state.Pending, 0); }
        }

        private static bool LiveProvesNoHigherPriorityDestination(Map map, Thing thing)
        {
            try
            {
                int current = (int)StoreUtility.CurrentStoragePriorityOf(thing);
                List<IHaulDestination> destinations = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
                for (int i = 0; i < destinations.Count; i++)
                {
                    IHaulDestination destination = destinations[i];
                    if (destination == null) continue;
                    StorageSettings settings = destination.GetStoreSettings();
                    if (settings == null || (int)settings.Priority <= current) continue;
                    if (destination.Accepts(thing)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void RecordCaptureTime(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = (long)(elapsed * (1000000.0 / Stopwatch.Frequency));
            Interlocked.Add(ref captureTotalUs, us);
            long prior = Interlocked.Read(ref captureMaxUs);
            while (us > prior)
            {
                long observed = Interlocked.CompareExchange(ref captureMaxUs, us, prior);
                if (observed == prior) break;
                prior = observed;
            }
        }

        private static bool AuditAuthority(MethodBase method)
        {
            if (method == null) return false;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return true;
            return !HasForeign(info.Prefixes) && !HasForeign(info.Postfixes) &&
                   !HasForeign(info.Transpilers) && !HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal) ||
                    string.Equals(patch.owner, "allen.rimmt.diagnostics", StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        internal static string Summary()
        {
            return "T34-C.11 Haul-to-Inventory parallel eligibility: installed=" + installed +
                ", target=" + targetAssembly + ", authority=" + authorityState +
                ", source[calls/eligible]=" + Interlocked.Read(ref sourceCalls) + "/" + Interlocked.Read(ref sourceEligible) +
                ", hasJob[calls/lookups/predicted/rejected/revoked]=" + Interlocked.Read(ref hasJobCalls) + "/" +
                Interlocked.Read(ref planLookups) + "/" + Interlocked.Read(ref predictedNegatives) + "/" +
                Interlocked.Read(ref authoritativeRejects) + "/" + Interlocked.Read(ref liveRevocations) +
                ", capture[slices/candidates/cells]=" + Interlocked.Read(ref captureSlices) + "/" +
                Interlocked.Read(ref capturedCandidates) + "/" + Interlocked.Read(ref capturedAcceptanceCells) +
                ", captureUs[total/max]=" + Interlocked.Read(ref captureTotalUs) + "/" + Interlocked.Read(ref captureMaxUs) +
                ", worker[scheduled/rejected/built/published/candidates/failures]=" + Interlocked.Read(ref scheduled) + "/" +
                Interlocked.Read(ref schedulerRejected) + "/" + Interlocked.Read(ref built) + "/" +
                Interlocked.Read(ref published) + "/" + Interlocked.Read(ref workerCandidates) + "/" +
                Interlocked.Read(ref workerFailures) + ", captureFailures=" + Interlocked.Read(ref captureFailures) +
                ", authorityBypasses=" + Interlocked.Read(ref authorityBypasses) +
                ". Workers consume primitive ids/priorities/acceptance bytes only; every rejection is re-proved live; no wait.";
        }

        private sealed class MapState
        {
            internal int Pending;
            internal int LastCaptureTick = -1;
            internal int SourceCount = -1;
            internal int Cursor;
            internal PredictionPlan Plan;
        }

        private sealed class PredictionPlan
        {
            internal readonly int[] NegativeThingIds;
            internal PredictionPlan(int[] ids) { NegativeThingIds = ids ?? new int[0]; }
        }
    }
}
