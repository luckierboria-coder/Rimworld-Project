using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T26 first production consumer of the simulation-epoch contract.
    ///
    /// The important scheduling change versus T25 is that work starts BEFORE the relevant
    /// GenClosest call. At the outer JobGiver_Work package boundary the main thread snapshots a
    /// deliberately tiny whitelist of historically-heavy scanners into primitive arrays and
    /// submits the independent packets as one scheduler batch. Workers only read x/z, source
    /// index and a byte negative mask; Thing/Pawn/Map references stay in a main-thread slot and
    /// are never dereferenced by workers.
    ///
    /// If the current package later reaches the matching 13-arg ClosestThingReachable and the
    /// packet is already published, source membership + positions + epoch are validated on the
    /// main thread. Only snapshot-proven hard negatives may be omitted; every survivor still runs
    /// the original live validator and live CanReach. No wait is ever introduced. A cold/not-ready
    /// packet simply falls through to T24.1/Vanilla.
    /// </summary>
    internal static class ParallelWorkSearch093T26
    {
        internal const string FeatureId = "parallel.workSearchEpoch";

        private const int MinSourceCount = 64;
        private const int MaxSourceCount = 2048;
        private const int MaxPlansPerPackage = 2;
        private const int WarmupNegativeMatches = 16;
        private const int VerifyMask = 31;
        private const int MinPackageAgeMs = 8;
        private static readonly long MinPackageAgeTicks = Math.Max(1L, Stopwatch.Frequency * MinPackageAgeMs / 1000L);

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;

        private static MethodBase packageTarget;
        private static MethodBase reachableTarget;
        private static MethodBase repairHasJobTarget;
        private static MethodBase refuelHasJobTarget;
        private static MethodBase refuelUtilityTarget;

        private static volatile bool compatibilityReady;
        private static volatile bool queryAuthoritySafe;
        private static volatile bool repairAuthoritySafe;
        private static volatile bool refuelAuthoritySafe;
        private static int installed;
        private static int installFailures;

        private static readonly TrustState RepairTrust = new TrustState();
        private static readonly TrustState RefuelTrust = new TrustState();
        private static readonly Dictionary<Type, FieldInfo> ScannerFields = new Dictionary<Type, FieldInfo>();
        private static readonly object ScannerFieldLock = new object();

        private static long epochBegins;
        private static long epochEnds;
        private static long packages;
        private static long nestedPackages;
        private static long captureAttempts;
        private static long captureAccepted;
        private static long captureRejectedSmall;
        private static long captureRejectedLarge;
        private static long captureRejectedShape;
        private static long captureCandidates;
        private static long captureNegatives;
        private static long captureTicks;
        private static long captureTicksMax;
        private static long batchesSubmitted;
        private static long batchSubmitRejected;
        private static long plansScheduled;
        private static long plansPublished;
        private static long plansLateDropped;
        private static long workerFailures;
        private static long workerTicks;
        private static long workerTicksMax;
        private static long queryObserved;
        private static long queryScopeBypass;
        private static long queryScannerBypass;
        private static long queryAuthorityBypass;
        private static long queryNoPlan;
        private static long queryNotReady;
        private static long queryEpochMismatch;
        private static long querySourceMismatch;
        private static long queryPositionMismatch;
        private static long queryWarmupBypass;
        private static long queryParitySamples;
        private static long queryParityMatches;
        private static long queryParityMismatches;
        private static long queryAuthoritative;
        private static long queryAuthoritativeNull;
        private static long liveValidatorCalls;
        private static long liveValidatorRejects;
        private static long liveReachCalls;
        private static long liveReachRejects;
        private static long negativesSkipped;
        private static long repairAuthoritative;
        private static long refuelAuthoritative;
        private static long validateTicks;
        private static long validateTicksMax;
        private static long sourceEnumerated;

        private enum PlanKind : byte
        {
            None = 0,
            Repair = 1,
            Refuel = 2
        }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                packageTarget = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                reachableTarget = FindExactReachable13();
                repairHasJobTarget = AccessTools.Method(typeof(WorkGiver_Repair), nameof(WorkGiver_Repair.HasJobOnThing),
                    new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                refuelHasJobTarget = AccessTools.Method(typeof(WorkGiver_Refuel), nameof(WorkGiver_Refuel.HasJobOnThing),
                    new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                refuelUtilityTarget = AccessTools.Method(typeof(RefuelWorkGiverUtility), nameof(RefuelWorkGiverUtility.CanRefuel),
                    new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });

                if (packageTarget == null || reachableTarget == null || repairHasJobTarget == null ||
                    refuelHasJobTarget == null || refuelUtilityTarget == null)
                {
                    FeatureGate.Suppress(FeatureId, "required T26 target missing");
                    return;
                }

                HarmonyMethod packagePrefix = new HarmonyMethod(typeof(ParallelWorkSearch093T26), nameof(PackagePrefix));
                packagePrefix.priority = Priority.First + 220;
                HarmonyMethod packageFinalizer = new HarmonyMethod(typeof(ParallelWorkSearch093T26), nameof(PackageFinalizer));
                packageFinalizer.priority = Priority.Last - 220;
                harmony.Patch(packageTarget, prefix: packagePrefix, finalizer: packageFinalizer);

                HarmonyMethod queryPrefix = new HarmonyMethod(typeof(ParallelWorkSearch093T26), nameof(ReachablePrefix));
                queryPrefix.priority = Priority.First + 220;
                harmony.Patch(reachableTarget, prefix: queryPrefix);

                Volatile.Write(ref installed, 1);
                Log.Message("[RimMT] T26 Parallel Work Search installed. Repair/Refuel packets are captured at JobGiver package entry, computed speculatively by worker batches, and consumed only in the same simulation epoch after exact main-thread source/position validation. No worker wait; Vanilla remains final authority for survivors.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref installFailures);
                FeatureGate.Suppress(FeatureId, "T26 install failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] T26 Parallel Work Search failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            if (compatibilityReady) return;
            try
            {
                queryAuthoritySafe = !HasForeignPatch(reachableTarget);
                repairAuthoritySafe = !HasForeignPatch(repairHasJobTarget);
                refuelAuthoritySafe = !HasForeignPatch(refuelHasJobTarget) && !HasForeignPatch(refuelUtilityTarget);
                compatibilityReady = true;
                Log.Message("[RimMT] T26 compatibility: query=" + queryAuthoritySafe +
                    ", Repair=" + repairAuthoritySafe + ", Refuel=" + refuelAuthoritySafe +
                    ". A foreign Harmony owner disables only the affected authoritative packet kind; fallback stays T24.1/Vanilla.");
            }
            catch (Exception ex)
            {
                queryAuthoritySafe = false;
                repairAuthoritySafe = false;
                refuelAuthoritySafe = false;
                compatibilityReady = true;
                Log.Warning("[RimMT] T26 compatibility audit failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void OnEpochBegin(long epoch, int gameTick)
        {
            if (epoch != 0L) Interlocked.Increment(ref epochBegins);
        }

        internal static void OnEpochEnd(long epoch, int gameTick)
        {
            if (epoch != 0L) Interlocked.Increment(ref epochEnds);
        }

        public static void PackagePrefix(JobGiver_Work __instance, Pawn __0, ref PackageState __state)
        {
            __state = default(PackageState);
            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || __instance == null || __0 == null ||
                __0.Map == null || __0.workSettings == null || !SimulationEpochCoordinator093T26.InSimulationTick)
                return;

            __state.Entered = true;
            __state.Outermost = packageDepth == 0;
            packageDepth++;
            if (!__state.Outermost)
            {
                __state.Context = current;
                Interlocked.Increment(ref nestedPackages);
                return;
            }

            PackageContext context = new PackageContext(
                __0,
                __0.Map,
                SimulationEpochCoordinator093T26.CurrentEpoch,
                SimulationEpochCoordinator093T26.CurrentGameTick,
                Stopwatch.GetTimestamp(),
                Interlocked.Increment(ref PackageContext.SequenceSource));
            current = context;
            __state.Context = context;
            Interlocked.Increment(ref packages);
            PrepareAndSchedule(__instance, __0, context);
        }

        public static Exception PackageFinalizer(Exception __exception, PackageState __state)
        {
            if (!__state.Entered) return __exception;
            if (packageDepth > 0) packageDepth--;
            if (__state.Outermost)
            {
                PackageContext context = __state.Context;
                if (context != null) Volatile.Write(ref context.Active, 0);
                if (ReferenceEquals(current, context)) current = null;
            }
            return __exception;
        }

        private static void PrepareAndSchedule(JobGiver_Work giver, Pawn pawn, PackageContext context)
        {
            if (!queryAuthoritySafe || context == null || pawn == null || pawn.Map == null) return;
            List<WorkGiver> workGivers;
            try { workGivers = giver.emergency ? pawn.workSettings.WorkGiversInOrderEmergency : pawn.workSettings.WorkGiversInOrderNormal; }
            catch { return; }
            if (workGivers == null || workGivers.Count == 0) return;

            List<Action> actions = new List<Action>(MaxPlansPerPackage);
            for (int i = 0; i < workGivers.Count && actions.Count < MaxPlansPerPackage; i++)
            {
                WorkGiver_Scanner scanner = workGivers[i] as WorkGiver_Scanner;
                if (scanner == null || scanner.def == null || !scanner.def.scanThings) continue;

                PlanKind kind = KindFor(scanner);
                if (kind == PlanKind.None || !KindAuthoritySafe(kind)) continue;
                if (context.GetSlot(kind) != null) continue;

                bool prioritized;
                bool allowUnreachable;
                try
                {
                    prioritized = scanner.Prioritized;
                    allowUnreachable = scanner.AllowUnreachable;
                }
                catch { continue; }
                if (prioritized || allowUnreachable) continue;

                PlanSlot slot = CaptureSlot(kind, scanner, pawn, context);
                if (slot == null) continue;
                context.SetSlot(kind, slot);
                actions.Add(delegate { BuildPlanWorker(context, slot); });
            }

            if (actions.Count == 0) return;
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || !scheduler.TryEnqueueBatch(FeatureId, JobPriority.High, actions.ToArray()))
            {
                Interlocked.Increment(ref batchSubmitRejected);
                return;
            }

            Interlocked.Increment(ref batchesSubmitted);
            Interlocked.Add(ref plansScheduled, actions.Count);
        }

        private static PlanSlot CaptureSlot(PlanKind kind, WorkGiver_Scanner scanner, Pawn pawn, PackageContext context)
        {
            Interlocked.Increment(ref captureAttempts);
            long started = Stopwatch.GetTimestamp();
            try
            {
                IEnumerable<Thing> source = scanner.PotentialWorkThingsGlobal(pawn);
                if (source == null)
                    source = pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
                ICollection collection = source as ICollection;
                if (collection == null)
                {
                    Interlocked.Increment(ref captureRejectedShape);
                    return null;
                }

                int count = collection.Count;
                if (count < MinSourceCount)
                {
                    Interlocked.Increment(ref captureRejectedSmall);
                    return null;
                }
                if (count > MaxSourceCount)
                {
                    Interlocked.Increment(ref captureRejectedLarge);
                    return null;
                }

                Thing[] members = new Thing[count];
                int[] xs = new int[count];
                int[] zs = new int[count];
                byte[] negatives = new byte[count];
                int index = 0;
                int negativeCount = 0;
                foreach (Thing thing in source)
                {
                    if (index >= count || thing == null || !thing.Spawned || thing.MapHeld != pawn.Map)
                    {
                        Interlocked.Increment(ref captureRejectedShape);
                        return null;
                    }
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(pawn.Map))
                    {
                        Interlocked.Increment(ref captureRejectedShape);
                        return null;
                    }

                    members[index] = thing;
                    xs[index] = pos.x;
                    zs[index] = pos.z;
                    bool negative = CaptureHardNegative(kind, pawn, thing, pos);
                    negatives[index] = negative ? (byte)1 : (byte)0;
                    if (negative) negativeCount++;
                    index++;
                }
                if (index != count)
                {
                    Interlocked.Increment(ref captureRejectedShape);
                    return null;
                }

                Interlocked.Increment(ref captureAccepted);
                Interlocked.Add(ref captureCandidates, count);
                Interlocked.Add(ref captureNegatives, negativeCount);
                return new PlanSlot(kind, scanner, context.Epoch, context.Sequence, pawn.Map.uniqueID,
                    pawn.Position.x, pawn.Position.z, members, xs, zs, negatives, negativeCount);
            }
            catch
            {
                Interlocked.Increment(ref captureRejectedShape);
                return null;
            }
            finally
            {
                RecordElapsed(ref captureTicks, ref captureTicksMax, started);
            }
        }

        private static bool CaptureHardNegative(PlanKind kind, Pawn pawn, Thing thing, IntVec3 pos)
        {
            if (kind == PlanKind.Repair)
            {
                Building building = thing as Building;
                if (building == null || building.def == null || building.def.building == null || !building.def.building.repairable)
                    return true;
                if (!ReferenceEquals(thing.Faction, pawn.Faction)) return true;
                if (pawn.Faction == Faction.OfPlayer)
                {
                    Area_Home home = pawn.Map.areaManager == null ? null : pawn.Map.areaManager.Home;
                    if (home == null || !home[pos]) return true;
                }
                if (!thing.def.useHitPoints || thing.HitPoints >= thing.MaxHitPoints) return true;
                DesignationManager dm = pawn.Map.designationManager;
                if (dm != null)
                {
                    if (dm.DesignationOn(building, DesignationDefOf.Deconstruct) != null) return true;
                    if (building.def.mineable && dm.DesignationAt(pos, DesignationDefOf.Mine) != null) return true;
                }
                if (building.IsBurning()) return true;
                return false;
            }

            if (kind == PlanKind.Refuel)
            {
                if (thing is Building_Turret) return true;
                CompRefuelable comp = thing.TryGetComp<CompRefuelable>();
                if (comp == null || comp.IsFull || !comp.ShouldAutoRefuelNow) return true;
                if (thing.IsForbidden(pawn)) return true;
                if (!ReferenceEquals(thing.Faction, pawn.Faction)) return true;
                return false;
            }

            return false;
        }

        // WORKER BOUNDARY: only PlanSlot-owned primitive arrays are read below. The Thing[] held by
        // the slot is intentionally not passed into this method and is never dereferenced here.
        private static void BuildPlanWorker(PackageContext context, PlanSlot slot)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (context == null || slot == null || Volatile.Read(ref context.Active) == 0)
                {
                    Interlocked.Increment(ref plansLateDropped);
                    return;
                }

                int count = slot.Xs.Length;
                CandidateKey[] keys = new CandidateKey[count - slot.NegativeCount];
                int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    if (slot.Negatives[i] != 0) continue;
                    long dx = (long)slot.Xs[i] - slot.RootX;
                    long dz = (long)slot.Zs[i] - slot.RootZ;
                    keys[kept++] = new CandidateKey(i, dx * dx + dz * dz);
                }
                if (kept > 1) Array.Sort(keys, 0, kept, CandidateKeyComparer.Instance);

                int[] ordered = new int[kept];
                for (int i = 0; i < kept; i++) ordered[i] = keys[i].Index;

                if (Volatile.Read(ref context.Active) == 0)
                {
                    Interlocked.Increment(ref plansLateDropped);
                    return;
                }
                Volatile.Write(ref slot.Published, new PublishedPlan(slot.Epoch, slot.Sequence, ordered));
                Interlocked.Increment(ref plansPublished);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
            finally
            {
                RecordElapsed(ref workerTicks, ref workerTicksMax, started);
            }
        }

        public static bool ReachablePrefix(object[] __args, ref Thing __result)
        {
            Interlocked.Increment(ref queryObserved);
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || !compatibilityReady || !queryAuthoritySafe ||
                !FeatureGate.IsEnabled(FeatureId) || __args == null || __args.Length != 13 ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
            {
                if (!queryAuthoritySafe) Interlocked.Increment(ref queryAuthorityBypass);
                return true;
            }

            if (Stopwatch.GetTimestamp() - context.StartTicks < MinPackageAgeTicks)
            {
                Interlocked.Increment(ref queryScopeBypass);
                return true;
            }

            Predicate<Thing> validator = __args[6] as Predicate<Thing>;
            WorkGiver_Scanner scanner = TryResolveScanner(validator);
            PlanKind kind = KindFor(scanner);
            if (kind == PlanKind.None || !KindAuthoritySafe(kind) || !ReferenceEquals(scanner, context.GetScanner(kind)))
            {
                Interlocked.Increment(ref queryScannerBypass);
                return true;
            }

            PlanSlot slot = context.GetSlot(kind);
            if (slot == null)
            {
                Interlocked.Increment(ref queryNoPlan);
                return true;
            }
            PublishedPlan plan = Volatile.Read(ref slot.Published);
            if (plan == null)
            {
                Interlocked.Increment(ref queryNotReady);
                return true;
            }
            if (plan.Epoch != context.Epoch || plan.Sequence != context.Sequence ||
                context.Epoch != SimulationEpochCoordinator093T26.CurrentEpoch)
            {
                Interlocked.Increment(ref queryEpochMismatch);
                return true;
            }

            IntVec3 root;
            Map map;
            PathEndMode endMode;
            TraverseParms traverse;
            float maxDistance;
            IEnumerable<Thing> custom;
            try
            {
                root = (IntVec3)__args[0];
                map = __args[1] as Map;
                endMode = (PathEndMode)__args[3];
                traverse = (TraverseParms)__args[4];
                maxDistance = Convert.ToSingle(__args[5]);
                custom = __args[7] as IEnumerable<Thing>;
            }
            catch { return true; }

            if (map == null || !ReferenceEquals(map, context.Map) || !ReferenceEquals(traverse.pawn, context.Pawn) ||
                root.x != slot.RootX || root.z != slot.RootZ || maxDistance <= 0f)
                return true;

            IEnumerable<Thing> source = custom;
            if (source == null)
            {
                try { source = map.listerThings.ThingsMatching((ThingRequest)__args[2]); }
                catch { return true; }
            }

            long validateStart = Stopwatch.GetTimestamp();
            bool sourceValid = ValidateSource(slot, source, map);
            RecordElapsed(ref validateTicks, ref validateTicksMax, validateStart);
            if (!sourceValid) return true;

            TrustState trust = kind == PlanKind.Repair ? RepairTrust : RefuelTrust;
            if (!PrepareTrust(trust, slot, validator))
            {
                Interlocked.Increment(ref queryWarmupBypass);
                return true;
            }

            double maxSq = (double)maxDistance * maxDistance;
            int localValidatorCalls = 0;
            int localValidatorRejects = 0;
            int localReachCalls = 0;
            int localReachRejects = 0;
            int[] ordered = plan.OrderedIndices;
            for (int i = 0; i < ordered.Length; i++)
            {
                int index = ordered[i];
                long dx = (long)slot.Xs[index] - root.x;
                long dz = (long)slot.Zs[index] - root.z;
                if ((double)(dx * dx + dz * dz) > maxSq) break;

                Thing thing = slot.Members[index];
                if (validator != null)
                {
                    localValidatorCalls++;
                    if (!validator(thing))
                    {
                        localValidatorRejects++;
                        continue;
                    }
                }

                localReachCalls++;
                if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverse))
                {
                    localReachRejects++;
                    continue;
                }

                RecordAuthoritative(kind, slot.NegativeCount, localValidatorCalls, localValidatorRejects,
                    localReachCalls, localReachRejects, false);
                __result = thing;
                return false;
            }

            RecordAuthoritative(kind, slot.NegativeCount, localValidatorCalls, localValidatorRejects,
                localReachCalls, localReachRejects, true);
            __result = null;
            return false;
        }

        private static bool ValidateSource(PlanSlot slot, IEnumerable<Thing> source, Map map)
        {
            ICollection collection = source as ICollection;
            if (slot == null || source == null || collection == null || collection.Count != slot.Members.Length)
            {
                Interlocked.Increment(ref querySourceMismatch);
                return false;
            }

            int index = 0;
            try
            {
                foreach (Thing thing in source)
                {
                    Interlocked.Increment(ref sourceEnumerated);
                    if (index >= slot.Members.Length || !ReferenceEquals(thing, slot.Members[index]))
                    {
                        Interlocked.Increment(ref querySourceMismatch);
                        return false;
                    }
                    if (thing == null || !thing.Spawned || thing.MapHeld != map)
                    {
                        Interlocked.Increment(ref queryPositionMismatch);
                        return false;
                    }
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || pos.x != slot.Xs[index] || pos.z != slot.Zs[index])
                    {
                        Interlocked.Increment(ref queryPositionMismatch);
                        return false;
                    }
                    index++;
                }
            }
            catch
            {
                Interlocked.Increment(ref querySourceMismatch);
                return false;
            }
            if (index != slot.Members.Length)
            {
                Interlocked.Increment(ref querySourceMismatch);
                return false;
            }
            return true;
        }

        private static bool PrepareTrust(TrustState trust, PlanSlot slot, Predicate<Thing> validator)
        {
            if (trust == null || trust.Quarantined || validator == null) return false;
            if (slot.NegativeCount <= 0) return trust.Matches >= WarmupNegativeMatches;

            bool mustVerify = trust.Matches < WarmupNegativeMatches || ((++trust.UseSerial) & VerifyMask) == 0;
            if (!mustVerify) return true;

            int samples = trust.Matches < WarmupNegativeMatches ? 4 : 1;
            int checkedCount = 0;
            for (int i = 0; i < slot.Negatives.Length && checkedCount < samples; i++)
            {
                if (slot.Negatives[i] == 0) continue;
                Interlocked.Increment(ref queryParitySamples);
                checkedCount++;
                bool live;
                try { live = validator(slot.Members[i]); }
                catch { return false; }
                if (live)
                {
                    trust.Quarantined = true;
                    trust.Matches = 0;
                    Interlocked.Increment(ref queryParityMismatches);
                    return false;
                }
                trust.Matches++;
                Interlocked.Increment(ref queryParityMatches);
            }

            return trust.Matches >= WarmupNegativeMatches && !trust.Quarantined;
        }

        private static void RecordAuthoritative(PlanKind kind, int skipped, int validatorCalls, int validatorRejects,
            int reachCalls, int reachRejects, bool isNull)
        {
            Interlocked.Increment(ref queryAuthoritative);
            if (isNull) Interlocked.Increment(ref queryAuthoritativeNull);
            Interlocked.Add(ref negativesSkipped, skipped);
            Interlocked.Add(ref liveValidatorCalls, validatorCalls);
            Interlocked.Add(ref liveValidatorRejects, validatorRejects);
            Interlocked.Add(ref liveReachCalls, reachCalls);
            Interlocked.Add(ref liveReachRejects, reachRejects);
            if (kind == PlanKind.Repair) Interlocked.Increment(ref repairAuthoritative);
            else if (kind == PlanKind.Refuel) Interlocked.Increment(ref refuelAuthoritative);
        }

        private static PlanKind KindFor(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return PlanKind.None;
            Type t = scanner.GetType();
            if (t == typeof(WorkGiver_Repair)) return PlanKind.Repair;
            if (t == typeof(WorkGiver_Refuel)) return PlanKind.Refuel;
            return PlanKind.None;
        }

        private static bool KindAuthoritySafe(PlanKind kind)
        {
            if (kind == PlanKind.Repair) return repairAuthoritySafe && !RepairTrust.Quarantined;
            if (kind == PlanKind.Refuel) return refuelAuthoritySafe && !RefuelTrust.Quarantined;
            return false;
        }

        private static WorkGiver_Scanner TryResolveScanner(Predicate<Thing> validator)
        {
            if (validator == null || validator.Target == null) return null;
            object target = validator.Target;
            Type type = target.GetType();
            FieldInfo field;
            lock (ScannerFieldLock)
            {
                if (!ScannerFields.TryGetValue(type, out field))
                {
                    field = ResolveScannerField(type);
                    ScannerFields[type] = field;
                }
            }
            try { return field == null ? null : field.GetValue(target) as WorkGiver_Scanner; }
            catch { return null; }
        }

        private static FieldInfo ResolveScannerField(Type type)
        {
            if (type == null) return null;
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
                if (typeof(WorkGiver_Scanner).IsAssignableFrom(fields[i].FieldType)) return fields[i];
            return null;
        }

        private static MethodBase FindExactReachable13()
        {
            MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "ClosestThingReachable" || method.ReturnType != typeof(Thing)) continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length != 13) continue;
                if (p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                    p[2].ParameterType == typeof(ThingRequest) && p[3].ParameterType == typeof(PathEndMode) &&
                    p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) &&
                    p[6].ParameterType == typeof(Predicate<Thing>) &&
                    typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType)) return method;
            }
            return null;
        }

        private static bool HasForeignPatch(MethodBase method)
        {
            if (method == null) return true;
            try
            {
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches == null) return false;
                return ContainsForeign(patches.Prefixes) || ContainsForeign(patches.Postfixes) ||
                    ContainsForeign(patches.Transpilers) || ContainsForeign(patches.Finalizers);
            }
            catch { return true; }
        }

        private static bool ContainsForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null) continue;
                if (!string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed < 0L) return;
            Interlocked.Add(ref total, elapsed);
            long seen;
            while (elapsed > (seen = Interlocked.Read(ref max)))
                if (Interlocked.CompareExchange(ref max, elapsed, seen) == seen) break;
        }

        internal static string Summary()
        {
            long accepted = Interlocked.Read(ref captureAccepted);
            long scheduled = Interlocked.Read(ref plansScheduled);
            long published = Interlocked.Read(ref plansPublished);
            double avgCaptureUs = accepted == 0 ? 0.0 : Interlocked.Read(ref captureTicks) * 1000000.0 / Stopwatch.Frequency / accepted;
            double avgWorkerUs = published == 0 ? 0.0 : Interlocked.Read(ref workerTicks) * 1000000.0 / Stopwatch.Frequency / published;
            long validated = Interlocked.Read(ref queryAuthoritative) + Interlocked.Read(ref queryWarmupBypass);
            double avgValidateUs = validated == 0 ? 0.0 : Interlocked.Read(ref validateTicks) * 1000000.0 / Stopwatch.Frequency / validated;

            StringBuilder sb = new StringBuilder(1600);
            sb.Append("T26 parallel work search: installed=").Append(Volatile.Read(ref installed) != 0)
              .Append(", compatibilityReady=").Append(compatibilityReady)
              .Append(", authority[query/repair/refuel]=").Append(queryAuthoritySafe).Append("/").Append(repairAuthoritySafe).Append("/").Append(refuelAuthoritySafe)
              .Append(", epochs=").Append(Interlocked.Read(ref epochBegins)).Append("/").Append(Interlocked.Read(ref epochEnds))
              .Append(", packages=").Append(Interlocked.Read(ref packages)).Append(", nested=").Append(Interlocked.Read(ref nestedPackages))
              .Append(", capture[attempt/accepted/small/large/shape]=").Append(Interlocked.Read(ref captureAttempts)).Append("/")
              .Append(accepted).Append("/").Append(Interlocked.Read(ref captureRejectedSmall)).Append("/")
              .Append(Interlocked.Read(ref captureRejectedLarge)).Append("/").Append(Interlocked.Read(ref captureRejectedShape))
              .Append(", candidates=").Append(Interlocked.Read(ref captureCandidates)).Append(", hardNegatives=").Append(Interlocked.Read(ref captureNegatives))
              .Append(", batches=").Append(Interlocked.Read(ref batchesSubmitted)).Append(", batchRejected=").Append(Interlocked.Read(ref batchSubmitRejected))
              .Append(", plans[scheduled/published/late/workerFail]=").Append(scheduled).Append("/").Append(published).Append("/")
              .Append(Interlocked.Read(ref plansLateDropped)).Append("/").Append(Interlocked.Read(ref workerFailures))
              .Append(", query[observed/scope/scanner/authority/noPlan/notReady/epoch/source/position]=").Append(Interlocked.Read(ref queryObserved)).Append("/")
              .Append(Interlocked.Read(ref queryScopeBypass)).Append("/").Append(Interlocked.Read(ref queryScannerBypass)).Append("/")
              .Append(Interlocked.Read(ref queryAuthorityBypass)).Append("/").Append(Interlocked.Read(ref queryNoPlan)).Append("/")
              .Append(Interlocked.Read(ref queryNotReady)).Append("/").Append(Interlocked.Read(ref queryEpochMismatch)).Append("/")
              .Append(Interlocked.Read(ref querySourceMismatch)).Append("/").Append(Interlocked.Read(ref queryPositionMismatch))
              .Append(", parity[samples/matches/mismatch/warmupBypass]=").Append(Interlocked.Read(ref queryParitySamples)).Append("/")
              .Append(Interlocked.Read(ref queryParityMatches)).Append("/").Append(Interlocked.Read(ref queryParityMismatches)).Append("/")
              .Append(Interlocked.Read(ref queryWarmupBypass))
              .Append(", authoritative=").Append(Interlocked.Read(ref queryAuthoritative)).Append(", null=").Append(Interlocked.Read(ref queryAuthoritativeNull))
              .Append(", byKind[repair/refuel]=").Append(Interlocked.Read(ref repairAuthoritative)).Append("/").Append(Interlocked.Read(ref refuelAuthoritative))
              .Append(", hardNegativesSkipped=").Append(Interlocked.Read(ref negativesSkipped))
              .Append(", liveValidator[calls/rejects]=").Append(Interlocked.Read(ref liveValidatorCalls)).Append("/").Append(Interlocked.Read(ref liveValidatorRejects))
              .Append(", liveReach[calls/rejects]=").Append(Interlocked.Read(ref liveReachCalls)).Append("/").Append(Interlocked.Read(ref liveReachRejects))
              .Append(", avgCaptureUs=").Append(avgCaptureUs.ToString("F2")).Append(", maxCaptureUs=").Append((Interlocked.Read(ref captureTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2"))
              .Append(", avgWorkerUs=").Append(avgWorkerUs.ToString("F2")).Append(", maxWorkerUs=").Append((Interlocked.Read(ref workerTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2"))
              .Append(", avgValidateUs=").Append(avgValidateUs.ToString("F2")).Append(", maxValidateUs=").Append((Interlocked.Read(ref validateTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2"))
              .Append(", sourceEnumerated=").Append(Interlocked.Read(ref sourceEnumerated))
              .Append(", trust[repair=").Append(RepairTrust.Matches).Append(RepairTrust.Quarantined ? ":Q" : ":OK")
              .Append(",refuel=").Append(RefuelTrust.Matches).Append(RefuelTrust.Quarantined ? ":Q" : ":OK").Append("]")
              .Append(", installFailures=").Append(Volatile.Read(ref installFailures))
              .Append(". Same-package/same-epoch only; worker reads primitive arrays only; not-ready/stale/mismatch falls through without waiting.");
            return sb.ToString();
        }

        public struct PackageState
        {
            public bool Entered;
            public bool Outermost;
            public PackageContext Context;
        }

        public sealed class PackageContext
        {
            internal static long SequenceSource;
            internal readonly Pawn Pawn;
            internal readonly Map Map;
            internal readonly long Epoch;
            internal readonly int GameTick;
            internal readonly long StartTicks;
            internal readonly long Sequence;
            internal int Active = 1;
            internal PlanSlot Repair;
            internal PlanSlot Refuel;

            internal PackageContext(Pawn pawn, Map map, long epoch, int gameTick, long startTicks, long sequence)
            {
                Pawn = pawn;
                Map = map;
                Epoch = epoch;
                GameTick = gameTick;
                StartTicks = startTicks;
                Sequence = sequence;
            }

            internal PlanSlot GetSlot(PlanKind kind)
            {
                return kind == PlanKind.Repair ? Repair : kind == PlanKind.Refuel ? Refuel : null;
            }

            internal WorkGiver_Scanner GetScanner(PlanKind kind)
            {
                PlanSlot slot = GetSlot(kind);
                return slot == null ? null : slot.Scanner;
            }

            internal void SetSlot(PlanKind kind, PlanSlot slot)
            {
                if (kind == PlanKind.Repair) Repair = slot;
                else if (kind == PlanKind.Refuel) Refuel = slot;
            }
        }

        public sealed class PlanSlot
        {
            internal readonly PlanKind Kind;
            internal readonly WorkGiver_Scanner Scanner;
            internal readonly long Epoch;
            internal readonly long Sequence;
            internal readonly int MapId;
            internal readonly int RootX;
            internal readonly int RootZ;
            internal readonly Thing[] Members; // main-thread tokens; worker never receives/dereferences this array
            internal readonly int[] Xs;
            internal readonly int[] Zs;
            internal readonly byte[] Negatives;
            internal readonly int NegativeCount;
            internal PublishedPlan Published;

            internal PlanSlot(PlanKind kind, WorkGiver_Scanner scanner, long epoch, long sequence, int mapId,
                int rootX, int rootZ, Thing[] members, int[] xs, int[] zs, byte[] negatives, int negativeCount)
            {
                Kind = kind;
                Scanner = scanner;
                Epoch = epoch;
                Sequence = sequence;
                MapId = mapId;
                RootX = rootX;
                RootZ = rootZ;
                Members = members;
                Xs = xs;
                Zs = zs;
                Negatives = negatives;
                NegativeCount = negativeCount;
            }
        }

        public sealed class PublishedPlan
        {
            internal readonly long Epoch;
            internal readonly long Sequence;
            internal readonly int[] OrderedIndices;
            internal PublishedPlan(long epoch, long sequence, int[] orderedIndices)
            {
                Epoch = epoch;
                Sequence = sequence;
                OrderedIndices = orderedIndices;
            }
        }

        private sealed class TrustState
        {
            internal int Matches;
            internal long UseSerial;
            internal bool Quarantined;
        }

        private struct CandidateKey
        {
            internal readonly int Index;
            internal readonly long DistanceSquared;
            internal CandidateKey(int index, long distanceSquared)
            {
                Index = index;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class CandidateKeyComparer : IComparer<CandidateKey>
        {
            internal static readonly CandidateKeyComparer Instance = new CandidateKeyComparer();
            public int Compare(CandidateKey a, CandidateKey b)
            {
                int c = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            }
        }
    }
}
