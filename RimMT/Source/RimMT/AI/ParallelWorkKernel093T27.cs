using System;
using System.Collections;
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
    /// <summary>
    /// T27.1 source-centric parallel Work Search Kernel.
    ///
    /// Lessons carried forward from T25/T26/T27:
    /// - never wait/spin/join the simulation thread for workers;
    /// - never materialize an unknown IEnumerable just to decide whether to parallelize;
    /// - never cache Job/validator/Reachability/reservation/final outcomes across packages;
    /// - never scan/classify the whole WorkGiver list merely to discover speculative work.
    ///
    /// Instead, actual Vanilla ClosestThingReachable calls teach the kernel which stable IList sources
    /// are really used. Those source identities are kept only as weak references and their membership/
    /// positions are maintained by PersistentMapSearchFabric. A later synchronous JobGiver_Work package
    /// may speculatively build a NEW root-specific distance/source-order plan for recently-used sources.
    /// The plan lifetime is strictly that one package: no completed distance plan survives the finalizer.
    ///
    /// Workers consume only immutable FabricEntry data. Before a ready plan can affect the live call, the
    /// main thread fully revalidates count, source order, spawn/map and every position. Vanilla still owns
    /// Region search, Reachability, validator, reservation and Job creation. FullParallel remains OFF.
    /// </summary>
    internal static class ParallelWorkKernel093T27
    {
        internal const string FeatureId = "parallel.workKernel";

        private const int MinSourceCount = 64;
        private const int MaxSourceCount = 4096;
        private const int MaxPlansPerPackage = 8;
        private const int MaxHotSourcesPerMap = 24;
        private const long HotWindowPackages = 48;

        private static readonly ConditionalWeakTable<object, SourceState> Sources =
            new ConditionalWeakTable<object, SourceState>();
        private static readonly ConditionalWeakTable<Map, HotMapState> HotMaps =
            new ConditionalWeakTable<Map, HotMapState>();

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;

        private static volatile bool installed;
        private static volatile bool targetAuthoritySafe = true;
        private static int nextSourceId;
        private static long nextPackageSequence;

        private static long packages;
        private static long sourceUses;
        private static long staticSourceUses;
        private static long customSourceUses;
        private static long customNonListBypass;
        private static long sourceUseSizeBypass;
        private static long sourceUseUnsafeMemberBypass;
        private static long hotSourcesAdded;
        private static long hotSourcesExpired;
        private static long hotPreheatConsidered;
        private static long hotPreheatScheduled;
        private static long hotPreheatSkippedCold;
        private static long sourceRefreshes;
        private static long sourceRegisterRejected;
        private static long snapshotMisses;

        private static long plansScheduled;
        private static long plansCompleted;
        private static long plansReadyAtPackageEnd;
        private static long plansNotReadyAtPackageEnd;
        private static long plansReadyUnusedAtPackageEnd;
        private static long plansConsumed;
        private static long plansConsumedStatic;
        private static long plansConsumedCustom;
        private static long noPlanAtUse;
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

        private static long firstUseReady;
        private static long firstUseNotReady;
        private static long firstUseWindowTicks;
        private static long firstUseWindowTicksMax;
        private static long readyLeadTicks;
        private static long readyLeadSamples;
        private static long missWindowTicks;
        private static long missWindowSamples;
        private static long useWindowLt50;
        private static long useWindow50To100;
        private static long useWindow100To250;
        private static long useWindow250To500;
        private static long useWindow500To1000;
        private static long useWindowGe1000;

        private static long foreignTargetBypass;
        private static long installFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase package = AccessTools.Method(typeof(RimWorld.JobGiver_Work), "TryIssueJobPackage");
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
                    FeatureGate.Suppress(FeatureId, "T27.1 JobGiver_Work/ClosestThingReachable target missing");
                    return;
                }

                targetAuthoritySafe = !HasForeignPatches(closest);
                harmony.Patch(package,
                    prefix: new HarmonyMethod(typeof(ParallelWorkKernel093T27), nameof(PackagePrefix)) { priority = Priority.First + 425 },
                    finalizer: new HarmonyMethod(typeof(ParallelWorkKernel093T27), nameof(PackageFinalizer)) { priority = Priority.Last - 425 });
                harmony.Patch(closest,
                    prefix: new HarmonyMethod(typeof(ParallelWorkKernel093T27), nameof(ClosestThingReachablePrefix)) { priority = Priority.First + 425 });

                installed = true;
                Log.Message("[RimMT] T27.1 Parallel Work Kernel installed. Source-centric hot-use scheduling; stable custom IList learning; package-local plans only; FullParallel OFF.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                FeatureGate.Suppress(FeatureId, "T27.1 install failure: " + ex.GetType().Name);
                Log.Warning("[RimMT] T27.1 Parallel Work Kernel install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PackagePrefix(Pawn pawn)
        {
            if (packageDepth++ != 0) return;
            current = null;

            if (!installed || !targetAuthoritySafe || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing ||
                pawn == null || !pawn.Spawned || pawn.Map == null)
                return;

            long sequence = Interlocked.Increment(ref nextPackageSequence);
            Interlocked.Increment(ref packages);
            PackageContext context = new PackageContext(pawn, pawn.Map, pawn.Position, sequence, Stopwatch.GetTimestamp());
            current = context;

            try
            {
                PreheatRecentlyUsedSources(context);
            }
            catch
            {
                // Fail open. Vanilla package execution proceeds unchanged.
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
                        PlanSlot slot = pair.Value;
                        if (slot == null) continue;
                        if (Volatile.Read(ref slot.Ready) != 0)
                        {
                            Interlocked.Increment(ref plansReadyAtPackageEnd);
                            if (Volatile.Read(ref slot.Consumed) == 0)
                                Interlocked.Increment(ref plansReadyUnusedAtPackageEnd);
                        }
                        else
                        {
                            Interlocked.Increment(ref plansNotReadyAtPackageEnd);
                        }
                    }
                }
                current = null;
            }
            return __exception;
        }

        private static void PreheatRecentlyUsedSources(PackageContext context)
        {
            HotMapState hot;
            if (!HotMaps.TryGetValue(context.Map, out hot) || hot == null || hot.Sources.Count == 0)
                return;

            for (int i = hot.Sources.Count - 1; i >= 0; i--)
            {
                SourceState state = hot.Sources[i];
                if (state == null || state.HotMapId != context.Map.uniqueID || state.SourceRef == null || !state.SourceRef.IsAlive)
                {
                    hot.Sources.RemoveAt(i);
                    Interlocked.Increment(ref hotSourcesExpired);
                    continue;
                }

                long age = context.Sequence - state.LastUsePackage;
                if (age < 0 || age > HotWindowPackages)
                {
                    hot.Sources.RemoveAt(i);
                    state.HotMapId = int.MinValue;
                    Interlocked.Increment(ref hotSourcesExpired);
                    continue;
                }
            }

            if (hot.Sources.Count == 0) return;

            hot.Sources.Sort(SourceStateComparer.Instance);
            for (int i = 0; i < hot.Sources.Count && context.Plans.Count < MaxPlansPerPackage; i++)
            {
                SourceState state = hot.Sources[i];
                Interlocked.Increment(ref hotPreheatConsidered);
                long age = context.Sequence - state.LastUsePackage;
                if (age < 0 || age > HotWindowPackages)
                {
                    Interlocked.Increment(ref hotPreheatSkippedCold);
                    continue;
                }

                object target = state.SourceRef == null ? null : state.SourceRef.Target;
                IList source = target as IList;
                if (source == null)
                    continue;

                if (TrySchedulePlan(context, source, state, state.LastUseWasCustom != 0))
                    Interlocked.Increment(ref hotPreheatScheduled);
            }
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
                !RimMTThreadGuard.IsMainThread)
                return;

            PackageContext context = current;
            if (context == null || packageDepth <= 0 || map == null || !ReferenceEquals(map, context.Map) ||
                traverseParams.pawn == null || !ReferenceEquals(traverseParams.pawn, context.Pawn) ||
                root.x != context.Root.x || root.z != context.Root.z ||
                searchRegionsMin != 0 || searchRegionsMax >= 0 ||
                traversableRegionTypes != RegionType.Set_Passable || ignoreEntirelyForbiddenRegions)
                return;

            if (!targetAuthoritySafe)
            {
                Interlocked.Increment(ref foreignTargetBypass);
                return;
            }

            bool isCustom = customGlobalSearchSet != null;
            IList source = null;
            if (isCustom)
            {
                source = customGlobalSearchSet as IList;
                if (source == null)
                {
                    Interlocked.Increment(ref customNonListBypass);
                    return;
                }
            }
            else
            {
                if (thingReq.IsUndefined) return;
                try { source = map.listerThings.ThingsMatching(thingReq) as IList; }
                catch { return; }
                if (source == null) return;
            }

            int count;
            try { count = source.Count; }
            catch { return; }
            if (count < MinSourceCount || count > MaxSourceCount)
            {
                Interlocked.Increment(ref sourceUseSizeBypass);
                return;
            }

            SourceState state = Sources.GetValue(source, CreateSourceState);
            Interlocked.Increment(ref sourceUses);
            if (isCustom) Interlocked.Increment(ref customSourceUses);
            else Interlocked.Increment(ref staticSourceUses);

            state.LastUsePackage = context.Sequence;
            state.UseCount++;
            state.LastUseWasCustom = isCustom ? 1 : 0;
            AddHotSource(map, state);

            // Seed/refresh persistent membership on actual Vanilla use. This copies only known IList
            // members; unknown/dynamic IEnumerable sources are never consumed by RimMT.
            if (!EnsureRegistered(map, source, state))
            {
                Interlocked.Increment(ref noPlanAtUse);
                return;
            }

            PlanSlot slot;
            if (!context.Plans.TryGetValue(source, out slot) || slot == null)
            {
                Interlocked.Increment(ref noPlanAtUse);
                // Same-package repeated calls may benefit if this fresh plan completes later. The current
                // call never waits and proceeds through untouched Vanilla code.
                TrySchedulePlan(context, source, state, isCustom);
                return;
            }

            long useTicks = Stopwatch.GetTimestamp();
            RecordFirstUse(slot, useTicks);

            if (Volatile.Read(ref slot.Ready) == 0 || slot.Plan == null || Volatile.Read(ref slot.Failed) != 0)
            {
                Interlocked.Increment(ref planNotReadyAtUse);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            bool valid = ValidatePlan(map, source, slot, context.Root);
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
            if (isCustom) Interlocked.Increment(ref plansConsumedCustom);
            else Interlocked.Increment(ref plansConsumedStatic);
            Volatile.Write(ref slot.Consumed, 1);
        }

        private static void AddHotSource(Map map, SourceState state)
        {
            if (map == null || state == null) return;
            HotMapState hot = HotMaps.GetValue(map, CreateHotMapState);
            if (state.HotMapId == map.uniqueID)
                return;

            state.HotMapId = map.uniqueID;
            hot.Sources.Add(state);
            Interlocked.Increment(ref hotSourcesAdded);

            if (hot.Sources.Count > MaxHotSourcesPerMap)
            {
                hot.Sources.Sort(SourceStateComparer.Instance);
                while (hot.Sources.Count > MaxHotSourcesPerMap)
                {
                    int last = hot.Sources.Count - 1;
                    SourceState removed = hot.Sources[last];
                    hot.Sources.RemoveAt(last);
                    if (removed != null && removed.HotMapId == map.uniqueID)
                        removed.HotMapId = int.MinValue;
                    Interlocked.Increment(ref hotSourcesExpired);
                }
            }
        }

        private static bool TrySchedulePlan(PackageContext context, IList source, SourceState state, bool isCustom)
        {
            if (context == null || source == null || state == null || context.Plans.Count >= MaxPlansPerPackage)
                return false;
            if (context.Plans.ContainsKey(source))
                return true;

            int count;
            try { count = source.Count; }
            catch { return false; }
            if (count < MinSourceCount || count > MaxSourceCount)
                return false;

            if (!EnsureRegistered(context.Map, source, state))
                return false;

            PersistentMapSearchFabric.SourceSnapshot snapshot;
            if (!PersistentMapSearchFabric.TryGetSourceSnapshot(context.Map, state.SourceId, out snapshot) ||
                snapshot == null || !snapshot.Complete || snapshot.Count != count)
            {
                Interlocked.Increment(ref snapshotMisses);
                return false;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || scheduler.ProductionPending > 24)
            {
                Interlocked.Increment(ref schedulerRejected);
                return false;
            }

            PlanSlot slot = new PlanSlot(source, state, snapshot, isCustom, Stopwatch.GetTimestamp());
            context.Plans.Add(source, slot);
            IntVec3 root = context.Root;

            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.High, delegate
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    PersistentMapSearchFabric.DistancePlan plan = snapshot.BuildDistancePlan(root.x, root.z);
                    slot.Plan = plan;
                    if (plan != null)
                        Interlocked.Add(ref planItems, plan.Count);
                    slot.ReadyTicks = Stopwatch.GetTimestamp();
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
            {
                Interlocked.Increment(ref plansScheduled);
                return true;
            }

            context.Plans.Remove(source);
            Interlocked.Increment(ref schedulerRejected);
            return false;
        }

        private static bool EnsureRegistered(Map map, IList source, SourceState state)
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
                    Thing thing = source[i] as Thing;
                    if (thing == null || thing is Pawn || !thing.Spawned || thing.MapHeld != map)
                    {
                        Interlocked.Increment(ref sourceUseUnsafeMemberBypass);
                        return false;
                    }
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(map))
                    {
                        Interlocked.Increment(ref sourceUseUnsafeMemberBypass);
                        return false;
                    }
                    members[i] = thing;
                }
            }
            catch
            {
                Interlocked.Increment(ref sourceUseUnsafeMemberBypass);
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
            return false; // publication is asynchronous; current live call always stays Vanilla.
        }

        private static bool QuickMembershipMatches(IList source, Thing[] members)
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

        private static bool ValidatePlan(Map map, IList source, PlanSlot slot, IntVec3 root)
        {
            SourceState state = slot.State;
            PersistentMapSearchFabric.DistancePlan plan = slot.Plan;
            Thing[] members = state.Members;
            if (state.MapId != map.uniqueID || members == null || plan == null ||
                plan.MapId != map.uniqueID || plan.Width != map.Size.x || plan.Height != map.Size.z ||
                plan.RootX != root.x || plan.RootZ != root.z || source.Count != members.Length ||
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

        private static void RecordFirstUse(PlanSlot slot, long useTicks)
        {
            if (slot == null || Interlocked.CompareExchange(ref slot.FirstUseRecorded, 1, 0) != 0)
                return;

            slot.FirstUseTicks = useTicks;
            long window = Math.Max(0L, useTicks - slot.ScheduledTicks);
            Interlocked.Add(ref firstUseWindowTicks, window);
            UpdateMax(ref firstUseWindowTicksMax, window);
            RecordUseWindowBucket(window);

            if (Volatile.Read(ref slot.Ready) != 0 && slot.ReadyTicks > 0L && slot.ReadyTicks <= useTicks)
            {
                Interlocked.Increment(ref firstUseReady);
                long lead = useTicks - slot.ReadyTicks;
                Interlocked.Add(ref readyLeadTicks, lead);
                Interlocked.Increment(ref readyLeadSamples);
            }
            else
            {
                Interlocked.Increment(ref firstUseNotReady);
                Interlocked.Add(ref missWindowTicks, window);
                Interlocked.Increment(ref missWindowSamples);
            }
        }

        private static void RecordUseWindowBucket(long ticks)
        {
            double us = ticks * 1000000.0 / Stopwatch.Frequency;
            if (us < 50.0) Interlocked.Increment(ref useWindowLt50);
            else if (us < 100.0) Interlocked.Increment(ref useWindow50To100);
            else if (us < 250.0) Interlocked.Increment(ref useWindow100To250);
            else if (us < 500.0) Interlocked.Increment(ref useWindow250To500);
            else if (us < 1000.0) Interlocked.Increment(ref useWindow500To1000);
            else Interlocked.Increment(ref useWindowGe1000);
        }

        private static SourceState CreateSourceState(object source)
        {
            int id = Interlocked.Increment(ref nextSourceId);
            if (id == 0) id = Interlocked.Increment(ref nextSourceId);
            return new SourceState
            {
                SourceId = id,
                MapId = int.MinValue,
                HotMapId = int.MinValue,
                SourceRef = new WeakReference(source)
            };
        }

        private static HotMapState CreateHotMapState(Map map)
        {
            return new HotMapState();
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
            long firstReady = Interlocked.Read(ref firstUseReady);
            long firstMiss = Interlocked.Read(ref firstUseNotReady);
            long firstTotal = firstReady + firstMiss;
            long leadSamples = Interlocked.Read(ref readyLeadSamples);
            long missSamples = Interlocked.Read(ref missWindowSamples);
            double avgItems = completed == 0 ? 0.0 : Interlocked.Read(ref planItems) / (double)completed;
            double avgBuildUs = completed == 0 ? 0.0 :
                Interlocked.Read(ref buildTicks) * 1000000.0 / Stopwatch.Frequency / completed;
            double avgConsumeUs = consumed == 0 ? 0.0 :
                Interlocked.Read(ref consumeTicks) * 1000000.0 / Stopwatch.Frequency / consumed;
            double avgFirstUseUs = firstTotal == 0 ? 0.0 :
                Interlocked.Read(ref firstUseWindowTicks) * 1000000.0 / Stopwatch.Frequency / firstTotal;
            double avgReadyLeadUs = leadSamples == 0 ? 0.0 :
                Interlocked.Read(ref readyLeadTicks) * 1000000.0 / Stopwatch.Frequency / leadSamples;
            double avgMissWindowUs = missSamples == 0 ? 0.0 :
                Interlocked.Read(ref missWindowTicks) * 1000000.0 / Stopwatch.Frequency / missSamples;

            return "T27.1 source-centric parallel work kernel: installed=" + installed +
                ", authoritySafe=" + targetAuthoritySafe +
                ", packages=" + Interlocked.Read(ref packages) +
                ", sourceUses[all/static/custom/nonList/size/unsafeMember]=" +
                Interlocked.Read(ref sourceUses) + "/" + Interlocked.Read(ref staticSourceUses) + "/" +
                Interlocked.Read(ref customSourceUses) + "/" + Interlocked.Read(ref customNonListBypass) + "/" +
                Interlocked.Read(ref sourceUseSizeBypass) + "/" + Interlocked.Read(ref sourceUseUnsafeMemberBypass) +
                ", hot[added/expired/considered/scheduled/coldSkip]=" +
                Interlocked.Read(ref hotSourcesAdded) + "/" + Interlocked.Read(ref hotSourcesExpired) + "/" +
                Interlocked.Read(ref hotPreheatConsidered) + "/" + Interlocked.Read(ref hotPreheatScheduled) + "/" +
                Interlocked.Read(ref hotPreheatSkippedCold) +
                ", sourceRefreshes=" + Interlocked.Read(ref sourceRefreshes) +
                ", sourceRegisterRejected=" + Interlocked.Read(ref sourceRegisterRejected) +
                ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) +
                ", plans[scheduled/completed/consumed(static/custom)/noPlanUse/notReadyUse/validationFail]=" +
                scheduled + "/" + completed + "/" + consumed + "(" + Interlocked.Read(ref plansConsumedStatic) + "/" +
                Interlocked.Read(ref plansConsumedCustom) + ")/" + Interlocked.Read(ref noPlanAtUse) + "/" +
                Interlocked.Read(ref planNotReadyAtUse) + "/" + Interlocked.Read(ref planValidationFailed) +
                ", firstUse[ready/notReady]=" + firstReady + "/" + firstMiss +
                ", avgScheduleToFirstUseUs=" + avgFirstUseUs.ToString("F2") +
                ", maxScheduleToFirstUseUs=" + (Interlocked.Read(ref firstUseWindowTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", avgReadyLeadUs=" + avgReadyLeadUs.ToString("F2") +
                ", avgMissWindowUs=" + avgMissWindowUs.ToString("F2") +
                ", useWindowUs[<50/50-100/100-250/250-500/500-1000/>=1000]=" +
                Interlocked.Read(ref useWindowLt50) + "/" + Interlocked.Read(ref useWindow50To100) + "/" +
                Interlocked.Read(ref useWindow100To250) + "/" + Interlocked.Read(ref useWindow250To500) + "/" +
                Interlocked.Read(ref useWindow500To1000) + "/" + Interlocked.Read(ref useWindowGe1000) +
                ", endReady/notReady/readyUnused=" + Interlocked.Read(ref plansReadyAtPackageEnd) + "/" +
                Interlocked.Read(ref plansNotReadyAtPackageEnd) + "/" + Interlocked.Read(ref plansReadyUnusedAtPackageEnd) +
                ", schedulerRejected=" + Interlocked.Read(ref schedulerRejected) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ", avgItems=" + avgItems.ToString("F1") +
                ", avgWorkerBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxWorkerBuildUs=" + (Interlocked.Read(ref buildTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", avgMainValidateUs=" + avgConsumeUs.ToString("F2") +
                ", maxMainValidateUs=" + (Interlocked.Read(ref consumeTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", validatedItems=" + Interlocked.Read(ref validatedItems) +
                ", fullParallel=OFF, waits=0, crossPackagePlanCache=OFF. Only source-use history crosses packages; every distance plan is rebuilt for the current root and dies with that synchronous package.";
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
            }
        }

        private sealed class SourceState
        {
            internal int SourceId;
            internal int MapId;
            internal Thing[] Members;
            internal int NeedsRefresh;
            internal WeakReference SourceRef;
            internal long LastUsePackage;
            internal long UseCount;
            internal int LastUseWasCustom;
            internal int HotMapId;
        }

        private sealed class HotMapState
        {
            internal readonly List<SourceState> Sources = new List<SourceState>();
        }

        private sealed class SourceStateComparer : IComparer<SourceState>
        {
            internal static readonly SourceStateComparer Instance = new SourceStateComparer();
            public int Compare(SourceState a, SourceState b)
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                int recent = b.LastUsePackage.CompareTo(a.LastUsePackage);
                if (recent != 0) return recent;
                return b.UseCount.CompareTo(a.UseCount);
            }
        }

        private sealed class PlanSlot
        {
            internal readonly IList Source;
            internal readonly SourceState State;
            internal readonly PersistentMapSearchFabric.SourceSnapshot Snapshot;
            internal readonly bool IsCustom;
            internal readonly long ScheduledTicks;
            internal PersistentMapSearchFabric.DistancePlan Plan;
            internal long ReadyTicks;
            internal long FirstUseTicks;
            internal int Ready;
            internal int Failed;
            internal int Consumed;
            internal int FirstUseRecorded;

            internal PlanSlot(IList source, SourceState state, PersistentMapSearchFabric.SourceSnapshot snapshot,
                bool isCustom, long scheduledTicks)
            {
                Source = source;
                State = state;
                Snapshot = snapshot;
                IsCustom = isCustom;
                ScheduledTicks = scheduledTicks;
            }
        }

        private sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly Map Map;
            internal readonly IntVec3 Root;
            internal readonly long Sequence;
            internal readonly long StartTicks;
            internal readonly Dictionary<object, PlanSlot> Plans = new Dictionary<object, PlanSlot>(ReferenceObjectComparer.Instance);

            internal PackageContext(Pawn pawn, Map map, IntVec3 root, long sequence, long startTicks)
            {
                Pawn = pawn;
                Map = map;
                Root = root;
                Sequence = sequence;
                StartTicks = startTicks;
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
