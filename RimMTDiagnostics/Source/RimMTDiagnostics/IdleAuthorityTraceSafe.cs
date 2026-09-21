using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// Read-only correctness trace for the mass-idle regression.
    /// It observes results that the live WorkGiver methods already produced inside
    /// Pawn_JobTracker.DetermineNextJob. It never invokes a WorkGiver, validator,
    /// CanReach, CanReserve, or candidate enumerable on its own.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class IdleAuthorityTraceSafe
    {
        private const int SampleMask = 3; // 1/4 baseline sample; repeat-idle pawns are always armed.
        private const int RecentCapacity = 24;
        private const int MaxRowsPerTrace = 24;

        private static readonly FieldInfo JobTrackerPawnField =
            AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
        private static readonly Dictionary<MethodBase, MethodDescriptor> MethodMap =
            new Dictionary<MethodBase, MethodDescriptor>();
        private static readonly Dictionary<int, bool> LastIdleByPawn =
            new Dictionary<int, bool>();
        private static readonly TraceRecord[] Recent = new TraceRecord[RecentCapacity];

        [ThreadStatic] private static TraceContext current;

        private static int nextMethodId;
        private static int determineSerial;
        private static int recentPos;
        private static int recentCount;
        private static int methodsPatched;
        private static int patchFailures;
        private static long playerDetermines;
        private static long armedDetermines;
        private static long sampledBypass;
        private static long idleOutcomes;
        private static long idleCaptured;
        private static long armedNonIdleDiscarded;
        private static long contextReentry;
        private static long failures;

        static IdleAuthorityTraceSafe()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Harmony harmony = new Harmony(DiagnosticsBootstrap.HarmonyId + ".idleauthority");

                MethodBase determine = AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob");
                if (determine != null)
                {
                    harmony.Patch(
                        determine,
                        prefix: new HarmonyMethod(typeof(IdleAuthorityTraceSafe), nameof(DeterminePrefix))
                        { priority = Priority.First + 10 },
                        postfix: new HarmonyMethod(typeof(IdleAuthorityTraceSafe), nameof(DeterminePostfix))
                        { priority = Priority.Last - 100 });
                }
                else
                {
                    patchFailures++;
                }

                HashSet<MethodBase> seen = new HashSet<MethodBase>();
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int ai = 0; ai < assemblies.Length; ai++)
                {
                    Type[] types;
                    try { types = assemblies[ai].GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    catch { continue; }
                    if (types == null) continue;

                    for (int ti = 0; ti < types.Length; ti++)
                    {
                        Type type = types[ti];
                        if (type == null || !typeof(WorkGiver_Scanner).IsAssignableFrom(type)) continue;

                        MethodInfo[] methods;
                        try
                        {
                            methods = type.GetMethods(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                        catch { continue; }

                        for (int mi = 0; mi < methods.Length; mi++)
                        {
                            MethodInfo method = methods[mi];
                            if (method == null || !seen.Add(method)) continue;
                            TryPatchObservedMethod(harmony, method);
                        }
                    }
                }

                Log.Message("[RimMT Diagnostics] Idle Authority Trace SAFE installed: methods=" +
                    methodsPatched + ", failures=" + patchFailures +
                    ". Read-only: no extra validators/reachability/reservation/candidate enumeration.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                Log.Warning("[RimMT Diagnostics] Idle Authority Trace SAFE install failed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TryPatchObservedMethod(Harmony harmony, MethodInfo method)
        {
            string name = method.Name;
            HarmonyMethod postfix = null;
            MethodKind kind;

            if ((name == "ShouldSkip" || name == "HasJobOnThing" || name == "HasJobOnCell") &&
                method.ReturnType == typeof(bool))
            {
                postfix = new HarmonyMethod(typeof(IdleAuthorityTraceSafe), nameof(BoolPostfix))
                { priority = Priority.Last - 80 };
                kind = MethodKind.Bool;
            }
            else if ((name == "JobOnThing" || name == "JobOnCell" || name == "NonScanJob") &&
                     typeof(Job).IsAssignableFrom(method.ReturnType))
            {
                postfix = new HarmonyMethod(typeof(IdleAuthorityTraceSafe), nameof(JobPostfix))
                { priority = Priority.Last - 80 };
                kind = MethodKind.Job;
            }
            else if (name == "PotentialWorkThingsGlobal" &&
                     typeof(IEnumerable<Thing>).IsAssignableFrom(method.ReturnType))
            {
                postfix = new HarmonyMethod(typeof(IdleAuthorityTraceSafe), nameof(ThingSourcePostfix))
                { priority = Priority.Last - 80 };
                kind = MethodKind.ThingSource;
            }
            else if (name == "PotentialWorkCellsGlobal" &&
                     typeof(IEnumerable<IntVec3>).IsAssignableFrom(method.ReturnType))
            {
                postfix = new HarmonyMethod(typeof(IdleAuthorityTraceSafe), nameof(CellSourcePostfix))
                { priority = Priority.Last - 80 };
                kind = MethodKind.CellSource;
            }
            else
            {
                return;
            }

            try
            {
                harmony.Patch(method, postfix: postfix);
                int id = nextMethodId++;
                MethodMap[method] = new MethodDescriptor(
                    id,
                    (method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName) +
                    "." + method.Name,
                    kind);
                methodsPatched++;
            }
            catch
            {
                patchFailures++;
            }
        }

        public static void DeterminePrefix(Pawn_JobTracker __instance)
        {
            if (current != null)
            {
                contextReentry++;
                current = null;
            }

            Pawn pawn = GetPawn(__instance);
            if (!IsPlayerHumanlike(pawn)) return;

            playerDetermines++;
            bool priorIdle = false;
            LastIdleByPawn.TryGetValue(pawn.thingIDNumber, out priorIdle);
            int serial = ++determineSerial;
            bool arm = priorIdle || ((serial & SampleMask) == 0);
            if (!arm)
            {
                sampledBypass++;
                return;
            }

            armedDetermines++;
            current = new TraceContext
            {
                Pawn = pawn,
                Tick = CurrentTick(),
                EntryJob = JobName(SafeCurJob(pawn)),
                Rows = new Dictionary<int, MethodCounts>()
            };
        }

        public static void DeterminePostfix(Pawn_JobTracker __instance, ThinkResult __result)
        {
            Pawn pawn = current == null ? GetPawn(__instance) : current.Pawn;
            if (!IsPlayerHumanlike(pawn))
            {
                current = null;
                return;
            }

            bool idle = IsIdleOutcome(__result);
            LastIdleByPawn[pawn.thingIDNumber] = idle;
            if (idle) idleOutcomes++;

            TraceContext ctx = current;
            current = null;
            if (ctx == null) return;

            if (!idle)
            {
                armedNonIdleDiscarded++;
                return;
            }

            idleCaptured++;
            AddRecent(BuildRecord(ctx, __result));
        }

        public static void BoolPostfix(MethodBase __originalMethod, bool __result)
        {
            TraceContext ctx = current;
            if (ctx == null) return;
            MethodDescriptor descriptor;
            if (!MethodMap.TryGetValue(__originalMethod, out descriptor)) return;

            MethodCounts counts = GetCounts(ctx, descriptor.Id);
            counts.Calls++;
            if (__result) counts.TrueOrNonNull++;
            else counts.FalseOrNull++;
            ctx.Rows[descriptor.Id] = counts;
        }

        public static void JobPostfix(MethodBase __originalMethod, Job __result)
        {
            TraceContext ctx = current;
            if (ctx == null) return;
            MethodDescriptor descriptor;
            if (!MethodMap.TryGetValue(__originalMethod, out descriptor)) return;

            MethodCounts counts = GetCounts(ctx, descriptor.Id);
            counts.Calls++;
            if (__result != null) counts.TrueOrNonNull++;
            else counts.FalseOrNull++;
            ctx.Rows[descriptor.Id] = counts;
        }

        public static void ThingSourcePostfix(MethodBase __originalMethod, IEnumerable<Thing> __result)
        {
            TraceContext ctx = current;
            if (ctx == null) return;
            RecordSource(ctx, __originalMethod, __result as ICollection<Thing>);
            if (__result == null) RecordSourceNull(ctx, __originalMethod);
        }

        public static void CellSourcePostfix(MethodBase __originalMethod, IEnumerable<IntVec3> __result)
        {
            TraceContext ctx = current;
            if (ctx == null) return;
            RecordSource(ctx, __originalMethod, __result as ICollection<IntVec3>);
            if (__result == null) RecordSourceNull(ctx, __originalMethod);
        }

        private static void RecordSource<T>(TraceContext ctx, MethodBase method, ICollection<T> collection)
        {
            MethodDescriptor descriptor;
            if (ctx == null || !MethodMap.TryGetValue(method, out descriptor)) return;

            MethodCounts counts = GetCounts(ctx, descriptor.Id);
            counts.Calls++;
            if (collection != null)
            {
                counts.KnownCountSamples++;
                counts.KnownCountSum += collection.Count;
                if (collection.Count > counts.KnownCountMax) counts.KnownCountMax = collection.Count;
            }
            ctx.Rows[descriptor.Id] = counts;
        }

        private static void RecordSourceNull(TraceContext ctx, MethodBase method)
        {
            MethodDescriptor descriptor;
            if (ctx == null || !MethodMap.TryGetValue(method, out descriptor)) return;
            MethodCounts counts = GetCounts(ctx, descriptor.Id);
            counts.FalseOrNull++;
            ctx.Rows[descriptor.Id] = counts;
        }

        private static MethodCounts GetCounts(TraceContext ctx, int id)
        {
            MethodCounts counts;
            if (!ctx.Rows.TryGetValue(id, out counts)) counts = default(MethodCounts);
            return counts;
        }

        private static TraceRecord BuildRecord(TraceContext ctx, ThinkResult result)
        {
            string resultJob = "<none>";
            string source = "<none>";
            try
            {
                resultJob = JobName(result.Job);
                if (result.SourceNode != null) source = result.SourceNode.GetType().FullName;
            }
            catch { failures++; }

            List<string> rows = new List<string>();
            IEnumerable<KeyValuePair<int, MethodCounts>> ordered = ctx.Rows
                .OrderByDescending(kv => IsPlantRow(kv.Key))
                .ThenByDescending(kv => kv.Value.TrueOrNonNull)
                .ThenByDescending(kv => kv.Value.Calls)
                .Take(MaxRowsPerTrace);

            foreach (KeyValuePair<int, MethodCounts> pair in ordered)
            {
                MethodDescriptor descriptor = FindDescriptor(pair.Key);
                MethodCounts counts = pair.Value;
                if (descriptor == null) continue;

                StringBuilder row = new StringBuilder();
                row.Append(descriptor.Name)
                   .Append("[calls=").Append(counts.Calls);

                if (descriptor.Kind == MethodKind.Bool)
                    row.Append(",true/false=").Append(counts.TrueOrNonNull).Append('/').Append(counts.FalseOrNull);
                else if (descriptor.Kind == MethodKind.Job)
                    row.Append(",job/null=").Append(counts.TrueOrNonNull).Append('/').Append(counts.FalseOrNull);
                else
                {
                    row.Append(",null=").Append(counts.FalseOrNull);
                    if (counts.KnownCountSamples > 0)
                        row.Append(",knownCount(avg/max)=")
                           .Append((counts.KnownCountSum / (double)counts.KnownCountSamples).ToString("F1"))
                           .Append('/').Append(counts.KnownCountMax);
                    else
                        row.Append(",knownCount=unknown(no enumeration)");
                }

                row.Append(']');
                rows.Add(row.ToString());
            }

            return new TraceRecord
            {
                Tick = ctx.Tick,
                Pawn = PawnText(ctx.Pawn),
                EntryJob = ctx.EntryJob,
                ResultJob = resultJob,
                Source = source,
                Rows = rows.ToArray()
            };
        }

        private static bool IsPlantRow(int id)
        {
            MethodDescriptor d = FindDescriptor(id);
            if (d == null || string.IsNullOrEmpty(d.Name)) return false;
            string n = d.Name;
            return n.IndexOf("Grow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Plant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Sow", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static MethodDescriptor FindDescriptor(int id)
        {
            foreach (KeyValuePair<MethodBase, MethodDescriptor> pair in MethodMap)
                if (pair.Value.Id == id) return pair.Value;
            return null;
        }

        private static void AddRecent(TraceRecord record)
        {
            Recent[recentPos] = record;
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(32768);
            sb.AppendLine("[Idle Authority Trace SAFE]");
            sb.Append("installedMethods=").Append(methodsPatched)
              .Append(", patchFailures=").Append(patchFailures)
              .Append(", playerDetermines=").Append(playerDetermines)
              .Append(", armed=").Append(armedDetermines)
              .Append(", sampledBypass=").Append(sampledBypass)
              .Append(", idleOutcomes=").Append(idleOutcomes)
              .Append(", idleCaptured=").Append(idleCaptured)
              .Append(", armedNonIdleDiscarded=").Append(armedNonIdleDiscarded)
              .Append(", contextReentry=").Append(contextReentry)
              .Append(", failures=").Append(failures)
              .AppendLine();
            sb.AppendLine("Policy=read-only return observation; no extra WorkGiver/validator/CanReach/CanReserve calls; candidate IEnumerable values are never enumerated.");

            if (recentCount == 0)
            {
                sb.AppendLine("RecentIdleAuthority=none");
                return sb.ToString();
            }

            sb.AppendLine("RecentIdleAuthority=");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                TraceRecord record = Recent[(start + i) % RecentCapacity];
                sb.Append(" - tick=").Append(record.Tick)
                  .Append(", pawn=").Append(record.Pawn)
                  .Append(", entry=").Append(record.EntryJob)
                  .Append(", result=").Append(record.ResultJob)
                  .Append(", source=").Append(record.Source)
                  .AppendLine();

                string[] rows = record.Rows;
                if (rows == null || rows.Length == 0)
                {
                    sb.AppendLine("    workgiver-results=none-observed");
                    continue;
                }
                for (int r = 0; r < rows.Length; r++)
                    sb.Append("    ").AppendLine(rows[r]);
            }
            return sb.ToString();
        }

        internal static void Reset()
        {
            Array.Clear(Recent, 0, Recent.Length);
            LastIdleByPawn.Clear();
            current = null;
            recentPos = recentCount = determineSerial = 0;
            playerDetermines = armedDetermines = sampledBypass = idleOutcomes = idleCaptured = 0L;
            armedNonIdleDiscarded = contextReentry = failures = 0L;
        }

        private static Pawn GetPawn(Pawn_JobTracker tracker)
        {
            if (tracker == null || JobTrackerPawnField == null) return null;
            try { return JobTrackerPawnField.GetValue(tracker) as Pawn; }
            catch { failures++; return null; }
        }

        private static bool IsPlayerHumanlike(Pawn pawn)
        {
            try
            {
                return pawn != null && !pawn.Destroyed && pawn.RaceProps != null &&
                       pawn.RaceProps.Humanlike && pawn.Faction == Faction.OfPlayer;
            }
            catch { return false; }
        }

        private static Job SafeCurJob(Pawn pawn)
        {
            try { return pawn == null ? null : pawn.CurJob; }
            catch { return null; }
        }

        private static bool IsIdleOutcome(ThinkResult result)
        {
            try
            {
                if (result.SourceNode != null &&
                    result.SourceNode.GetType().FullName == "RimWorld.JobGiver_WanderColony")
                    return true;

                Job job = result.Job;
                if (job == null || job.def == null || string.IsNullOrEmpty(job.def.defName))
                    return false;
                string name = job.def.defName;
                return name.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       name == "Wait" || name.StartsWith("Wait_", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private static string JobName(Job job)
        {
            try
            {
                if (job == null) return "<none>";
                return job.def == null || string.IsNullOrEmpty(job.def.defName) ? "<job>" : job.def.defName;
            }
            catch { return "<error>"; }
        }

        private static string PawnText(Pawn pawn)
        {
            if (pawn == null) return "<null>";
            string label = null;
            try { label = pawn.LabelShortCap; } catch { }
            if (string.IsNullOrEmpty(label)) label = pawn.def == null ? "Pawn" : pawn.def.defName;
            return label + "#" + pawn.thingIDNumber;
        }

        private enum MethodKind
        {
            Bool,
            Job,
            ThingSource,
            CellSource
        }

        private sealed class MethodDescriptor
        {
            internal readonly int Id;
            internal readonly string Name;
            internal readonly MethodKind Kind;

            internal MethodDescriptor(int id, string name, MethodKind kind)
            {
                Id = id;
                Name = name;
                Kind = kind;
            }
        }

        private sealed class TraceContext
        {
            internal Pawn Pawn;
            internal int Tick;
            internal string EntryJob;
            internal Dictionary<int, MethodCounts> Rows;
        }

        private struct MethodCounts
        {
            internal int Calls;
            internal int TrueOrNonNull;
            internal int FalseOrNull;
            internal int KnownCountSamples;
            internal long KnownCountSum;
            internal int KnownCountMax;
        }

        private struct TraceRecord
        {
            internal int Tick;
            internal string Pawn;
            internal string EntryJob;
            internal string ResultJob;
            internal string Source;
            internal string[] Rows;
        }
    }
}
