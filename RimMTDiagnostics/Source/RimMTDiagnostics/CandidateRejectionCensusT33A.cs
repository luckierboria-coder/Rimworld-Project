using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// T33-A Candidate Rejection Signature Census.
    ///
    /// Measurement-only, diagnostics companion only.
    /// Samples 1/8 live JobGiver_Work Validator(Thing) calls and asks:
    /// - was the candidate already forbidden to the package pawn?
    /// - did the live validator invoke CanReserve on this candidate, and did it return false?
    /// - did the live validator invoke CanReach on this candidate, and did it return false?
    /// - did the validator ultimately accept or reject?
    ///
    /// It never alters any result. The purpose is to identify generic shared negative primitives
    /// that could later become safe candidate-admission proofs before expensive validators run.
    /// </summary>
    internal static class CandidateRejectionCensusT33A
    {
        private const int SampleMask = 7; // 1/8.
        private const int MaxScannerKeys = 512;

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static Pawn packagePawn;
        [ThreadStatic] private static long sampleSerial;
        [ThreadStatic] private static Probe activeProbe;

        private static bool installed;
        private static int validatorPatched;
        private static int patchFailures;

        private static long packages;
        private static long validatorsObserved;
        private static long validatorsRunOriginalBypass;
        private static long sampledValidators;
        private static long sampledRejects;
        private static long sampledAccepts;

        private static long forbiddenReads;
        private static long forbiddenReadFailures;
        private static long rejectForbiddenTrue;
        private static long acceptForbiddenTrue;

        private static long rejectReserveCalled;
        private static long rejectReserveFalse;
        private static long acceptReserveCalled;
        private static long acceptReserveFalse;

        private static long rejectReachCalled;
        private static long rejectReachFalse;
        private static long acceptReachCalled;
        private static long acceptReachFalse;

        private static long rejectAnySharedNegative;
        private static long acceptAnySharedNegative;
        private static long rejectNoSharedNegative;

        private static readonly long[] RejectMasks = new long[8];
        private static readonly long[] AcceptMasks = new long[8];

        private static readonly Dictionary<string, ScannerStats> ScannerTable =
            new Dictionary<string, ScannerStats>();
        private static readonly Dictionary<Type, ScannerAccessor> ScannerAccessors =
            new Dictionary<Type, ScannerAccessor>();

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase package = AccessTools.Method(
                    typeof(JobGiver_Work),
                    "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });

                if (package != null)
                {
                    harmony.Patch(
                        package,
                        prefix: new HarmonyMethod(typeof(CandidateRejectionCensusT33A), nameof(PackagePrefix))
                        { priority = Priority.First + 120 },
                        finalizer: new HarmonyMethod(typeof(CandidateRejectionCensusT33A), nameof(PackageFinalizer))
                        { priority = Priority.Last - 120 });
                }

                PatchValidators(harmony);

                MethodBase reserve = AccessTools.Method(
                    typeof(ReservationManager),
                    nameof(ReservationManager.CanReserve),
                    new Type[]
                    {
                        typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(int),
                        typeof(ReservationLayerDef), typeof(bool)
                    });
                if (reserve != null)
                {
                    harmony.Patch(
                        reserve,
                        postfix: new HarmonyMethod(typeof(CandidateRejectionCensusT33A), nameof(CanReservePostfix))
                        { priority = Priority.Last - 150 });
                }
                else patchFailures++;

                MethodBase reach = AccessTools.Method(
                    typeof(Reachability),
                    nameof(Reachability.CanReach),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms)
                    });
                if (reach != null)
                {
                    harmony.Patch(
                        reach,
                        postfix: new HarmonyMethod(typeof(CandidateRejectionCensusT33A), nameof(CanReachPostfix))
                        { priority = Priority.Last - 150 });
                }
                else patchFailures++;

                installed = package != null && validatorPatched > 0 && reserve != null && reach != null;
                Log.Message("[RimMT Diagnostics] T33-A Candidate Rejection Signature Census installed=" +
                    installed + ", validators=" + validatorPatched + ", sample=1/8, failures=" + patchFailures +
                    ". Measurement-only; results are never altered.");
            }
            catch (Exception ex)
            {
                installed = false;
                patchFailures++;
                Log.Warning("[RimMT Diagnostics] T33-A install failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchValidators(Harmony harmony)
        {
            List<Type> nested = new List<Type>();
            CollectNestedTypes(typeof(JobGiver_Work), nested);
            HashSet<MethodBase> unique = new HashSet<MethodBase>();

            for (int i = 0; i < nested.Count; i++)
            {
                MethodInfo[] methods;
                try
                {
                    methods = nested[i].GetMethods(
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                for (int j = 0; j < methods.Length; j++)
                {
                    MethodInfo method = methods[j];
                    if (method == null ||
                        method.ReturnType != typeof(bool) ||
                        method.Name.IndexOf("Validator", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    ParameterInfo[] p = method.GetParameters();
                    if (p.Length != 1 || !typeof(Thing).IsAssignableFrom(p[0].ParameterType))
                        continue;
                    if (!unique.Add(method)) continue;

                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(
                                typeof(CandidateRejectionCensusT33A),
                                nameof(ValidatorPrefix))
                            { priority = Priority.Last - 100 },
                            postfix: new HarmonyMethod(
                                typeof(CandidateRejectionCensusT33A),
                                nameof(ValidatorPostfix))
                            { priority = Priority.First + 100 });
                        validatorPatched++;
                    }
                    catch { patchFailures++; }
                }
            }
        }

        private static void CollectNestedTypes(Type parent, List<Type> output)
        {
            Type[] nested;
            try { nested = parent.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return; }

            for (int i = 0; i < nested.Length; i++)
            {
                Type t = nested[i];
                if (t == null) continue;
                output.Add(t);
                CollectNestedTypes(t, output);
            }
        }

        public static void PackagePrefix(Pawn __0)
        {
            if (packageDepth == 0)
            {
                packagePawn = __0;
                sampleSerial = 0;
                activeProbe = null;
                packages++;
            }
            packageDepth++;
        }

        public static Exception PackageFinalizer(Exception __exception)
        {
            if (packageDepth > 0) packageDepth--;
            if (packageDepth == 0)
            {
                activeProbe = null;
                packagePawn = null;
            }
            return __exception;
        }

        public static void ValidatorPrefix(
            object __instance,
            object[] __args,
            bool __runOriginal,
            ref Probe __state)
        {
            __state = null;
            validatorsObserved++;

            if (!__runOriginal)
            {
                validatorsRunOriginalBypass++;
                return;
            }

            if (packageDepth <= 0 || packagePawn == null ||
                __args == null || __args.Length != 1)
                return;

            Thing thing = __args[0] as Thing;
            if (thing == null) return;

            long serial = ++sampleSerial;
            if ((serial & SampleMask) != 0)
                return;

            bool forbidden = false;
            try
            {
                forbiddenReads++;
                forbidden = thing.IsForbidden(packagePawn);
            }
            catch
            {
                forbiddenReadFailures++;
            }

            string scanner = ResolveScannerName(__instance);
            Probe probe = new Probe(packagePawn, thing, scanner, forbidden);
            activeProbe = probe;
            __state = probe;
            sampledValidators++;
        }

        public static void ValidatorPostfix(bool __result, Probe __state)
        {
            Probe probe = __state;
            if (probe == null) return;

            if (ReferenceEquals(activeProbe, probe))
                activeProbe = null;

            int mask = 0;
            if (probe.Forbidden) mask |= 1;
            if (probe.ReserveFalse) mask |= 2;
            if (probe.ReachFalse) mask |= 4;

            if (__result)
            {
                sampledAccepts++;
                AcceptMasks[mask]++;

                if (probe.Forbidden) acceptForbiddenTrue++;
                if (probe.ReserveCalled) acceptReserveCalled++;
                if (probe.ReserveFalse) acceptReserveFalse++;
                if (probe.ReachCalled) acceptReachCalled++;
                if (probe.ReachFalse) acceptReachFalse++;
                if (mask != 0) acceptAnySharedNegative++;
            }
            else
            {
                sampledRejects++;
                RejectMasks[mask]++;

                if (probe.Forbidden) rejectForbiddenTrue++;
                if (probe.ReserveCalled) rejectReserveCalled++;
                if (probe.ReserveFalse) rejectReserveFalse++;
                if (probe.ReachCalled) rejectReachCalled++;
                if (probe.ReachFalse) rejectReachFalse++;
                if (mask != 0) rejectAnySharedNegative++;
                else rejectNoSharedNegative++;
            }

            RecordScanner(probe.Scanner, __result, mask);
        }

        public static void CanReservePostfix(
            Pawn __0,
            LocalTargetInfo __1,
            bool __result)
        {
            Probe probe = activeProbe;
            if (probe == null) return;
            if (!ReferenceEquals(__0, probe.Pawn)) return;
            if (!TargetsCandidate(__1, probe.Thing)) return;

            probe.ReserveCalled = true;
            if (!__result) probe.ReserveFalse = true;
        }

        public static void CanReachPostfix(
            LocalTargetInfo dest,
            TraverseParms traverseParams,
            bool __result)
        {
            Probe probe = activeProbe;
            if (probe == null) return;
            if (traverseParams.pawn != null &&
                !ReferenceEquals(traverseParams.pawn, probe.Pawn))
                return;
            if (!TargetsCandidate(dest, probe.Thing)) return;

            probe.ReachCalled = true;
            if (!__result) probe.ReachFalse = true;
        }

        private static bool TargetsCandidate(LocalTargetInfo target, Thing candidate)
        {
            return target.HasThing && ReferenceEquals(target.Thing, candidate);
        }

        private static string ResolveScannerName(object closure)
        {
            if (closure == null) return "<static>";
            try
            {
                Type type = closure.GetType();
                ScannerAccessor accessor;
                if (!ScannerAccessors.TryGetValue(type, out accessor))
                {
                    accessor = ScannerAccessor.Build(type);
                    ScannerAccessors[type] = accessor;
                }

                WorkGiver_Scanner scanner = accessor == null ? null : accessor.Read(closure);
                if (scanner == null) return type.FullName ?? "<unknown>";

                if (scanner.def != null && !string.IsNullOrEmpty(scanner.def.defName))
                    return scanner.def.defName;
                return scanner.GetType().FullName ?? "<unknown>";
            }
            catch
            {
                return "<resolve-failed>";
            }
        }

        private static void RecordScanner(string key, bool accepted, int mask)
        {
            if (string.IsNullOrEmpty(key)) key = "<unknown>";
            ScannerStats stats;
            if (!ScannerTable.TryGetValue(key, out stats))
            {
                if (ScannerTable.Count >= MaxScannerKeys) return;
                stats = new ScannerStats();
                ScannerTable[key] = stats;
            }

            stats.Samples++;
            if (accepted)
            {
                stats.Accepts++;
                if (mask != 0) stats.AcceptsWithSharedNegative++;
            }
            else
            {
                stats.Rejects++;
                if (mask != 0) stats.RejectsWithSharedNegative++;
                else stats.RejectsWithoutSharedNegative++;
            }

            if ((mask & 1) != 0) stats.ForbiddenTrue++;
            if ((mask & 2) != 0) stats.ReserveFalse++;
            if ((mask & 4) != 0) stats.ReachFalse++;
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.AppendLine("[T33-A Candidate Rejection Signature Census]");
            sb.AppendLine(
                "installed=" + installed +
                ", sample=1/8" +
                ", packages=" + packages +
                ", validatorsObserved=" + validatorsObserved +
                ", runOriginalBypass=" + validatorsRunOriginalBypass +
                ", sampled=" + sampledValidators +
                ", rejects/accepts=" + sampledRejects + "/" + sampledAccepts +
                ", patchFailures=" + patchFailures);

            double rejectCoverage = sampledRejects == 0
                ? 0.0
                : 100.0 * rejectAnySharedNegative / sampledRejects;

            sb.AppendLine(
                "sharedNegativeOnReject[any/none/coverage%]=" +
                rejectAnySharedNegative + "/" +
                rejectNoSharedNegative + "/" +
                rejectCoverage.ToString("F2") +
                ", acceptWithSharedNegative=" + acceptAnySharedNegative);

            sb.AppendLine(
                "Forbidden[reads/failures/rejectTrue/acceptTrue]=" +
                forbiddenReads + "/" +
                forbiddenReadFailures + "/" +
                rejectForbiddenTrue + "/" +
                acceptForbiddenTrue);

            sb.AppendLine(
                "CanReserve[rejectCalled/rejectFalse/acceptCalled/acceptFalse]=" +
                rejectReserveCalled + "/" +
                rejectReserveFalse + "/" +
                acceptReserveCalled + "/" +
                acceptReserveFalse);

            sb.AppendLine(
                "CanReach[rejectCalled/rejectFalse/acceptCalled/acceptFalse]=" +
                rejectReachCalled + "/" +
                rejectReachFalse + "/" +
                acceptReachCalled + "/" +
                acceptReachFalse);

            sb.AppendLine("rejectMasks[none/F/R/FR/C/FC/RC/FRC]=" +
                JoinMasks(RejectMasks));
            sb.AppendLine("acceptMasks[none/F/R/FR/C/FC/RC/FRC]=" +
                JoinMasks(AcceptMasks));

            sb.AppendLine(
                "scannerBreadth=" + ScannerTable.Count +
                ", topRejectCoverage=" + BuildTopScanners());

            sb.AppendLine(
                "Interpretation rule: a primitive is NOT eligible for generic early rejection if " +
                "its corresponding accept-side negative count is non-zero. This census is correlation/proof " +
                "discovery only; it never changes validator results.");
            return sb.ToString();
        }

        private static string JoinMasks(long[] values)
        {
            return values[0] + "/" + values[1] + "/" + values[2] + "/" + values[3] + "/" +
                   values[4] + "/" + values[5] + "/" + values[6] + "/" + values[7];
        }

        private static string BuildTopScanners()
        {
            string k1 = null, k2 = null, k3 = null, k4 = null, k5 = null;
            ScannerStats s1 = null, s2 = null, s3 = null, s4 = null, s5 = null;

            foreach (KeyValuePair<string, ScannerStats> pair in ScannerTable)
            {
                ScannerStats s = pair.Value;
                if (s == null) continue;
                if (s1 == null || s.RejectsWithSharedNegative > s1.RejectsWithSharedNegative)
                {
                    k5=k4; s5=s4; k4=k3; s4=s3; k3=k2; s3=s2; k2=k1; s2=s1; k1=pair.Key; s1=s;
                }
                else if (s2 == null || s.RejectsWithSharedNegative > s2.RejectsWithSharedNegative)
                {
                    k5=k4; s5=s4; k4=k3; s4=s3; k3=k2; s3=s2; k2=pair.Key; s2=s;
                }
                else if (s3 == null || s.RejectsWithSharedNegative > s3.RejectsWithSharedNegative)
                {
                    k5=k4; s5=s4; k4=k3; s4=s3; k3=pair.Key; s3=s;
                }
                else if (s4 == null || s.RejectsWithSharedNegative > s4.RejectsWithSharedNegative)
                {
                    k5=k4; s5=s4; k4=pair.Key; s4=s;
                }
                else if (s5 == null || s.RejectsWithSharedNegative > s5.RejectsWithSharedNegative)
                {
                    k5=pair.Key; s5=s;
                }
            }

            return FormatScanner(k1,s1) + "; " + FormatScanner(k2,s2) + "; " +
                   FormatScanner(k3,s3) + "; " + FormatScanner(k4,s4) + "; " +
                   FormatScanner(k5,s5);
        }

        private static string FormatScanner(string key, ScannerStats stats)
        {
            if (stats == null) return "none";
            return key + "(samples=" + stats.Samples +
                   ", rejectShared=" + stats.RejectsWithSharedNegative +
                   ", rejectNone=" + stats.RejectsWithoutSharedNegative +
                   ", acceptShared=" + stats.AcceptsWithSharedNegative +
                   ", F/R/C=" + stats.ForbiddenTrue + "/" +
                   stats.ReserveFalse + "/" + stats.ReachFalse + ")";
        }

        internal sealed class Probe
        {
            internal readonly Pawn Pawn;
            internal readonly Thing Thing;
            internal readonly string Scanner;
            internal readonly bool Forbidden;
            internal bool ReserveCalled;
            internal bool ReserveFalse;
            internal bool ReachCalled;
            internal bool ReachFalse;

            internal Probe(Pawn pawn, Thing thing, string scanner, bool forbidden)
            {
                Pawn = pawn;
                Thing = thing;
                Scanner = scanner;
                Forbidden = forbidden;
            }
        }

        private sealed class ScannerStats
        {
            internal long Samples;
            internal long Rejects;
            internal long Accepts;
            internal long RejectsWithSharedNegative;
            internal long RejectsWithoutSharedNegative;
            internal long AcceptsWithSharedNegative;
            internal long ForbiddenTrue;
            internal long ReserveFalse;
            internal long ReachFalse;
        }

        private sealed class ScannerAccessor
        {
            private readonly FieldInfo[] path;
            private ScannerAccessor(FieldInfo[] path) { this.path = path; }

            internal WorkGiver_Scanner Read(object root)
            {
                object value = root;
                try
                {
                    for (int i = 0; i < path.Length; i++)
                    {
                        if (value == null) return null;
                        value = path[i].GetValue(value);
                    }
                    return value as WorkGiver_Scanner;
                }
                catch { return null; }
            }

            internal static ScannerAccessor Build(Type root)
            {
                List<FieldInfo> path = new List<FieldInfo>();
                HashSet<Type> visited = new HashSet<Type>();
                return Find(root, 0, path, visited)
                    ? new ScannerAccessor(path.ToArray())
                    : null;
            }

            private static bool Find(
                Type type,
                int depth,
                List<FieldInfo> path,
                HashSet<Type> visited)
            {
                if (type == null || depth > 3 || visited.Contains(type))
                    return false;
                visited.Add(type);

                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch { return false; }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field != null &&
                        typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType))
                    {
                        path.Add(field);
                        return true;
                    }
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field == null ||
                        field.FieldType.IsPrimitive ||
                        field.FieldType == typeof(string) ||
                        field.FieldType.IsEnum ||
                        field.FieldType.IsPointer)
                        continue;

                    path.Add(field);
                    if (Find(field.FieldType, depth + 1, path, visited))
                        return true;
                    path.RemoveAt(path.Count - 1);
                }

                return false;
            }
        }
    }
}
