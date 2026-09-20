using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// T33-B Rejection Family Census.
    ///
    /// Diagnostics-only discovery stage. It patches concrete WorkGiver_Scanner.HasJobOnThing
    /// methods and samples 1/64 live calls. For each scanner method it performs one bounded
    /// direct-IL scan, extracts Verse/RimWorld/mod method calls and field reads, then weights
    /// those member families by actual sampled HasJobOnThing reject/accept outcomes.
    ///
    /// This is structural correlation, not proof that a member executed on a particular call.
    /// No member is invoked by the census and no gameplay result is changed.
    /// </summary>
    internal static class CandidateRejectionFamilyCensusT33B
    {
        private const int SampleMask = 63; // 1/64 live HasJobOnThing calls.
        private const int MaxFamilies = 4096;
        private const int MaxMembersPerMethod = 96;
        private const int MaxScannerProfiles = 768;
        private const int TopCount = 16;

        [ThreadStatic] private static long sampleSerial;

        private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
        private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

        private static readonly Dictionary<string, MemberFamily> Families =
            new Dictionary<string, MemberFamily>();
        private static readonly Dictionary<MethodBase, MemberFamily[]> MethodProfiles =
            new Dictionary<MethodBase, MemberFamily[]>();
        private static readonly Dictionary<string, ScannerProfile> ScannerProfiles =
            new Dictionary<string, ScannerProfile>();

        private static bool installed;
        private static int methodsPatched;
        private static int patchFailures;
        private static int ilScanFailures;
        private static int familyCapacityBypass;
        private static int memberCapBypass;
        private static int scannerProfileCapBypass;

        private static long observed;
        private static long runOriginalBypass;
        private static long sampled;
        private static long sampledRejects;
        private static long sampledAccepts;
        private static long samplesWithoutProfile;
        private static long weightedMemberVisits;

        static CandidateRejectionFamilyCensusT33B()
        {
            BuildOpcodeTables();
        }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            try
            {
                List<WorkGiverDef> defs = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                HashSet<MethodBase> unique = new HashSet<MethodBase>();

                if (defs != null)
                {
                    for (int i = 0; i < defs.Count; i++)
                    {
                        WorkGiverDef def = defs[i];
                        Type giverType = def == null ? null : def.giverClass;
                        if (giverType == null || !typeof(WorkGiver_Scanner).IsAssignableFrom(giverType))
                            continue;

                        MethodInfo method = AccessTools.Method(
                            giverType,
                            "HasJobOnThing",
                            new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                        if (method == null || !unique.Add(method))
                            continue;

                        try
                        {
                            harmony.Patch(
                                method,
                                prefix: new HarmonyMethod(
                                    typeof(CandidateRejectionFamilyCensusT33B),
                                    nameof(HasJobPrefix))
                                { priority = Priority.Last - 80 },
                                postfix: new HarmonyMethod(
                                    typeof(CandidateRejectionFamilyCensusT33B),
                                    nameof(HasJobPostfix))
                                { priority = Priority.Last - 80 });
                            methodsPatched++;
                        }
                        catch
                        {
                            patchFailures++;
                        }
                    }
                }

                installed = methodsPatched > 0;
                Log.Message(
                    "[RimMT Diagnostics] T33-B Rejection Family Census installed=" + installed +
                    ", HasJobOnThingMethods=" + methodsPatched +
                    ", sample=1/64, failures=" + patchFailures +
                    ". Structural IL correlation only; no result is altered.");
            }
            catch (Exception ex)
            {
                installed = false;
                patchFailures++;
                Log.Warning(
                    "[RimMT Diagnostics] T33-B install failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void HasJobPrefix(
            WorkGiver_Scanner __instance,
            MethodBase __originalMethod,
            bool __runOriginal,
            ref SampleState __state)
        {
            __state = default(SampleState);
            observed++;

            if (!__runOriginal)
            {
                runOriginalBypass++;
                return;
            }

            long serial = ++sampleSerial;
            if ((serial & SampleMask) != 0)
                return;

            ScannerProfile profile = GetScannerProfile(__instance, __originalMethod);
            if (profile == null)
            {
                samplesWithoutProfile++;
                return;
            }

            sampled++;
            __state = new SampleState(profile);
        }

        public static void HasJobPostfix(bool __result, SampleState __state)
        {
            ScannerProfile profile = __state.Profile;
            if (profile == null) return;

            MemberFamily[] members = profile.Members;
            if (__result)
            {
                sampledAccepts++;
                if (!profile.AcceptSeen)
                {
                    profile.AcceptSeen = true;
                    for (int i = 0; i < members.Length; i++)
                        members[i].AcceptScanners++;
                }

                for (int i = 0; i < members.Length; i++)
                {
                    members[i].AcceptWeight++;
                    weightedMemberVisits++;
                }
            }
            else
            {
                sampledRejects++;
                if (!profile.RejectSeen)
                {
                    profile.RejectSeen = true;
                    for (int i = 0; i < members.Length; i++)
                        members[i].RejectScanners++;
                }

                for (int i = 0; i < members.Length; i++)
                {
                    members[i].RejectWeight++;
                    weightedMemberVisits++;
                }
            }
        }

        private static ScannerProfile GetScannerProfile(
            WorkGiver_Scanner scanner,
            MethodBase method)
        {
            if (scanner == null || method == null)
                return null;

            string key = ScannerKey(scanner, method);
            ScannerProfile profile;
            if (ScannerProfiles.TryGetValue(key, out profile))
                return profile;

            if (ScannerProfiles.Count >= MaxScannerProfiles)
            {
                scannerProfileCapBypass++;
                return null;
            }

            MemberFamily[] members;
            if (!MethodProfiles.TryGetValue(method, out members))
            {
                members = BuildMethodProfile(method);
                MethodProfiles[method] = members;
            }

            profile = new ScannerProfile(key, members);
            ScannerProfiles.Add(key, profile);

            for (int i = 0; i < members.Length; i++)
                members[i].StructuralScanners++;

            return profile;
        }

        private static string ScannerKey(WorkGiver_Scanner scanner, MethodBase method)
        {
            string defName = null;
            try
            {
                if (scanner.def != null)
                    defName = scanner.def.defName;
            }
            catch { }

            string typeName = scanner.GetType().FullName ?? scanner.GetType().Name;
            string methodOwner = method.DeclaringType == null
                ? "<unknown>"
                : (method.DeclaringType.FullName ?? method.DeclaringType.Name);

            if (string.IsNullOrEmpty(defName))
                return typeName + "|" + methodOwner;
            return defName + "|" + typeName + "|" + methodOwner;
        }

        private static MemberFamily[] BuildMethodProfile(MethodBase method)
        {
            List<MemberFamily> result = new List<MemberFamily>(32);
            HashSet<string> seen = new HashSet<string>();

            try
            {
                MethodBody body = method.GetMethodBody();
                if (body == null) return result.ToArray();

                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0)
                    return result.ToArray();

                int p = 0;
                while (p < il.Length)
                {
                    OpCode op;
                    byte first = il[p++];
                    if (first == 0xFE)
                    {
                        if (p >= il.Length)
                        {
                            ilScanFailures++;
                            break;
                        }
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
                        ilScanFailures++;
                        break;
                    }

                    bool methodRead =
                        op.Value == OpCodes.Call.Value ||
                        op.Value == OpCodes.Callvirt.Value;
                    bool fieldRead =
                        op.Value == OpCodes.Ldfld.Value ||
                        op.Value == OpCodes.Ldsfld.Value ||
                        op.Value == OpCodes.Ldflda.Value ||
                        op.Value == OpCodes.Ldsflda.Value;

                    if ((methodRead && op.OperandType == OperandType.InlineMethod) ||
                        (fieldRead && op.OperandType == OperandType.InlineField))
                    {
                        int token = BitConverter.ToInt32(il, operandStart);
                        MemberInfo member = ResolveMember(method, token);
                        string familyKey;
                        MemberKind kind;

                        if (TryNormalizeMember(member, methodRead, fieldRead, out familyKey, out kind) &&
                            seen.Add(familyKey))
                        {
                            MemberFamily family = GetFamily(familyKey, kind);
                            if (family != null)
                            {
                                result.Add(family);
                                if (result.Count >= MaxMembersPerMethod)
                                {
                                    memberCapBypass++;
                                    break;
                                }
                            }
                        }
                    }

                    p += operandSize;
                }
            }
            catch
            {
                ilScanFailures++;
            }

            return result.ToArray();
        }

        private static MemberInfo ResolveMember(MethodBase method, int token)
        {
            try
            {
                Type[] typeArgs =
                    method.DeclaringType != null && method.DeclaringType.IsGenericType
                    ? method.DeclaringType.GetGenericArguments()
                    : Type.EmptyTypes;

                Type[] methodArgs =
                    method.IsGenericMethod
                    ? method.GetGenericArguments()
                    : Type.EmptyTypes;

                return method.Module.ResolveMember(token, typeArgs, methodArgs);
            }
            catch
            {
                ilScanFailures++;
                return null;
            }
        }

        private static bool TryNormalizeMember(
            MemberInfo member,
            bool methodRead,
            bool fieldRead,
            out string key,
            out MemberKind kind)
        {
            key = null;
            kind = MemberKind.Method;
            if (member == null) return false;

            Type owner = member.DeclaringType;
            if (owner == null) return false;

            string ns = owner.Namespace ?? string.Empty;
            if (ns.StartsWith("System", StringComparison.Ordinal) ||
                ns.StartsWith("Microsoft", StringComparison.Ordinal) ||
                ns.StartsWith("HarmonyLib", StringComparison.Ordinal) ||
                ns.StartsWith("MonoMod", StringComparison.Ordinal) ||
                ns.StartsWith("UnityEngine", StringComparison.Ordinal))
                return false;

            string ownerName = owner.FullName ?? owner.Name;
            if (string.IsNullOrEmpty(ownerName))
                return false;

            if (methodRead)
            {
                MethodBase m = member as MethodBase;
                if (m == null || m.IsConstructor) return false;
                string name = m.Name ?? string.Empty;
                if (string.IsNullOrEmpty(name)) return false;
                if (name == "ToString" || name == "GetHashCode" || name == "Equals")
                    return false;

                kind = MemberKind.Method;
                key = "M:" + ownerName + "." + name;
                return true;
            }

            if (fieldRead)
            {
                FieldInfo f = member as FieldInfo;
                if (f == null) return false;
                string name = f.Name ?? string.Empty;
                if (string.IsNullOrEmpty(name)) return false;

                kind = MemberKind.Field;
                key = "F:" + ownerName + "." + name;
                return true;
            }

            return false;
        }

        private static MemberFamily GetFamily(string key, MemberKind kind)
        {
            MemberFamily family;
            if (Families.TryGetValue(key, out family))
                return family;

            if (Families.Count >= MaxFamilies)
            {
                familyCapacityBypass++;
                return null;
            }

            family = new MemberFamily(key, kind);
            Families.Add(key, family);
            return family;
        }

        private static void BuildOpcodeTables()
        {
            FieldInfo[] fields = typeof(OpCodes).GetFields(
                BindingFlags.Public | BindingFlags.Static);

            for (int i = 0; i < fields.Length; i++)
            {
                object value = fields[i].GetValue(null);
                if (!(value is OpCode)) continue;

                OpCode op = (OpCode)value;
                ushort code = unchecked((ushort)op.Value);
                if (code < 0x100)
                    OneByteOpCodes[code] = op;
                else if ((code & 0xFF00) == 0xFE00)
                    TwoByteOpCodes[code & 0xFF] = op;
            }
        }

        private static int OperandSize(OperandType type, byte[] il, int p)
        {
            switch (type)
            {
                case OperandType.InlineNone:
                    return 0;

                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;

                case OperandType.InlineVar:
                    return 2;

                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return 4;

                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;

                case OperandType.InlineSwitch:
                    if (p + 4 > il.Length) return -1;
                    int count = BitConverter.ToInt32(il, p);
                    if (count < 0 || count > 100000) return -1;
                    return 4 + count * 4;

                default:
                    return -1;
            }
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("[T33-B Rejection Family Census]");
            sb.AppendLine(
                "installed=" + installed +
                ", sample=1/64 live HasJobOnThing" +
                ", methodsPatched=" + methodsPatched +
                ", scannerProfiles=" + ScannerProfiles.Count +
                ", methodProfiles=" + MethodProfiles.Count +
                ", families=" + Families.Count +
                ", observed=" + observed +
                ", runOriginalBypass=" + runOriginalBypass +
                ", sampled=" + sampled +
                ", rejects/accepts=" + sampledRejects + "/" + sampledAccepts +
                ", samplesWithoutProfile=" + samplesWithoutProfile +
                ", weightedMemberVisits=" + weightedMemberVisits +
                ", failures[patch/il/familyCap/memberCap/scannerCap]=" +
                patchFailures + "/" + ilScanFailures + "/" +
                familyCapacityBypass + "/" + memberCapBypass + "/" +
                scannerProfileCapBypass);

            sb.AppendLine(
                "Top shared METHOD families by reject-weight (structural IL presence, not executed-call proof):");
            sb.Append(BuildTop(MemberKind.Method, false));

            sb.AppendLine(
                "Top shared FIELD-read families by reject-weight (structural IL presence, not executed-read proof):");
            sb.Append(BuildTop(MemberKind.Field, false));

            sb.AppendLine(
                "Reject-only structural candidates (acceptWeight=0, rejectScanners>=3; discovery only):");
            sb.Append(BuildTop(MemberKind.Method, true));
            sb.Append(BuildTop(MemberKind.Field, true));

            sb.AppendLine(
                "Interpretation: T33-B ranks shared members for the NEXT live-proof stage. " +
                "A member listed here MUST NOT become production admission solely because acceptWeight=0; " +
                "direct IL presence does not prove that the member executed on each rejected candidate. " +
                "No member is called by T33-B and no HasJobOnThing result is changed.");
            return sb.ToString();
        }

        private static string BuildTop(MemberKind kind, bool rejectOnly)
        {
            List<MemberFamily> list = new List<MemberFamily>();

            foreach (KeyValuePair<string, MemberFamily> pair in Families)
            {
                MemberFamily f = pair.Value;
                if (f == null || f.Kind != kind) continue;
                if (f.StructuralScanners < 2) continue;
                if (rejectOnly &&
                    (f.AcceptWeight != 0 || f.RejectScanners < 3))
                    continue;
                list.Add(f);
            }

            list.Sort(MemberFamilyComparer.Instance);

            if (list.Count == 0)
                return "  none\n";

            StringBuilder sb = new StringBuilder(4096);
            int count = Math.Min(TopCount, list.Count);
            for (int i = 0; i < count; i++)
            {
                MemberFamily f = list[i];
                sb.Append("  ");
                sb.Append(i + 1);
                sb.Append(". ");
                sb.Append(f.Key);
                sb.Append(" [struct/rejectScanners/acceptScanners=");
                sb.Append(f.StructuralScanners);
                sb.Append("/");
                sb.Append(f.RejectScanners);
                sb.Append("/");
                sb.Append(f.AcceptScanners);
                sb.Append(", rejectWeight/acceptWeight=");
                sb.Append(f.RejectWeight);
                sb.Append("/");
                sb.Append(f.AcceptWeight);
                sb.AppendLine("]");
            }
            return sb.ToString();
        }

        internal struct SampleState
        {
            internal readonly ScannerProfile Profile;

            internal SampleState(ScannerProfile profile)
            {
                Profile = profile;
            }
        }

        internal sealed class ScannerProfile
        {
            private readonly string Key;
            private readonly MemberFamily[] Members;
            private bool RejectSeen;
            private bool AcceptSeen;

            private ScannerProfile(string key, MemberFamily[] members)
            {
                Key = key;
                Members = members ?? new MemberFamily[0];
            }
        }

        private sealed class MemberFamily
        {
            internal readonly string Key;
            internal readonly MemberKind Kind;
            internal int StructuralScanners;
            internal int RejectScanners;
            internal int AcceptScanners;
            internal long RejectWeight;
            internal long AcceptWeight;

            internal MemberFamily(string key, MemberKind kind)
            {
                Key = key;
                Kind = kind;
            }
        }

        private sealed class MemberFamilyComparer : IComparer<MemberFamily>
        {
            internal static readonly MemberFamilyComparer Instance =
                new MemberFamilyComparer();

            public int Compare(MemberFamily x, MemberFamily y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return 1;
                if (y == null) return -1;

                int c = y.RejectWeight.CompareTo(x.RejectWeight);
                if (c != 0) return c;

                c = y.RejectScanners.CompareTo(x.RejectScanners);
                if (c != 0) return c;

                c = y.StructuralScanners.CompareTo(x.StructuralScanners);
                if (c != 0) return c;

                return string.CompareOrdinal(x.Key, y.Key);
            }
        }

        private enum MemberKind
        {
            Method,
            Field
        }
    }
}
