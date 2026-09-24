using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T34-C.7 adds a reuse-gated second worker stage to T34-B distance plans. The main thread captures a
    /// compact primitive fact row once; workers run pure rule kernels and publish rejection
    /// bitmaps. Only invariant type/component negatives are authoritative. Mutable pawn, stack
    /// and building facts are shadow telemetry until a later version supplies an invalidation
    /// proof. Every authoritative reject is cheaply rechecked live before it can skip Reachability
    /// or the original validator. Missing, stale, quarantined or unfinished plans fail open.
    /// </summary>
    internal static class CandidateClassificationFabric093T34C
    {
        internal const string FeatureId = "parallel.candidateClassification";
        private const string MainHarmonyOwner = "allen.rimmt";
        private const string DiagnosticsHarmonyOwner = "allen.rimmt.diagnostics";
        private const int MaxTrackedUnresolvedTypes = 64;
        private const int MaxReportedUnresolvedTypes = 12;
        private const int MaxAuthorityEvidence = 16;
        private const int MaxScannerMissEvidence = 16;

        private const byte RejectWrongType = 1;
        private const byte RejectMissingFixedComp = 2;
        private const byte RejectRefuelFull = 4;
        private const byte ShadowStackFull = 1;
        private const byte ShadowPawnUnavailable = 2;
        private const byte ShadowBuildingUnpowered = 4;
        private const byte ShadowRefuelable = 8;
        private const byte ShadowRefuelFull = 16;

        private const int FactPawn = 1;
        private const int FactCorpse = 2;
        private const int FactFire = 4;
        private const int FactBuilding = 8;
        private const int FactHoldingTargetComp = 16;
        private const int FactStackFull = 32;
        private const int FactPawnUnavailable = 64;
        private const int FactBuildingUnpowered = 128;
        private const int FactRefuelable = 256;
        private const int FactRefuelFull = 512;

        private static readonly ConditionalWeakTable<PersistentMapSearchFabric.SourceSnapshot, SnapshotPlans>
            Plans = new ConditionalWeakTable<PersistentMapSearchFabric.SourceSnapshot, SnapshotPlans>();
        private static readonly Dictionary<Type, ScannerAccessor> ScannerAccessors =
            new Dictionary<Type, ScannerAccessor>();
        private static readonly Dictionary<Type, bool> AuthorityCache =
            new Dictionary<Type, bool>();
        // Census writes and report reads occur on the RimWorld main thread. The bounded maps keep
        // evidence useful without adding locks or unbounded allocation to scanner hot paths.
        private static readonly Dictionary<Type, long> UnresolvedTypes = new Dictionary<Type, long>();
        private static readonly Dictionary<Type, long> AuthorityBypassTypes = new Dictionary<Type, long>();
        private static readonly List<AuthorityEvidence> AuthorityEvidenceRows = new List<AuthorityEvidence>();
        private static readonly List<ScannerMissEvidence> ScannerMissEvidenceRows = new List<ScannerMissEvidence>();
        private static readonly KernelCounters[] KernelYield = new KernelCounters[] {
            null, new KernelCounters(), new KernelCounters(), new KernelCounters(), new KernelCounters(),
            new KernelCounters()
        };

        private static long observed;
        private static long kernelResolved;
        private static long kernelUnresolved;
        private static long authorityBypass;
        private static long authorityCompatiblePatches;
        private static long authorityForeignPatches;
        private static long planHits;
        private static long planMisses;
        private static long crossRootPlanHits;
        private static long plansScheduled;
        private static long schedulerRejected;
        private static long plansBuilt;
        private static long workerFailures;
        private static long factsCaptured;
        private static long authoritativeClassified;
        private static long shadowStack;
        private static long shadowPawn;
        private static long shadowBuilding;
        private static long consumedQueries;
        private static long candidatesRejected;
        private static long liveParityChecks;
        private static long liveParityFailures;
        private static long liveStateRevocations;
        private static long quarantined;
        private static long sourceIndexInvalid;
        private static long sourceIndexDuplicate;
        private static long scannerResolveMisses;
        private static long unresolvedTypeDropped;
        private static long authorityTypeDropped;
        private static long authorityEvidenceDropped;
        private static long scannerMissEvidenceDropped;
        private static long refuelFacts;
        private static long refuelFullFacts;
        private static long plansFirstConsumed;
        private static long firstObservationDeferred;
        private static long repeatObservationAdmitted;

        internal static bool TryGetOrSchedule(
            PersistentMapSearchFabric.SourceSnapshot snapshot,
            PersistentMapSearchFabric.DistancePlanEntry[] entries,
            Predicate<Thing> validator,
            int rootX,
            int rootZ,
            out ClassificationPlan plan)
        {
            plan = null;
            Interlocked.Increment(ref observed);

            if (!FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing ||
                snapshot == null || entries == null || entries.Length == 0)
                return false;

            KernelKind kernel;
            if (!TryResolveKernel(validator, out kernel))
            {
                Interlocked.Increment(ref kernelUnresolved);
                return false;
            }
            Interlocked.Increment(ref kernelResolved);
            Interlocked.Increment(ref Counters(kernel).Resolved);

            SnapshotPlans table = Plans.GetValue(snapshot, delegate(PersistentMapSearchFabric.SourceSnapshot ignored) { return new SnapshotPlans(); });
            long key = MakeKey(kernel);
            ClassificationSlot slot = table.Slots.GetOrAdd(key, delegate { return new ClassificationSlot(); });

            if (Volatile.Read(ref slot.Quarantined) != 0)
                return false;

            if (Volatile.Read(ref slot.Ready) != 0)
            {
                ClassificationPlan ready = slot.Plan;
                if (ready != null && ready.Count == snapshot.Count && ready.Kernel == kernel)
                {
                    plan = ready;
                    Interlocked.Increment(ref planHits);
                    Interlocked.Increment(ref Counters(kernel).PlanHits);
                    if (slot.FirstRootX != rootX || slot.FirstRootZ != rootZ)
                        Interlocked.Increment(ref crossRootPlanHits);
                    return true;
                }
            }

            Interlocked.Increment(ref planMisses);
            Interlocked.Increment(ref Counters(kernel).PlanMisses);

            // Runtime evidence showed that most source+kernel plans were built after one query and
            // never consumed. Require one repeated observation of the same immutable source snapshot
            // before copying facts or queueing worker work. Reused Refuel sources pay only one extra
            // Vanilla pass; one-shot sources allocate and schedule nothing.
            if (Interlocked.Increment(ref slot.Observations) < 2)
            {
                Interlocked.Increment(ref firstObservationDeferred);
                return false;
            }
            if (Interlocked.CompareExchange(ref slot.Scheduled, 1, 0) != 0)
                return false;
            Interlocked.Increment(ref repeatObservationAdmitted);

            CandidateFact[] facts;
            try
            {
                facts = CaptureFacts(entries, snapshot.Count, kernel);
            }
            catch
            {
                Volatile.Write(ref slot.Scheduled, 0);
                return false;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            bool accepted = scheduler != null && scheduler.TryEnqueue(
                FeatureId,
                JobPriority.High,
                delegate { BuildPlan(slot, kernel, facts); });

            if (accepted)
            {
                slot.FirstRootX = rootX;
                slot.FirstRootZ = rootZ;
                Interlocked.Increment(ref plansScheduled);
            }
            else
            {
                Volatile.Write(ref slot.Scheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
            }
            return false;
        }

        internal static bool ValidateReject(
            ClassificationPlan plan,
            int index,
            Thing thing,
            out bool quarantineRequired)
        {
            quarantineRequired = false;
            if (plan == null || index < 0 || index >= plan.Count || thing == null)
                return false;

            byte reason = plan.RejectReasons[index];
            if (reason == 0)
                return false;

            Interlocked.Increment(ref liveParityChecks);
            Interlocked.Increment(ref Counters(plan.Kernel).ParityChecks);
            bool stillRejected = LiveReject(plan.Kernel, reason, thing);
            if (!stillRejected)
            {
                // Fuel fullness is volatile. A consumer can burn fuel after capture without
                // changing source membership. The live recheck already prevents an unsafe
                // skip, so this candidate resumes the ordinary live path without quarantine.
                if (plan.Kernel == KernelKind.Refuel && (reason & RejectRefuelFull) != 0)
                {
                    Interlocked.Increment(ref liveStateRevocations);
                    Interlocked.Increment(ref Counters(plan.Kernel).LiveStateRevocations);
                    return false;
                }
                Interlocked.Increment(ref liveParityFailures);
                Interlocked.Increment(ref Counters(plan.Kernel).ParityFailures);
                quarantineRequired = true;
                return false;
            }
            return true;
        }

        internal static bool TryGetRejectReason(
            ClassificationPlan plan,
            int sourceIndex,
            out byte reason)
        {
            reason = 0;
            if (plan == null || sourceIndex < 0 || sourceIndex >= plan.Count)
            {
                Interlocked.Increment(ref sourceIndexInvalid);
                return false;
            }
            reason = plan.RejectReasons[sourceIndex];
            return true;
        }

        internal static void Quarantine(
            PersistentMapSearchFabric.SourceSnapshot snapshot,
            ClassificationPlan plan)
        {
            if (snapshot == null || plan == null)
                return;

            SnapshotPlans table;
            if (!Plans.TryGetValue(snapshot, out table) || table == null)
                return;

            ClassificationSlot slot;
            if (table.Slots.TryGetValue(MakeKey(plan.Kernel), out slot) && slot != null)
            {
                Volatile.Write(ref slot.Quarantined, 1);
                Interlocked.Increment(ref quarantined);
                Interlocked.Increment(ref Counters(plan.Kernel).Quarantined);
            }
        }

        internal static void NoteConsumed(ClassificationPlan plan, int rejected, int within)
        {
            if (plan == null)
                return;
            if (Interlocked.CompareExchange(ref plan.ConsumptionNoted, 1, 0) == 0)
                Interlocked.Increment(ref plansFirstConsumed);
            Interlocked.Increment(ref consumedQueries);
            Interlocked.Increment(ref Counters(plan.Kernel).ConsumedQueries);
            if (rejected > 0)
            {
                Interlocked.Add(ref candidatesRejected, rejected);
                Interlocked.Add(ref Counters(plan.Kernel).CandidatesRejected, rejected);
            }
        }

        private static CandidateFact[] CaptureFacts(
            PersistentMapSearchFabric.DistancePlanEntry[] entries,
            int sourceCount,
            KernelKind kernel)
        {
            if (sourceCount <= 0 || entries.Length != sourceCount)
            {
                Interlocked.Increment(ref sourceIndexInvalid);
                throw new InvalidOperationException("T34-C.2 source count mismatch");
            }

            CandidateFact[] facts = new CandidateFact[sourceCount];
            bool[] seen = new bool[sourceCount];
            for (int i = 0; i < entries.Length; i++)
            {
                int sourceIndex = entries[i].SourceIndex;
                if (sourceIndex < 0 || sourceIndex >= sourceCount)
                {
                    Interlocked.Increment(ref sourceIndexInvalid);
                    throw new InvalidOperationException("T34-C.2 source index outside snapshot");
                }
                if (seen[sourceIndex])
                {
                    Interlocked.Increment(ref sourceIndexDuplicate);
                    throw new InvalidOperationException("T34-C.2 duplicate source index");
                }
                seen[sourceIndex] = true;

                Thing thing = entries[i].Thing;
                int flags = 0;
                if (thing is Pawn) flags |= FactPawn;
                if (thing is Corpse) flags |= FactCorpse;
                if (thing is Fire) flags |= FactFire;
                if (thing is Building) flags |= FactBuilding;

                if (thing != null)
                {
                    if (thing.TryGetComp<CompHoldingPlatformTarget>() != null)
                        flags |= FactHoldingTargetComp;
                    if (thing.def != null && thing.def.stackLimit > 1 &&
                        thing.stackCount >= thing.def.stackLimit)
                        flags |= FactStackFull;

                    Pawn pawn = thing as Pawn;
                    if (pawn != null && (pawn.Dead || pawn.Downed || !pawn.Awake()))
                        flags |= FactPawnUnavailable;

                    CompPowerTrader power = thing.TryGetComp<CompPowerTrader>();
                    if (thing is Building && power != null && !power.PowerOn)
                        flags |= FactBuildingUnpowered;

                    if (kernel == KernelKind.Refuel)
                    {
                        CompRefuelable refuelable = thing.TryGetComp<CompRefuelable>();
                        if (refuelable != null)
                        {
                            flags |= FactRefuelable;
                            if (refuelable.IsFull) flags |= FactRefuelFull;
                        }
                    }
                }
                facts[sourceIndex] = new CandidateFact(flags);
            }
            Interlocked.Add(ref factsCaptured, facts.Length);
            return facts;
        }

        private static void BuildPlan(
            ClassificationSlot slot,
            KernelKind kernel,
            CandidateFact[] facts)
        {
            try
            {
                byte[] rejects = new byte[facts.Length];
                byte[] shadows = new byte[facts.Length];
                long localAuthoritative = 0;
                long localStack = 0;
                long localPawn = 0;
                long localBuilding = 0;
                long localRefuelable = 0;
                long localRefuelFull = 0;

                for (int i = 0; i < facts.Length; i++)
                {
                    int flags = facts[i].Flags;
                    byte reject = EvaluateAuthoritative(kernel, flags);
                    rejects[i] = reject;
                    if (reject != 0) localAuthoritative++;

                    byte shadow = 0;
                    if ((flags & FactStackFull) != 0) { shadow |= ShadowStackFull; localStack++; }
                    if ((flags & FactPawnUnavailable) != 0) { shadow |= ShadowPawnUnavailable; localPawn++; }
                    if ((flags & FactBuildingUnpowered) != 0) { shadow |= ShadowBuildingUnpowered; localBuilding++; }
                    if ((flags & FactRefuelable) != 0) { shadow |= ShadowRefuelable; localRefuelable++; }
                    if ((flags & FactRefuelFull) != 0) { shadow |= ShadowRefuelFull; localRefuelFull++; }
                    shadows[i] = shadow;
                }

                slot.Plan = new ClassificationPlan(kernel, rejects, shadows);
                Interlocked.Add(ref authoritativeClassified, localAuthoritative);
                Interlocked.Add(ref shadowStack, localStack);
                Interlocked.Add(ref shadowPawn, localPawn);
                Interlocked.Add(ref shadowBuilding, localBuilding);
                Interlocked.Add(ref refuelFacts, localRefuelable);
                Interlocked.Add(ref refuelFullFacts, localRefuelFull);
                Interlocked.Increment(ref plansBuilt);
                Volatile.Write(ref slot.Ready, 1);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
        }

        private static byte EvaluateAuthoritative(KernelKind kernel, int flags)
        {
            switch (kernel)
            {
                case KernelKind.Corpse:
                    return (flags & FactCorpse) == 0 ? RejectWrongType : (byte)0;
                case KernelKind.Pawn:
                    return (flags & FactPawn) == 0 ? RejectWrongType : (byte)0;
                case KernelKind.Fire:
                    return (flags & FactFire) == 0 ? RejectWrongType : (byte)0;
                case KernelKind.HoldingTarget:
                    return (flags & FactHoldingTargetComp) == 0 ? RejectMissingFixedComp : (byte)0;
                case KernelKind.Refuel:
                    return (flags & FactRefuelFull) != 0 ? RejectRefuelFull : (byte)0;
                default:
                    return 0;
            }
        }

        private static bool LiveReject(KernelKind kernel, byte reason, Thing thing)
        {
            if ((reason & RejectWrongType) != 0)
            {
                if (kernel == KernelKind.Corpse) return !(thing is Corpse);
                if (kernel == KernelKind.Pawn) return !(thing is Pawn);
                if (kernel == KernelKind.Fire) return !(thing is Fire);
                return false;
            }
            if ((reason & RejectMissingFixedComp) != 0 && kernel == KernelKind.HoldingTarget)
                return thing.TryGetComp<CompHoldingPlatformTarget>() == null;
            if ((reason & RejectRefuelFull) != 0 && kernel == KernelKind.Refuel)
            {
                CompRefuelable refuelable = thing.TryGetComp<CompRefuelable>();
                return refuelable != null && refuelable.IsFull;
            }
            return false;
        }

        private static bool TryResolveKernel(Predicate<Thing> validator, out KernelKind kernel)
        {
            kernel = KernelKind.None;
            ScannerResolveFailure resolveFailure;
            WorkGiver_Scanner scanner = ResolveScanner(validator, out resolveFailure);
            if (scanner == null)
            {
                Interlocked.Increment(ref scannerResolveMisses);
                RecordScannerMiss(validator, resolveFailure);
                return false;
            }

            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_HaulCorpses)) kernel = KernelKind.Corpse;
            else if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform)) kernel = KernelKind.HoldingTarget;
            else if (type == typeof(Workgiver_AdministerHemogen) || type == typeof(WorkGiver_VisitSickPawn)) kernel = KernelKind.Pawn;
            else if (type.FullName == "RimWorld.WorkGiver_FightFires") kernel = KernelKind.Fire;
            else if (type == typeof(WorkGiver_Refuel) || type == typeof(WorkGiver_Refuel_Turret)) kernel = KernelKind.Refuel;
            if (kernel == KernelKind.None)
            {
                RecordBoundedType(UnresolvedTypes, type, MaxTrackedUnresolvedTypes, ref unresolvedTypeDropped);
                return false;
            }

            Interlocked.Increment(ref Counters(kernel).Observed);
            bool authoritySafe = AuthoritySafe(type);
            if (!authoritySafe)
            {
                Interlocked.Increment(ref authorityBypass);
                Interlocked.Increment(ref Counters(kernel).AuthorityBypass);
                RecordBoundedType(AuthorityBypassTypes, type, MaxAuthorityEvidence, ref authorityTypeDropped);
                return false;
            }
            return true;
        }

        private static WorkGiver_Scanner ResolveScanner(
            Predicate<Thing> validator,
            out ScannerResolveFailure failure)
        {
            failure = ScannerResolveFailure.None;
            if (validator == null)
            {
                failure = ScannerResolveFailure.NoValidator;
                return null;
            }
            if (validator.Target == null)
            {
                failure = ScannerResolveFailure.NoTarget;
                return null;
            }
            try
            {
                Type type = validator.Target.GetType();
                ScannerAccessor accessor;
                if (!ScannerAccessors.TryGetValue(type, out accessor))
                {
                    accessor = ScannerAccessor.Build(type);
                    ScannerAccessors[type] = accessor;
                }
                if (accessor == null)
                {
                    failure = ScannerResolveFailure.NoAccessorPath;
                    return null;
                }
                WorkGiver_Scanner scanner = accessor.Read(validator.Target);
                if (scanner == null) failure = ScannerResolveFailure.NullPathValue;
                return scanner;
            }
            catch
            {
                failure = ScannerResolveFailure.Exception;
                return null;
            }
        }

        private static void RecordScannerMiss(
            Predicate<Thing> validator,
            ScannerResolveFailure failure)
        {
            Type targetType = validator == null || validator.Target == null ? null : validator.Target.GetType();
            MethodInfo method = validator == null ? null : validator.Method;
            for (int i = 0; i < ScannerMissEvidenceRows.Count; i++)
            {
                ScannerMissEvidence row = ScannerMissEvidenceRows[i];
                if (row.TargetType == targetType && row.Method == method && row.Failure == failure)
                {
                    row.Count++;
                    return;
                }
            }
            if (ScannerMissEvidenceRows.Count < MaxScannerMissEvidence)
                ScannerMissEvidenceRows.Add(new ScannerMissEvidence(targetType, method, failure));
            else
                Interlocked.Increment(ref scannerMissEvidenceDropped);
        }

        private static bool AuthoritySafe(Type scannerType)
        {
            bool safe;
            if (AuthorityCache.TryGetValue(scannerType, out safe))
                return safe;

            safe = true;
            try
            {
                Type[] args = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
                string[] names = new string[] { "HasJobOnThing", "JobOnThing" };
                for (int n = 0; n < names.Length; n++)
                {
                    Type current = scannerType;
                    while (current != null && typeof(WorkGiver).IsAssignableFrom(current))
                    {
                        MethodInfo method = current.GetMethod(names[n],
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, args, null);
                        if (method != null)
                        {
                            Patches patches = Harmony.GetPatchInfo(method);
                            if (patches != null)
                            {
                                bool incompatible = false;
                                incompatible |= HasIncompatiblePatch(scannerType, method, patches.Prefixes, true, "prefix");
                                incompatible |= HasIncompatiblePatch(scannerType, method, patches.Postfixes, true, "postfix");
                                incompatible |= HasIncompatiblePatch(scannerType, method, patches.Transpilers, false, "transpiler");
                                incompatible |= HasIncompatiblePatch(scannerType, method, patches.Finalizers, false, "finalizer");
                                if (incompatible) safe = false;
                            }
                        }
                        current = current.BaseType;
                    }
                }
            }
            catch { safe = false; }
            AuthorityCache[scannerType] = safe;
            return safe;
        }

        private static bool HasIncompatiblePatch(
            Type scannerType,
            MethodInfo targetMethod,
            IEnumerable<Patch> patches,
            bool allowDiagnosticsMeasurement,
            string category)
        {
            if (patches == null)
                return false;

            bool incompatible = false;
            foreach (Patch patch in patches)
            {
                if (patch == null)
                    continue;

                if (string.Equals(patch.owner, MainHarmonyOwner, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref authorityCompatiblePatches);
                    continue;
                }

                if (allowDiagnosticsMeasurement &&
                    string.Equals(patch.owner, DiagnosticsHarmonyOwner, StringComparison.Ordinal) &&
                    IsDiagnosticsMeasurementPatch(patch.PatchMethod))
                {
                    Interlocked.Increment(ref authorityCompatiblePatches);
                    continue;
                }

                Interlocked.Increment(ref authorityForeignPatches);
                incompatible = true;
                if (AuthorityEvidenceRows.Count < MaxAuthorityEvidence)
                    AuthorityEvidenceRows.Add(new AuthorityEvidence(scannerType, targetMethod, patch.owner,
                        patch.PatchMethod, category, patch.priority));
                else
                    Interlocked.Increment(ref authorityEvidenceDropped);
            }
            return incompatible;
        }

        private static bool IsDiagnosticsMeasurementPatch(MethodInfo method)
        {
            Type owner = method == null ? null : method.DeclaringType;
            string ns = owner == null ? null : owner.Namespace;
            return !string.IsNullOrEmpty(ns) &&
                (string.Equals(ns, "RimMT.Diagnostics", StringComparison.Ordinal) ||
                 ns.StartsWith("RimMT.Diagnostics.", StringComparison.Ordinal));
        }

        private static long MakeKey(KernelKind kernel) { return (long)(int)kernel; }

        private static KernelCounters Counters(KernelKind kernel)
        {
            int index = (int)kernel;
            return index > 0 && index < KernelYield.Length ? KernelYield[index] : KernelYield[1];
        }

        private static void RecordBoundedType(
            Dictionary<Type, long> rows,
            Type type,
            int capacity,
            ref long dropped)
        {
            if (type == null) return;
            long count;
            if (rows.TryGetValue(type, out count))
            {
                rows[type] = count + 1;
                return;
            }
            if (rows.Count < capacity)
                rows.Add(type, 1);
            else
                Interlocked.Increment(ref dropped);
        }

        private static string KernelYieldSummary()
        {
            StringBuilder sb = new StringBuilder();
            KernelKind[] kinds = new KernelKind[] {
                KernelKind.Corpse, KernelKind.Pawn, KernelKind.Fire, KernelKind.HoldingTarget,
                KernelKind.Refuel
            };
            for (int i = 0; i < kinds.Length; i++)
            {
                if (i != 0) sb.Append(';');
                KernelCounters row = Counters(kinds[i]);
                sb.Append(kinds[i]).Append('=')
                    .Append(Interlocked.Read(ref row.Observed)).Append('/')
                    .Append(Interlocked.Read(ref row.Resolved)).Append('/')
                    .Append(Interlocked.Read(ref row.AuthorityBypass)).Append('/')
                    .Append(Interlocked.Read(ref row.PlanHits)).Append('/')
                    .Append(Interlocked.Read(ref row.PlanMisses)).Append('/')
                    .Append(Interlocked.Read(ref row.ConsumedQueries)).Append('/')
                    .Append(Interlocked.Read(ref row.CandidatesRejected)).Append('/')
                    .Append(Interlocked.Read(ref row.ParityChecks)).Append('/')
                    .Append(Interlocked.Read(ref row.ParityFailures)).Append('/')
                    .Append(Interlocked.Read(ref row.Quarantined)).Append('/')
                    .Append(Interlocked.Read(ref row.LiveStateRevocations));
            }
            return sb.ToString();
        }

        private static string TopTypeSummary(Dictionary<Type, long> rows, int limit)
        {
            List<KeyValuePair<Type, long>> copy = new List<KeyValuePair<Type, long>>(rows);
            copy.Sort(delegate(KeyValuePair<Type, long> left, KeyValuePair<Type, long> right) {
                int byCount = right.Value.CompareTo(left.Value);
                if (byCount != 0) return byCount;
                return string.Compare(left.Key.FullName, right.Key.FullName, StringComparison.Ordinal);
            });
            StringBuilder sb = new StringBuilder();
            int count = Math.Min(limit, copy.Count);
            for (int i = 0; i < count; i++)
            {
                if (i != 0) sb.Append(';');
                sb.Append(copy[i].Key.FullName).Append('=').Append(copy[i].Value);
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static string AuthorityEvidenceSummary()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < AuthorityEvidenceRows.Count; i++)
            {
                AuthorityEvidence row = AuthorityEvidenceRows[i];
                if (i != 0) sb.Append(';');
                sb.Append(row.ScannerType == null ? "?" : row.ScannerType.FullName).Append('.')
                    .Append(row.TargetMethod == null ? "?" : row.TargetMethod.Name).Append(':')
                    .Append(row.Category).Append("[owner=").Append(row.Owner ?? "?")
                    .Append(",patch=")
                    .Append(row.PatchMethod == null || row.PatchMethod.DeclaringType == null ? "?" : row.PatchMethod.DeclaringType.FullName)
                    .Append('.').Append(row.PatchMethod == null ? "?" : row.PatchMethod.Name)
                    .Append(",priority=").Append(row.Priority).Append(']');
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static string ScannerMissEvidenceSummary()
        {
            List<ScannerMissEvidence> copy = new List<ScannerMissEvidence>(ScannerMissEvidenceRows);
            copy.Sort(delegate(ScannerMissEvidence left, ScannerMissEvidence right) {
                return right.Count.CompareTo(left.Count);
            });
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < copy.Count; i++)
            {
                ScannerMissEvidence row = copy[i];
                if (i != 0) sb.Append(';');
                sb.Append(row.TargetType == null ? "static-or-null" : row.TargetType.FullName)
                    .Append('.').Append(row.Method == null ? "?" : row.Method.Name)
                    .Append('[').Append(row.Failure).Append("]=").Append(row.Count);
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        internal static string Summary()
        {
            long built = Interlocked.Read(ref plansBuilt);
            long firstConsumed = Interlocked.Read(ref plansFirstConsumed);
            return "T34-C.7 Reuse-Gated Classification Kernel: feature=" + FeatureGate.IsEnabled(FeatureId) +
                ", observed=" + Interlocked.Read(ref observed) +
                ", kernels[resolved/unresolved/authorityBypass]=" + Interlocked.Read(ref kernelResolved) + "/" +
                Interlocked.Read(ref kernelUnresolved) + "/" + Interlocked.Read(ref authorityBypass) +
                ", authorityPatches[compatible/foreign]=" +
                Interlocked.Read(ref authorityCompatiblePatches) + "/" +
                Interlocked.Read(ref authorityForeignPatches) +
                ", sourcePlans[hit/miss/crossRoot/scheduled/rejected/built/fail]=" + Interlocked.Read(ref planHits) + "/" +
                Interlocked.Read(ref planMisses) + "/" + Interlocked.Read(ref crossRootPlanHits) + "/" +
                Interlocked.Read(ref plansScheduled) + "/" +
                Interlocked.Read(ref schedulerRejected) + "/" + Interlocked.Read(ref plansBuilt) + "/" +
                Interlocked.Read(ref workerFailures) +
                ", entryIndex[invalid/duplicate]=" + Interlocked.Read(ref sourceIndexInvalid) + "/" +
                Interlocked.Read(ref sourceIndexDuplicate) +
                ", facts=" + Interlocked.Read(ref factsCaptured) +
                ", classified[authoritative/shadowStack/shadowPawn/shadowBuilding]=" +
                Interlocked.Read(ref authoritativeClassified) + "/" + Interlocked.Read(ref shadowStack) + "/" +
                Interlocked.Read(ref shadowPawn) + "/" + Interlocked.Read(ref shadowBuilding) +
                ", consumed[queries/rejected]=" + Interlocked.Read(ref consumedQueries) + "/" +
                Interlocked.Read(ref candidatesRejected) +
                ", parity[checks/fail/quarantine/liveStateRevoked]=" + Interlocked.Read(ref liveParityChecks) + "/" +
                Interlocked.Read(ref liveParityFailures) + "/" + Interlocked.Read(ref quarantined) + "/" +
                Interlocked.Read(ref liveStateRevocations) +
                ", planUse[firstConsumed/built/unconsumedUpper]=" + firstConsumed + "/" + built + "/" +
                Math.Max(0L, built - firstConsumed) +
                ", reuseGate[firstDeferred/repeatAdmitted]=" + Interlocked.Read(ref firstObservationDeferred) + "/" +
                Interlocked.Read(ref repeatObservationAdmitted) +
                ", kernelYield[observed/resolved/authorityBypass/hit/miss/consumed/rejected/parityChecks/parityFail/quarantine/liveStateRevoked]=" +
                KernelYieldSummary() +
                ", unresolved[scannerMiss/dropped/top]=" + Interlocked.Read(ref scannerResolveMisses) + "/" +
                Interlocked.Read(ref unresolvedTypeDropped) + "/" + TopTypeSummary(UnresolvedTypes, MaxReportedUnresolvedTypes) +
                ", authorityBypassTypes[dropped/top]=" + Interlocked.Read(ref authorityTypeDropped) + "/" +
                TopTypeSummary(AuthorityBypassTypes, MaxAuthorityEvidence) +
                ", authorityEvidence[dropped/rows]=" + Interlocked.Read(ref authorityEvidenceDropped) + "/" +
                AuthorityEvidenceSummary() +
                ", scannerMissEvidence[dropped/rows]=" + Interlocked.Read(ref scannerMissEvidenceDropped) + "/" +
                ScannerMissEvidenceSummary() +
                ", refuelFullKernel[facts/fullCaptured]=" + Interlocked.Read(ref refuelFacts) + "/" +
                Interlocked.Read(ref refuelFullFacts) +
                ". Refuel/Refuel_Turret full negatives require a clean Harmony authority audit and are rechecked live before Reachability or the original validator can be skipped; Root-independent source-index plans; no-wait; worker input is primitive-only; mutable stack/pawn/building facts are shadow-only; live validator and Reachability remain authoritative for survivors.";
        }

        internal sealed class ClassificationPlan
        {
            internal readonly KernelKind Kernel;
            internal readonly byte[] RejectReasons;
            internal readonly byte[] ShadowReasons;
            internal int ConsumptionNoted;
            internal int Count { get { return RejectReasons.Length; } }
            internal ClassificationPlan(KernelKind kernel, byte[] rejects, byte[] shadows)
            {
                Kernel = kernel;
                RejectReasons = rejects;
                ShadowReasons = shadows;
            }
        }

        internal enum KernelKind : byte { None, Corpse, Pawn, Fire, HoldingTarget, Refuel }
        private sealed class KernelCounters
        {
            internal long Observed;
            internal long Resolved;
            internal long AuthorityBypass;
            internal long PlanHits;
            internal long PlanMisses;
            internal long ConsumedQueries;
            internal long CandidatesRejected;
            internal long ParityChecks;
            internal long ParityFailures;
            internal long Quarantined;
            internal long LiveStateRevocations;
        }
        private enum ScannerResolveFailure : byte { None, NoValidator, NoTarget, NoAccessorPath, NullPathValue, Exception }
        private sealed class ScannerMissEvidence
        {
            internal readonly Type TargetType;
            internal readonly MethodInfo Method;
            internal readonly ScannerResolveFailure Failure;
            internal long Count;
            internal ScannerMissEvidence(Type targetType, MethodInfo method, ScannerResolveFailure failure)
            {
                TargetType = targetType;
                Method = method;
                Failure = failure;
                Count = 1;
            }
        }
        private sealed class AuthorityEvidence
        {
            internal readonly Type ScannerType;
            internal readonly MethodInfo TargetMethod;
            internal readonly string Owner;
            internal readonly MethodInfo PatchMethod;
            internal readonly string Category;
            internal readonly int Priority;
            internal AuthorityEvidence(Type scannerType, MethodInfo targetMethod, string owner,
                MethodInfo patchMethod, string category, int priority)
            {
                ScannerType = scannerType;
                TargetMethod = targetMethod;
                Owner = owner;
                PatchMethod = patchMethod;
                Category = category;
                Priority = priority;
            }
        }
        private struct CandidateFact { internal readonly int Flags; internal CandidateFact(int flags) { Flags = flags; } }
        private sealed class ClassificationSlot { internal ClassificationPlan Plan; internal int Scheduled; internal int Ready; internal int Quarantined; internal int Observations; internal int FirstRootX; internal int FirstRootZ; }
        private sealed class SnapshotPlans { internal readonly ConcurrentDictionary<long, ClassificationSlot> Slots = new ConcurrentDictionary<long, ClassificationSlot>(); }

        private sealed class ScannerAccessor
        {
            private readonly FieldInfo[] path;
            private ScannerAccessor(FieldInfo[] path) { this.path = path; }
            internal WorkGiver_Scanner Read(object root)
            {
                object value = root;
                for (int i = 0; i < path.Length; i++)
                {
                    if (value == null) return null;
                    value = path[i].GetValue(value);
                }
                return value as WorkGiver_Scanner;
            }
            internal static ScannerAccessor Build(Type root)
            {
                List<FieldInfo> path = new List<FieldInfo>();
                HashSet<Type> visited = new HashSet<Type>();
                return Find(root, 0, path, visited) ? new ScannerAccessor(path.ToArray()) : null;
            }
            private static bool Find(Type type, int depth, List<FieldInfo> path, HashSet<Type> visited)
            {
                if (type == null || depth > 3 || visited.Contains(type)) return false;
                visited.Add(type);
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (typeof(WorkGiver_Scanner).IsAssignableFrom(fields[i].FieldType))
                    { path.Add(fields[i]); return true; }
                }
                for (int i = 0; i < fields.Length; i++)
                {
                    Type fieldType = fields[i].FieldType;
                    if (fieldType.IsPrimitive || fieldType.IsEnum || fieldType == typeof(string) || fieldType.IsPointer) continue;
                    path.Add(fields[i]);
                    if (Find(fieldType, depth + 1, path, visited)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;
            }
        }
    }
}
