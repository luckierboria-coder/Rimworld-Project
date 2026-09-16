using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T27.2 — conservative foundation for future coarse WorkGiver parallelism.
    ///
    /// This class is audit/registry only. It never executes a WorkGiver on a worker and never changes
    /// JobGiver results. At startup it performs one bounded pass over WorkGiverDef.giverClass values,
    /// inventories relevant virtual entry points, foreign Harmony patches and direct IL dependencies,
    /// then assigns preliminary safety tiers. FullParallel admission is hard-disabled until the missing
    /// thread-safety foundations (Reservation promise/commit, live Reachability isolation, Job/Toil pools,
    /// Rand, JobFailReason and mod callback containment) are implemented and independently validated.
    /// </summary>
    internal static class WorkGiverParallelSafety093T27_2
    {
        internal const string FeatureId = "parallel.workSafety";

        private static readonly object Sync = new object();
        private static readonly Dictionary<Type, AuditRecord> Records = new Dictionary<Type, AuditRecord>();
        private static readonly List<string> CleanExamples = new List<string>();
        private static readonly List<string> SnapshotExamples = new List<string>();

        private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
        private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

        private static volatile bool initialized;
        private static long defsSeen;
        private static long uniqueTypes;
        private static long scannerTypes;
        private static long modTypes;
        private static long foreignPatchedTypes;
        private static long snapshotCandidates;
        private static long directIlCleanCandidates;
        private static long directIlScanFailures;
        private static long fullParallelAdmitted;
        private static long fullParallelDenied;

        private static long riskUnity;
        private static long riskReservation;
        private static long riskReachability;
        private static long riskRand;
        private static long riskJobFailReason;
        private static long riskJobCreation;
        private static long riskMutableWorld;
        private static long riskGlobalSideEffect;

        // Foundation gates intentionally start OFF. They are declarations, not settings.
        private const bool FoundationReservationPromise = false;
        private const bool FoundationReachabilityIsolation = false;
        private const bool FoundationJobToilPools = false;
        private const bool FoundationThreadLocalRand = false;
        private const bool FoundationThreadLocalJobFailReason = false;
        private const bool FoundationModCallbackContainment = false;

        static WorkGiverParallelSafety093T27_2()
        {
            BuildOpcodeTables();
        }

        internal static void Initialize()
        {
            if (initialized) return;
            lock (Sync)
            {
                if (initialized) return;
                try
                {
                    List<WorkGiverDef> defs = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                    if (defs != null)
                    {
                        for (int i = 0; i < defs.Count; i++)
                        {
                            WorkGiverDef def = defs[i];
                            if (def == null || def.giverClass == null) continue;
                            Interlocked.Increment(ref defsSeen);
                            if (!Records.ContainsKey(def.giverClass))
                            {
                                AuditRecord record = AuditType(def.giverClass);
                                Records.Add(def.giverClass, record);
                                Interlocked.Increment(ref uniqueTypes);
                            }
                        }
                    }
                    initialized = true;
                    Log.Message("[RimMT] T27.2 WorkGiver parallel safety registry initialized. Audit only; FullParallel admission hard-OFF.");
                }
                catch (Exception ex)
                {
                    initialized = false;
                    FeatureGate.Suppress(FeatureId, "T27.2 safety registry init failure: " + ex.GetType().Name);
                    Log.Warning("[RimMT] T27.2 WorkGiver safety registry failed closed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        internal static bool CanAdmitFullParallel(Type giverType, out string reason)
        {
            Interlocked.Increment(ref fullParallelDenied);
            reason = "FullParallel hard-OFF: reservation/reachability/job-pool/Rand/JobFailReason/mod-callback foundations are incomplete.";
            return false;
        }

        internal static SafetyTier GetTier(Type giverType)
        {
            if (giverType == null) return SafetyTier.MainThreadOnly;
            AuditRecord record;
            lock (Sync)
            {
                if (Records.TryGetValue(giverType, out record) && record != null)
                    return record.Tier;
            }
            return SafetyTier.MainThreadOnly;
        }

        private static AuditRecord AuditType(Type type)
        {
            bool isScanner = typeof(WorkGiver_Scanner).IsAssignableFrom(type);
            bool isModType = type.Assembly != typeof(WorkGiver).Assembly;
            if (isScanner) Interlocked.Increment(ref scannerTypes);
            if (isModType) Interlocked.Increment(ref modTypes);

            RiskFlags risks = RiskFlags.None;
            int relevantMethods = 0;
            int foreignPatches = 0;
            HashSet<MethodBase> seen = new HashSet<MethodBase>();

            string[] names = new string[]
            {
                "NonScanJob", "ShouldSkip", "MissingRequiredCapacity",
                "PotentialWorkThingsGlobal", "PotentialWorkCellsGlobal",
                "HasJobOnThing", "HasJobOnCell", "JobOnThing", "JobOnCell",
                "GetPriority", "MaxPathDanger",
                "get_Prioritized", "get_AllowUnreachable", "get_PotentialWorkThingRequest",
                "get_PathEndMode", "get_MaxRegionsToScanBeforeGlobalSearch"
            };

            MethodInfo[] methods;
            try { methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { methods = new MethodInfo[0]; }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || !NameIn(method.Name, names) || !seen.Add(method)) continue;
                relevantMethods++;
                if (HasForeignHarmonyPatch(method)) foreignPatches++;
                try
                {
                    ScanDirectDependencies(method, ref risks);
                }
                catch
                {
                    risks |= RiskFlags.IlScanIncomplete;
                    Interlocked.Increment(ref directIlScanFailures);
                }
            }

            if (foreignPatches > 0) Interlocked.Increment(ref foreignPatchedTypes);
            AccumulateRiskCounters(risks);

            SafetyTier tier;
            if (!isScanner || isModType || foreignPatches > 0)
            {
                tier = SafetyTier.MainThreadOnly;
            }
            else
            {
                tier = SafetyTier.SnapshotComputeCandidate;
                Interlocked.Increment(ref snapshotCandidates);
                AddExample(SnapshotExamples, type.FullName);

                RiskFlags hardDirectRisks = RiskFlags.UnityApi | RiskFlags.ReservationApi |
                    RiskFlags.ReachabilityApi | RiskFlags.RandApi | RiskFlags.JobFailReasonApi |
                    RiskFlags.JobCreationApi | RiskFlags.MutableWorldApi | RiskFlags.GlobalSideEffect |
                    RiskFlags.IlScanIncomplete;
                if ((risks & hardDirectRisks) == 0)
                {
                    tier = SafetyTier.DirectIlCleanCandidate;
                    Interlocked.Increment(ref directIlCleanCandidates);
                    AddExample(CleanExamples, type.FullName);
                }
            }

            return new AuditRecord(type, tier, risks, relevantMethods, foreignPatches, isModType, isScanner);
        }

        private static void ScanDirectDependencies(MethodInfo method, ref RiskFlags risks)
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) return;
            byte[] il = body.GetILAsByteArray();
            if (il == null || il.Length == 0) return;

            int p = 0;
            while (p < il.Length)
            {
                OpCode op;
                byte first = il[p++];
                if (first == 0xFE)
                {
                    if (p >= il.Length) break;
                    op = TwoByteOpCodes[il[p++]];
                }
                else
                {
                    op = OneByteOpCodes[first];
                }

                int operandStart = p;
                int operandSize = OperandSize(op.OperandType, il, p);
                if (operandSize < 0 || p + operandSize > il.Length)
                {
                    risks |= RiskFlags.IlScanIncomplete;
                    return;
                }

                if ((op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField ||
                     op.OperandType == OperandType.InlineTok || op.OperandType == OperandType.InlineType) && operandSize >= 4)
                {
                    int token = BitConverter.ToInt32(il, operandStart);
                    try
                    {
                        Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
                            ? method.DeclaringType.GetGenericArguments() : Type.EmptyTypes;
                        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
                        MemberInfo member = method.Module.ResolveMember(token, typeArgs, methodArgs);
                        if (member != null) ClassifyMember(member, ref risks);
                    }
                    catch
                    {
                        risks |= RiskFlags.IlScanIncomplete;
                    }
                }
                p += operandSize;
            }
        }

        private static void ClassifyMember(MemberInfo member, ref RiskFlags risks)
        {
            Type type = member as Type;
            if (type == null) type = member.DeclaringType;
            if (type == null) return;

            string ns = type.Namespace ?? string.Empty;
            string name = type.Name ?? string.Empty;
            string full = type.FullName ?? name;

            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal))
                risks |= RiskFlags.UnityApi;

            if (name == "ReservationManager" || full.IndexOf("ReservationManager", StringComparison.Ordinal) >= 0)
                risks |= RiskFlags.ReservationApi;

            if (name == "Reachability" || name == "RegionTraverser" || name == "Region" ||
                full.IndexOf("Reachability", StringComparison.Ordinal) >= 0)
                risks |= RiskFlags.ReachabilityApi;

            if (name == "Rand" || full == "Verse.Rand")
                risks |= RiskFlags.RandApi;

            if (name == "JobFailReason" || full.IndexOf("JobFailReason", StringComparison.Ordinal) >= 0)
                risks |= RiskFlags.JobFailReasonApi;

            if (name == "JobMaker" || name == "Job" || name.StartsWith("Toil", StringComparison.Ordinal) ||
                full.IndexOf("SimplePool", StringComparison.Ordinal) >= 0)
                risks |= RiskFlags.JobCreationApi;

            if (name == "Pawn" || name == "Thing" || name == "Map" || name == "ListerThings" ||
                name == "ThingGrid" || name == "Pawn_JobTracker" || name == "Pawn_PathFollower" ||
                name == "DesignationManager" || name == "ZoneManager" || name == "ThingOwner")
                risks |= RiskFlags.MutableWorldApi;

            if (name == "Messages" || name == "Log" || name == "SoundStarter" || name == "MoteMaker")
                risks |= RiskFlags.GlobalSideEffect;
        }

        private static bool HasForeignHarmonyPatch(MethodBase method)
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

        private static bool NameIn(string name, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(name, names[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private static void AccumulateRiskCounters(RiskFlags risks)
        {
            if ((risks & RiskFlags.UnityApi) != 0) Interlocked.Increment(ref riskUnity);
            if ((risks & RiskFlags.ReservationApi) != 0) Interlocked.Increment(ref riskReservation);
            if ((risks & RiskFlags.ReachabilityApi) != 0) Interlocked.Increment(ref riskReachability);
            if ((risks & RiskFlags.RandApi) != 0) Interlocked.Increment(ref riskRand);
            if ((risks & RiskFlags.JobFailReasonApi) != 0) Interlocked.Increment(ref riskJobFailReason);
            if ((risks & RiskFlags.JobCreationApi) != 0) Interlocked.Increment(ref riskJobCreation);
            if ((risks & RiskFlags.MutableWorldApi) != 0) Interlocked.Increment(ref riskMutableWorld);
            if ((risks & RiskFlags.GlobalSideEffect) != 0) Interlocked.Increment(ref riskGlobalSideEffect);
        }

        private static void AddExample(List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value) || list.Count >= 8) return;
            list.Add(value);
        }

        private static void BuildOpcodeTables()
        {
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                object value = fields[i].GetValue(null);
                if (!(value is OpCode)) continue;
                OpCode op = (OpCode)value;
                ushort code = unchecked((ushort)op.Value);
                if (code < 0x100) OneByteOpCodes[code] = op;
                else if ((code & 0xFF00) == 0xFE00) TwoByteOpCodes[code & 0xFF] = op;
            }
        }

        private static int OperandSize(OperandType type, byte[] il, int p)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (p + 4 > il.Length) return -1;
                    int count = BitConverter.ToInt32(il, p);
                    if (count < 0 || count > 100000) return -1;
                    return 4 + count * 4;
                default: return -1;
            }
        }

        internal static string Summary()
        {
            string snapshotExamples;
            string cleanExamples;
            lock (Sync)
            {
                snapshotExamples = SnapshotExamples.Count == 0 ? "<none>" : string.Join(";", SnapshotExamples.ToArray());
                cleanExamples = CleanExamples.Count == 0 ? "<none>" : string.Join(";", CleanExamples.ToArray());
            }

            return "T27.2 WorkGiver parallel safety registry: initialized=" + initialized +
                ", defs/types/scanners=" + Interlocked.Read(ref defsSeen) + "/" + Interlocked.Read(ref uniqueTypes) + "/" + Interlocked.Read(ref scannerTypes) +
                ", modTypes=" + Interlocked.Read(ref modTypes) +
                ", foreignPatchedTypes=" + Interlocked.Read(ref foreignPatchedTypes) +
                ", preliminary[snapshot/directILClean/fullAdmitted]=" + Interlocked.Read(ref snapshotCandidates) + "/" + Interlocked.Read(ref directIlCleanCandidates) + "/" + Interlocked.Read(ref fullParallelAdmitted) +
                ", fullDenied=" + Interlocked.Read(ref fullParallelDenied) +
                ", risks[unity/reservation/reach/rand/jobFail/jobCreate/mutableWorld/sideEffect]=" +
                Interlocked.Read(ref riskUnity) + "/" + Interlocked.Read(ref riskReservation) + "/" + Interlocked.Read(ref riskReachability) + "/" +
                Interlocked.Read(ref riskRand) + "/" + Interlocked.Read(ref riskJobFailReason) + "/" + Interlocked.Read(ref riskJobCreation) + "/" +
                Interlocked.Read(ref riskMutableWorld) + "/" + Interlocked.Read(ref riskGlobalSideEffect) +
                ", ilScanFailures=" + Interlocked.Read(ref directIlScanFailures) +
                ". FullParallel=HARD_OFF; direct IL clean is candidate evidence only, never an admission decision." +
                " SnapshotExamples=" + snapshotExamples + "; DirectILCleanExamples=" + cleanExamples;
        }

        internal static string ApiMatrixSummary()
        {
            return "T27.2 thread-safety API matrix: PrimitiveSnapshot=WORKER_SAFE; PersistentFabricSnapshot=WORKER_SAFE; DefRead=READ_ONLY_CANDIDATE; " +
                "Pawn/Thing/MapMutable=MAIN_THREAD; ReservationManager=MAIN_THREAD; LiveReachability=MAIN_THREAD; UnityEngine=MAIN_THREAD; " +
                "Job/ToilPool=THREAD_LOCAL_REQUIRED(" + OnOff(FoundationJobToilPools) + "); Rand=THREAD_LOCAL_REQUIRED(" + OnOff(FoundationThreadLocalRand) + "); " +
                "JobFailReason=THREAD_LOCAL_REQUIRED(" + OnOff(FoundationThreadLocalJobFailReason) + "); ReservationPromise=DEFERRED_COMMIT_REQUIRED(" + OnOff(FoundationReservationPromise) + "); " +
                "ReachabilityIsolation=SNAPSHOT_REQUIRED(" + OnOff(FoundationReachabilityIsolation) + "); ModCallbackContainment=REQUIRED(" + OnOff(FoundationModCallbackContainment) + ").";
        }

        private static string OnOff(bool value) { return value ? "READY" : "OFF"; }

        internal enum SafetyTier
        {
            MainThreadOnly,
            SnapshotComputeCandidate,
            DirectIlCleanCandidate
        }

        [Flags]
        private enum RiskFlags
        {
            None = 0,
            UnityApi = 1 << 0,
            ReservationApi = 1 << 1,
            ReachabilityApi = 1 << 2,
            RandApi = 1 << 3,
            JobFailReasonApi = 1 << 4,
            JobCreationApi = 1 << 5,
            MutableWorldApi = 1 << 6,
            GlobalSideEffect = 1 << 7,
            IlScanIncomplete = 1 << 8
        }

        private sealed class AuditRecord
        {
            internal readonly Type Type;
            internal readonly SafetyTier Tier;
            internal readonly RiskFlags Risks;
            internal readonly int RelevantMethods;
            internal readonly int ForeignPatches;
            internal readonly bool ModType;
            internal readonly bool Scanner;

            internal AuditRecord(Type type, SafetyTier tier, RiskFlags risks, int relevantMethods, int foreignPatches, bool modType, bool scanner)
            {
                Type = type;
                Tier = tier;
                Risks = risks;
                RelevantMethods = relevantMethods;
                ForeignPatches = foreignPatches;
                ModType = modType;
                Scanner = scanner;
            }
        }
    }
}
