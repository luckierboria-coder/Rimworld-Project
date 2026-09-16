using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T27 bottom-level WorkGiver search kernel.
    ///
    /// This deliberately does NOT execute WorkGiver/validator/Reachability/Reservation/Job code on workers.
    /// At the JobGiver_Work package boundary the main thread classifies safe, Vanilla static ThingRequest
    /// sources and asks the already-proven PersistentMapSearchFabric to build distance/source-order plans
    /// speculatively. Workers consume only immutable FabricEntry records (opaque Thing identity + x/z/order).
    /// If a plan has already finished when Vanilla naturally reaches ClosestThingReachable, the main thread
    /// fully revalidates membership, spawn/map and positions, then supplies the exact same source sorted by
    /// distance and original source order. Vanilla region search, live CanReach, validator, reservations and
    /// JobOnThing remain authoritative. There is no Wait/SpinWait/Join/Sleep and no final Job cache.
    ///
    /// Inspired by YaOpt's coarse WorkGiver scheduling and Full/MainThreaded classification, but T27 v1
    /// intentionally admits only SnapshotParallel. Full WorkGiver execution stays OFF until JobMaker pools,
    /// reservations, Reachability scratch state and mod callbacks have dedicated thread-safety shims.
    /// </summary>
    internal static class ParallelWorkKernel093T27
    {
        internal const string FeatureId = "parallel.workKernel";

        private const int MinSourceCount = 64;
        private const int MaxSourceCount = 4096;
        private const int MaxPlansPerPackage = 8;

        private static readonly ConditionalWeakTable<object, SourceState> Sources =
            new ConditionalWeakTable<object, SourceState>();

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;

        private static volatile bool installed;
        private static volatile bool targetAuthoritySafe = true;
        private static int nextSourceId;
        private static long packages;
        private static long workGiversSeen;
        private static long snapshotClassified;
        private static long mainThreadClassified;
        private static long modClassBypass;
        private static long customSourceBypass;
        private static long prioritizedBypass;
        private static long unreachableBypass;
        private static long boundedRegionBypass;
        private static long requestBypass;
        private static long sizeBypass;
        private static long sourceRefreshes;
        private static long sourceRegisterRejected;
        private static long snapshotMisses;
        private static long plansScheduled;
        private static long plansCompleted;
        private static long plansReadyAtPackageEnd;
        private static long plansNotReadyAtPackageEnd;
        private static long plansConsumed;
        private static long planNotReadyAtUse;
        private static long planValidationFailed;
        private static long schedulerRejected;
        private static long workerFailures;
        private static long planItems;
        private static long validatedItems;
        private static long buildTicks;
        private static long buildTicksMax;
        private static long consumeTicks;
        private static long consumeTicksMax;
        private static long foreignTargetBypass;
        private static long installFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase package = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                MethodBase closest = AccessTools.Method(
                    typeof(GenClosest), nameof(GenClosest.ClosestThingReachable),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(IEnumerable<Thing>), typeof(int), typeof(int),
                        typeof(bool), typeof(RegionType), typeof(bool)
                    });
                if (package == null || closest == null)
                {
                    installFailures++;
                    FeatureGate.Suppress(FeatureId, "T27 JobGiver_Work/ClosestThingReachable target missing");
                    return;
                }

                targetAuthoritySafe = !HasForeignPatches(closest);
                harmony.Patch(package,
                    prefix: new HarmonyMethod(typeof(ParallelWorkKernel093T27), nameof(PackagePrefix)) { priority = Priority.First + 425 },
                    finalizer: new HarmonyMethod(typeof(ParallelWorkKernel093T27), nameof(PackageFinalizer)) { priority = Priority.Last - 425 });
                harmony.Patch(closest,
                    prefix: new HarmonyMethod(typeof(ParallelWorkKernel093T27), nameof(ClosestThingReachablePrefix)) { priority = Priority.First + 425 });

                installed = true;
                Log.Message("[RimMT] T27 Parallel Work Kernel installed. SnapshotParallel speculative plans are active when authority-safe; FullParallel WorkGiver execution remains OFF.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                FeatureGate.Suppress(FeatureId, "T27 install failure: " + ex.GetType().Name);
                Log.Warning("[RimMT] T27 Parallel Work Kernel install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PackagePrefix(JobGiver_Work __instance, Pawn pawn)
        {
            if (packageDepth++ != 0) return;
            current = null;

            if (!installed || !targetAuthoritySafe || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing ||
                pawn == null || !pawn.Spawned || pawn.Map == null || pawn.workSettings == null)
                return;

            Interlocked.Increment(ref packages);
            try
            {
                SchedulePackage(__instance, pawn);
            }
            catch
            {
                current = null;
            }
        }

        public static Exception PackageFinalizer(Exception __exception)
        {
            if (packageDepth > 0) packageDepth--;
            if (packageDepth == 0)
            {
                PackageContext context = current;
                if (context != null)
                {
                    foreach (KeyValuePair<object, PlanSlot> pair in context.Plans)
                    {
                        if (Volatile.Read(ref pair.Value.Ready) != 0)
                            Interlocked.Increment(ref plansReadyAtPackageEnd);
                        else
                            Interlocked.Increment(ref plansNotReadyAtPackageEnd);
                    }
                }
                current = null;
            }
            return __exception;
        }

        private static void SchedulePackage(JobGiver_Work giverWork, Pawn pawn)
        {
            if (giverWork == null) return;
            Map map = pawn.Map;
            IntVec3 root = pawn.Position;
            List<WorkGiver> workGivers = giverWork.emergency
                ? pawn.workSettings.WorkGiversInOrderEmergency
                : pawn.workSettings.WorkGiversInOrderNormal;
            if (workGivers == null || workGivers.Count == 0) return;

            PackageContext context = new PackageContext(pawn, map, root);
            current = context;

            for (int i = 0; i < workGivers.Count && context.Plans.Count < MaxPlansPerPackage; i++)
            {
                WorkGiver giver = workGivers[i];
                Interlocked.Increment(ref workGiversSeen);
                WorkGiver_Scanner scanner = giver as WorkGiver_Scanner;
                if (scanner == null || scanner.def == null || !scanner.def.scanThings)
                {
                    Interlocked.Increment(ref mainThreadClassified);
                    continue;
                }

                IList<Thing> source;
                Classification classification = ClassifySnapshotSource(scanner, map, out source);
                if (classification != Classification.SnapshotParallel || source == null)
                {
                    Interlocked.Increment(ref mainThreadClassified);
                    continue;
                }
                Interlocked.Increment(ref snapshotClassified);

                if (context.Plans.ContainsKey(source))
                    continue;

                int count;
                try { count = source.Count; }
                catch { continue; }
                if (count < MinSourceCount || count > MaxSourceCount)
                {
                    Interlocked.Increment(ref sizeBypass);
                    continue;
                }

                SourceState state = Sources.GetValue(source, CreateSourceState);
                if (!EnsureRegistered(map, source, state))
                    continue;

                PersistentMapSearchFabric.SourceSnapshot snapshot;
                if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, state.SourceId, out snapshot) ||
                    snapshot == null || !snapshot.Complete || snapshot.Count != count)
                {
                    Interlocked.Increment(ref snapshotMisses);
                    continue;
                }

                PlanSlot slot = new PlanSlot(source, state, scanner.def.defName, snapshot);
                context.Plans.Add(source, slot);

                JobScheduler scheduler = RimMTRuntime.Scheduler;
                if (scheduler == null || scheduler.ProductionPending > 24)
                {
                    Interlocked.Increment(ref schedulerRejected);
                    continue;
                }

                bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.High, delegate
                {
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        PersistentMapSearchFabric.DistancePlan plan = snapshot.BuildDistancePlan(root.x, root.z);
                        slot.Plan = plan;
                        if (plan != null)
                            Interlocked.Add(ref planItems, plan.Count);
                        Interlocked.Increment(ref plansCompleted);
                        Volatile.Write(ref slot.Ready, 1);
                    }
                    catch
                    {
                        Volatile.Write(ref slot.Failed, 1);
                        Interlocked.Increment(ref workerFailures);
                    }
                    finally
                    {
                        long elapsed = Stopwatch.GetTimestamp() - started;
                        Interlocked.Add(ref buildTicks, elapsed);
                        UpdateMax(ref buildTicksMax, elapsed);
                    }
                });

                if (accepted)
                    Interlocked.Increment(ref plansScheduled);
                else
                    Interlocked.Increment(ref schedulerRejected);
            }
        }

        private static Classification ClassifySnapshotSource(WorkGiver_Scanner scanner, Map map, out IList<Thing> source)
        {
            source = null;
            Type type = scanner.GetType();
            if (type.Assembly != typeof(JobGiver_Work).Assembly)
            {
                Interlocked.Increment(ref modClassBypass);
                return Classification.MainThread;
            }

            MethodInfo customSource = type.GetMethod(nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (customSource == null || customSource.DeclaringType != typeof(WorkGiver_Scanner))
            {
                Interlocked.Increment(ref customSourceBypass);
                return Classification.MainThread;
            }

            bool prioritized;
            bool allowUnreachable;
            int maxRegions;
            ThingRequest request;
            try
            {
                prioritized = scanner.Prioritized;
                allowUnreachable = scanner.AllowUnreachable;
                maxRegions = scanner.MaxRegionsToScanBeforeGlobalSearch;
                request = scanner.PotentialWorkThingRequest;
            }
            catch
            {
                return Classification.MainThread;
            }

            if (prioritized)
            {
                Interlocked.Increment(ref prioritizedBypass);
                return Classification.MainThread;
            }
            if (allowUnreachable)
            {
                Interlocked.Increment(ref unreachableBypass);
                return Classification.MainThread;
            }
            if (maxRegions >= 0)
            {
                Interlocked.Increment(ref boundedRegionBypass);
                return Classification.MainThread;
            }
            if (request.IsUndefined || request.group == ThingRequestGroup.Nothing || request.group == ThingRequestGroup.Everything)
            {
                Interlocked.Increment(ref requestBypass);
                return Classification.MainThread;
            }

            try
            {
                source = map.listerThings.ThingsMatching(request);
            }
            catch
            {
                source = null;
            }
            return source == null ? Classification.MainThread : Classification.SnapshotParallel;
        }

        private static bool EnsureRegistered(Map map, IList<Thing> source, SourceState state)
        {
            int count;
            try { count = source.Count; }
            catch { return false; }

            bool refresh = state.MapId != map.uniqueID || Volatile.Read(ref state.NeedsRefresh) != 0 ||
                !QuickMembershipMatches(source, state.Members);
            if (!refresh) return true;

            Thing[] members = new Thing[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || thing is Pawn || !thing.Spawned || thing.MapHeld != map)
                        return false;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(map))
                        return false;
                    members[i] = thing;
                }
            }
            catch
            {
                return false;
            }

            if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, members))
            {
                Interlocked.Increment(ref sourceRegisterRejected);
                return false;
            }

            state.MapId = map.uniqueID;
            state.Members = members;
            Volatile.Write(ref state.NeedsRefresh, 0);
            Interlocked.Increment(ref sourceRefreshes);
            return false; // publication is asynchronous; never wait for the new snapshot.
        }

        private static bool QuickMembershipMatches(IList<Thing> source, Thing[] members)
        {
            if (source == null || members == null) return false;
            int count;
            try { count = source.Count; }
            catch { return false; }
            if (count != members.Length) return false;
            if (count == 0) return true;

            int middle = count >> 1;
            try
            {
                return ReferenceEquals(source[0], members[0]) &&
                       ReferenceEquals(source[middle], members[middle]) &&
                       ReferenceEquals(source[count - 1], members[count - 1]);
            }
            catch { return false; }
        }

        public static void ClosestThingReachablePrefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            TraverseParms traverseParams,
            ref IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMin,
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions)
        {
            if (!installed || !targetAuthoritySafe || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || customGlobalSearchSet != null)
                return;

            PackageContext context = current;
            if (context == null || packageDepth <= 0 || map == null || !ReferenceEquals(map, context.Map) ||
                traverseParams.pawn == null || !ReferenceEquals(traverseParams.pawn, context.Pawn) ||
                root.x != context.Root.x || root.z != context.Root.z ||
                searchRegionsMin != 0 || searchRegionsMax >= 0 || forceAllowGlobalSearch ||
                traversableRegionTypes != RegionType.Set_Passable || ignoreEntirelyForbiddenRegions || thingReq.IsUndefined)
                return;

            if (!targetAuthoritySafe)
            {
                Interlocked.Increment(ref foreignTargetBypass);
                return;
            }

            IList<Thing> source;
            try { source = map.listerThings.ThingsMatching(thingReq); }
            catch { return; }
            if (source == null) return;

            PlanSlot slot;
            if (!context.Plans.TryGetValue(source, out slot) || slot == null)
                return;
            if (Volatile.Read(ref slot.Ready) == 0 || slot.Plan == null || Volatile.Read(ref slot.Failed) != 0)
            {
                Interlocked.Increment(ref planNotReadyAtUse);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            bool valid = ValidatePlan(map, source, slot);
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref consumeTicks, elapsed);
            UpdateMax(ref consumeTicksMax, elapsed);

            if (!valid)
            {
                Volatile.Write(ref slot.State.NeedsRefresh, 1);
                Interlocked.Increment(ref planValidationFailed);
                return;
            }

            customGlobalSearchSet = slot.Plan.OrderedThings;
            Interlocked.Add(ref validatedItems, slot.Plan.Count);
            Interlocked.Increment(ref plansConsumed);
            Volatile.Write(ref slot.Consumed, 1);
        }

        private static bool ValidatePlan(Map map, IList<Thing> source, PlanSlot slot)
        {
            SourceState state = slot.State;
            PersistentMapSearchFabric.DistancePlan plan = slot.Plan;
            Thing[] members = state.Members;
            if (state.MapId != map.uniqueID || members == null || plan == null ||
                plan.MapId != map.uniqueID || plan.Width != map.Size.x || plan.Height != map.Size.z ||
                plan.RootX != current.Root.x || plan.RootZ != current.Root.z || source.Count != members.Length ||
                plan.Count != members.Length || plan.OrderedThings == null || plan.OrderedThings.Length != plan.Count)
                return false;

            for (int i = 0; i < members.Length; i++)
            {
                if (!ReferenceEquals(source[i], members[i]))
                    return false;
            }

            PersistentMapSearchFabric.DistancePlanEntry[] entries = plan.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                PersistentMapSearchFabric.DistancePlanEntry entry = entries[i];
                Thing thing = entry.Thing;
                if (thing == null || entry.SourceIndex < 0 || entry.SourceIndex >= members.Length ||
                    !ReferenceEquals(members[entry.SourceIndex], thing) || !thing.Spawned || thing.MapHeld != map)
                    return false;
                IntVec3 pos = thing.Position;
                if (pos.x != entry.X || pos.z != entry.Z)
                    return false;
            }
            return true;
        }

        private static SourceState CreateSourceState(object source)
        {
            int id = Interlocked.Increment(ref nextSourceId);
            if (id == 0) id = Interlocked.Increment(ref nextSourceId);
            return new SourceState { SourceId = id, MapId = int.MinValue };
        }

        private static bool HasForeignPatches(MethodBase method)
        {
            try
            {
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches == null) return false;
                return HasForeign(patches.Prefixes) || HasForeign(patches.Postfixes) ||
                       HasForeign(patches.Transpilers) || HasForeign(patches.Finalizers);
            }
            catch { return true; }
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch != null && !string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        internal static string Summary()
        {
            long scheduled = Interlocked.Read(ref plansScheduled);
            long completed = Interlocked.Read(ref plansCompleted);
            long consumed = Interlocked.Read(ref plansConsumed);
            double avgItems = completed == 0 ? 0.0 : Interlocked.Read(ref planItems) / (double)completed;
            double avgBuildUs = completed == 0 ? 0.0 :
                Interlocked.Read(ref buildTicks) * 1000000.0 / Stopwatch.Frequency / completed;
            double avgConsumeUs = consumed == 0 ? 0.0 :
                Interlocked.Read(ref consumeTicks) * 1000000.0 / Stopwatch.Frequency / consumed;

            return "T27 parallel work kernel: installed=" + installed +
                ", authoritySafe=" + targetAuthoritySafe +
                ", packages=" + Interlocked.Read(ref packages) +
                ", workGiversSeen=" + Interlocked.Read(ref workGiversSeen) +
                ", classified[snapshot/main]=" + Interlocked.Read(ref snapshotClassified) + "/" + Interlocked.Read(ref mainThreadClassified) +
                ", bypass[mod/custom/prioritized/unreachable/boundedRegion/request/size]=" +
                Interlocked.Read(ref modClassBypass) + "/" + Interlocked.Read(ref customSourceBypass) + "/" +
                Interlocked.Read(ref prioritizedBypass) + "/" + Interlocked.Read(ref unreachableBypass) + "/" +
                Interlocked.Read(ref boundedRegionBypass) + "/" + Interlocked.Read(ref requestBypass) + "/" + Interlocked.Read(ref sizeBypass) +
                ", sourceRefreshes=" + Interlocked.Read(ref sourceRefreshes) +
                ", sourceRegisterRejected=" + Interlocked.Read(ref sourceRegisterRejected) +
                ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) +
                ", plans[scheduled/completed/consumed/notReadyUse/validationFail]=" + scheduled + "/" + completed + "/" + consumed + "/" +
                Interlocked.Read(ref planNotReadyAtUse) + "/" + Interlocked.Read(ref planValidationFailed) +
                ", endReady/notReady=" + Interlocked.Read(ref plansReadyAtPackageEnd) + "/" + Interlocked.Read(ref plansNotReadyAtPackageEnd) +
                ", schedulerRejected=" + Interlocked.Read(ref schedulerRejected) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ", avgItems=" + avgItems.ToString("F1") +
                ", avgWorkerBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxWorkerBuildUs=" + (Interlocked.Read(ref buildTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", avgMainValidateUs=" + avgConsumeUs.ToString("F2") +
                ", maxMainValidateUs=" + (Interlocked.Read(ref consumeTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", validatedItems=" + Interlocked.Read(ref validatedItems) +
                ", fullParallel=OFF, waits=0. Worker stage dereferences no Thing/Map/Pawn; Vanilla owns Reachability/validator/reservation/Job commit.";
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
            }
        }

        private enum Classification
        {
            MainThread,
            SnapshotParallel,
            FullParallelReserved
        }

        private sealed class SourceState
        {
            internal int SourceId;
            internal int MapId;
            internal Thing[] Members;
            internal int NeedsRefresh;
        }

        private sealed class PlanSlot
        {
            internal readonly IList<Thing> Source;
            internal readonly SourceState State;
            internal readonly string WorkGiverDefName;
            internal readonly PersistentMapSearchFabric.SourceSnapshot Snapshot;
            internal PersistentMapSearchFabric.DistancePlan Plan;
            internal int Ready;
            internal int Failed;
            internal int Consumed;

            internal PlanSlot(IList<Thing> source, SourceState state, string workGiverDefName,
                PersistentMapSearchFabric.SourceSnapshot snapshot)
            {
                Source = source;
                State = state;
                WorkGiverDefName = workGiverDefName;
                Snapshot = snapshot;
            }
        }

        private sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly Map Map;
            internal readonly IntVec3 Root;
            internal readonly Dictionary<object, PlanSlot> Plans = new Dictionary<object, PlanSlot>(ReferenceObjectComparer.Instance);

            internal PackageContext(Pawn pawn, Map map, IntVec3 root)
            {
                Pawn = pawn;
                Map = map;
                Root = root;
            }
        }

        private sealed class ReferenceObjectComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceObjectComparer Instance = new ReferenceObjectComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
