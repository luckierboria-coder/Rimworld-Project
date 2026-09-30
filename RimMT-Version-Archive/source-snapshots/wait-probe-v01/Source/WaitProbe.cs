using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTWaitProbe
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string Id = "allen.rimmt.waitprobe";
        internal const string Version = "0.2";

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(Id);
                WaitTrace.Install(harmony);
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT Wait Probe v0.2] bootstrap failed: " + ex);
            }
        }
    }

    internal static class WaitTrace
    {
        private const int MaxDetermineCalls = 4096;
        private const int MaxWaitReports = 128;
        private const int MaxGiversPerReport = 24;

        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
        private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
        private static int determineCalls;
        private static int waitReports;
        private static int patchFailures;
        private static bool enabled = true;

        [ThreadStatic]
        private static TraceContext current;

        internal static void Install(Harmony harmony)
        {
            Patch(harmony,
                AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"),
                nameof(DeterminePrefix), nameof(DeterminePostfix));
            Patch(harmony,
                AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new[] { typeof(Pawn), typeof(JobIssueParams) }),
                nameof(WorkPrefix), nameof(WorkPostfix));
            PatchPostfix(harmony,
                AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver", new[] { typeof(Pawn), typeof(WorkGiver) }),
                nameof(PawnCanUsePostfix));

            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(GenClosest)))
            {
                if (method == null || method.ReturnType != typeof(Thing)) continue;
                if (method.Name == "ClosestThingReachable" || method.Name == "ClosestThing_Global" ||
                    method.Name == "ClosestThing_Global_Reachable")
                    PatchPostfix(harmony, method, nameof(ClosestThingPostfix));
            }

            foreach (WorkGiverDef def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                WorkGiver worker;
                try { worker = def.Worker; }
                catch { patchFailures++; continue; }
                if (worker == null) continue;

                Type type = worker.GetType();
                PatchPostfix(harmony, AccessTools.Method(type, "ShouldSkip", new[] { typeof(Pawn), typeof(bool) }), nameof(ShouldSkipPostfix));
                PatchPostfix(harmony, AccessTools.Method(type, "NonScanJob", new[] { typeof(Pawn) }), nameof(NonScanPostfix));

                if (!(worker is WorkGiver_Scanner)) continue;
                PatchPostfix(harmony, AccessTools.Method(type, "PotentialWorkThingsGlobal", new[] { typeof(Pawn) }), nameof(PotentialThingsPostfix));
                PatchPostfix(harmony, AccessTools.Method(type, "PotentialWorkCellsGlobal", new[] { typeof(Pawn) }), nameof(PotentialCellsPostfix));
                PatchPostfix(harmony, AccessTools.Method(type, "HasJobOnThing", new[] { typeof(Pawn), typeof(Thing), typeof(bool) }), nameof(HasThingPostfix));
                PatchPostfix(harmony, AccessTools.Method(type, "HasJobOnCell", new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) }), nameof(HasCellPostfix));
                PatchPostfix(harmony, AccessTools.Method(type, "JobOnThing", new[] { typeof(Pawn), typeof(Thing), typeof(bool) }), nameof(JobThingPostfix));
                PatchPostfix(harmony, AccessTools.Method(type, "JobOnCell", new[] { typeof(Pawn), typeof(IntVec3), typeof(bool) }), nameof(JobCellPostfix));
            }

            Log.Message("[RimMT Wait Probe v0.2] Installed bounded main-thread Wait trace: methods=" +
                Patched.Count + ", patchFailures=" + patchFailures +
                ". No job or ThinkResult is modified.");
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            if (target == null || !Patched.Add(target)) return;
            try
            {
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(WaitTrace), prefix) { priority = Priority.First },
                    postfix == null ? null : new HarmonyMethod(typeof(WaitTrace), postfix) { priority = Priority.Last });
            }
            catch { patchFailures++; }
        }

        private static void PatchPostfix(Harmony harmony, MethodBase target, string postfix)
        {
            Patch(harmony, target, null, postfix);
        }

        public static void DeterminePrefix(Pawn_JobTracker __instance)
        {
            if (!enabled || ++determineCalls > MaxDetermineCalls)
            {
                DisableIfNeeded();
                current = null;
                return;
            }

            Pawn pawn = GetPawn(__instance);
            if (!Eligible(pawn))
            {
                current = null;
                return;
            }

            current = new TraceContext(pawn);
        }

        public static void DeterminePostfix(Pawn_JobTracker __instance, ThinkResult __result)
        {
            TraceContext trace = current;
            current = null;
            if (trace == null) return;

            Job result = null;
            ThinkNode source = null;
            try { result = __result.Job; source = __result.SourceNode; }
            catch { }

            if (!IsWait(result)) return;
            if (++waitReports > MaxWaitReports)
            {
                DisableIfNeeded();
                return;
            }

            trace.FinalJob = result == null || result.def == null ? "NoJob" : result.def.defName;
            trace.FinalSource = source == null ? "<null>" : source.GetType().FullName;
            Log.Message(trace.Format(waitReports));
            DisableIfNeeded();
        }

        public static void WorkPrefix(Pawn pawn)
        {
            if (Matches(pawn)) current.WorkCalls++;
        }

        public static void WorkPostfix(Pawn pawn, ThinkResult __result)
        {
            if (!Matches(pawn)) return;
            Job job = null;
            try { job = __result.Job; }
            catch { }
            if (job == null) current.WorkNoJob++;
            else
            {
                current.WorkProduced++;
                current.WorkJobs.Add(job.def == null ? "<nullDef>" : job.def.defName);
            }
        }

        public static void PawnCanUsePostfix(Pawn pawn, WorkGiver giver, bool __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace row = current.For(giver);
            row.UseChecks++;
            current.ActiveGiver = row;
            if (__result)
            {
                row.UseAllowed++;
                return;
            }

            try
            {
                if (giver == null || giver.def == null) row.RejectOther++;
                else if (!giver.def.nonColonistsCanDo && !pawn.IsColonist && !pawn.IsColonyMech && !pawn.IsMutant) row.RejectColonistGate++;
                else if (pawn.WorkTagIsDisabled(giver.def.workTags)) row.RejectWorkTags++;
                else if (giver.def.workType != null && pawn.WorkTypeIsDisabled(giver.def.workType)) row.RejectWorkType++;
                else if (giver.MissingRequiredCapacity(pawn) != null) row.RejectCapacity++;
                else if (pawn.RaceProps.IsMechanoid && !giver.def.canBeDoneByMechs) row.RejectRace++;
                else if (pawn.IsMutant && !giver.def.canBeDoneByMutants) row.RejectRace++;
                else row.RejectShouldSkip++;
            }
            catch { row.RejectOther++; }
        }

        public static void ClosestThingPostfix(Thing __result)
        {
            if (current == null || current.ActiveGiver == null) return;
            current.ActiveGiver.ClosestCalls++;
            if (__result != null) current.ActiveGiver.ClosestFound++;
        }

        public static void ShouldSkipPostfix(WorkGiver __instance, Pawn pawn, bool __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.ShouldSkipCalls++;
            if (__result) giver.ShouldSkipTrue++;
        }

        public static void NonScanPostfix(WorkGiver __instance, Pawn pawn, Job __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.NonScanCalls++;
            if (__result != null) giver.NonScanJobs++;
        }

        public static void PotentialThingsPostfix(WorkGiver_Scanner __instance, Pawn pawn, IEnumerable<Thing> __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.ThingSources++;
            int count = CollectionCount(__result);
            if (count >= 0) giver.ThingSourceMembers += count;
            else giver.UnknownThingSources++;
        }

        public static void PotentialCellsPostfix(WorkGiver_Scanner __instance, Pawn pawn, IEnumerable<IntVec3> __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.CellSources++;
            int count = CollectionCount(__result);
            if (count >= 0) giver.CellSourceMembers += count;
            else giver.UnknownCellSources++;
        }

        public static void HasThingPostfix(WorkGiver_Scanner __instance, Pawn pawn, bool __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.HasThingCalls++;
            if (__result) giver.HasThingTrue++;
        }

        public static void HasCellPostfix(WorkGiver_Scanner __instance, Pawn pawn, bool __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.HasCellCalls++;
            if (__result) giver.HasCellTrue++;
        }

        public static void JobThingPostfix(WorkGiver_Scanner __instance, Pawn pawn, Job __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.JobThingCalls++;
            if (__result != null) giver.JobThingJobs++;
        }

        public static void JobCellPostfix(WorkGiver_Scanner __instance, Pawn pawn, Job __result)
        {
            if (!Matches(pawn)) return;
            GiverTrace giver = current.For(__instance);
            giver.JobCellCalls++;
            if (__result != null) giver.JobCellJobs++;
        }

        private static Pawn GetPawn(Pawn_JobTracker tracker)
        {
            try { return PawnField == null ? null : PawnField.GetValue(tracker) as Pawn; }
            catch { return null; }
        }

        private static bool Eligible(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && !pawn.Destroyed && pawn.Faction == Faction.OfPlayer &&
                pawn.RaceProps != null && pawn.RaceProps.Humanlike;
        }

        private static bool Matches(Pawn pawn)
        {
            return current != null && pawn != null && ReferenceEquals(current.Pawn, pawn);
        }

        private static bool IsWait(Job job)
        {
            if (job == null || job.def == null) return false;
            if (job.def == JobDefOf.Wait || job.def.isIdle) return true;
            return job.def.defName != null && job.def.defName.StartsWith("Wait", StringComparison.Ordinal);
        }

        private static int CollectionCount<T>(IEnumerable<T> source)
        {
            if (source == null) return 0;
            ICollection<T> generic = source as ICollection<T>;
            if (generic != null) return generic.Count;
            System.Collections.ICollection nongeneric = source as System.Collections.ICollection;
            return nongeneric == null ? -1 : nongeneric.Count;
        }

        private static void DisableIfNeeded()
        {
            if (!enabled) return;
            if (determineCalls <= MaxDetermineCalls && waitReports <= MaxWaitReports) return;
            enabled = false;
            Log.Message("[RimMT Wait Probe v0.2] Capture complete: determines=" + determineCalls +
                ", waitReports=" + waitReports + ". Probe remains patched but inert.");
        }

        private sealed class TraceContext
        {
            internal readonly Pawn Pawn;
            internal readonly int Tick;
            internal readonly Dictionary<string, GiverTrace> Givers = new Dictionary<string, GiverTrace>(StringComparer.Ordinal);
            internal readonly List<string> WorkJobs = new List<string>();
            internal GiverTrace ActiveGiver;
            internal int WorkCalls;
            internal int WorkNoJob;
            internal int WorkProduced;
            internal string FinalJob;
            internal string FinalSource;

            internal TraceContext(Pawn pawn)
            {
                Pawn = pawn;
                Tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
            }

            internal GiverTrace For(WorkGiver giver)
            {
                string key = giver == null || giver.def == null ? "<unknown>" : giver.def.defName;
                GiverTrace value;
                if (!Givers.TryGetValue(key, out value))
                {
                    value = new GiverTrace(key);
                    Givers.Add(key, value);
                }
                return value;
            }

            internal string Format(int reportNumber)
            {
                StringBuilder sb = new StringBuilder(4096);
                string assignment = "<none>";
                try { assignment = Pawn.timetable == null || Pawn.timetable.CurrentAssignment == null ? "<none>" : Pawn.timetable.CurrentAssignment.defName; }
                catch { }

                sb.Append("[RimMT Wait Probe v0.2] WAIT#").Append(reportNumber)
                    .Append(" pawn=").Append(Pawn.LabelShortCap).Append('#').Append(Pawn.thingIDNumber)
                    .Append(" tick=").Append(Tick)
                    .Append(" pos=").Append(Pawn.Position)
                    .Append(" assignment=").Append(assignment)
                    .Append(" final=").Append(FinalJob)
                    .Append(" source=").Append(FinalSource)
                    .Append(" work[calls/noJob/produced]=").Append(WorkCalls).Append('/').Append(WorkNoJob).Append('/').Append(WorkProduced)
                    .Append(" giversObserved=").Append(Givers.Count)
                    .Append(" workJobs=").Append(WorkJobs.Count == 0 ? "none" : string.Join(",", WorkJobs.ToArray()));

                List<GiverTrace> rows = Givers.Values.OrderByDescending(g => g.Score).ThenBy(g => g.Name).Take(MaxGiversPerReport).ToList();
                sb.Append(" details=");
                if (rows.Count == 0) sb.Append("none");
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i != 0) sb.Append(';');
                    sb.Append(rows[i].Format());
                }
                return sb.ToString();
            }
        }

        private sealed class GiverTrace
        {
            internal readonly string Name;
            internal int ShouldSkipCalls;
            internal int ShouldSkipTrue;
            internal int NonScanCalls;
            internal int NonScanJobs;
            internal int ThingSources;
            internal int ThingSourceMembers;
            internal int UnknownThingSources;
            internal int CellSources;
            internal int CellSourceMembers;
            internal int UnknownCellSources;
            internal int HasThingCalls;
            internal int HasThingTrue;
            internal int HasCellCalls;
            internal int HasCellTrue;
            internal int JobThingCalls;
            internal int JobThingJobs;
            internal int JobCellCalls;
            internal int JobCellJobs;
            internal int UseChecks;
            internal int UseAllowed;
            internal int RejectColonistGate;
            internal int RejectWorkTags;
            internal int RejectWorkType;
            internal int RejectShouldSkip;
            internal int RejectCapacity;
            internal int RejectRace;
            internal int RejectOther;
            internal int ClosestCalls;
            internal int ClosestFound;

            internal int Score => HasThingCalls + HasCellCalls + ThingSourceMembers + CellSourceMembers + NonScanCalls + ShouldSkipCalls + UseChecks + ClosestCalls;

            internal GiverTrace(string name) { Name = name; }

            internal string Format()
            {
                return Name + "{skip=" + ShouldSkipTrue + "/" + ShouldSkipCalls +
                    ",use=" + UseAllowed + "/" + UseChecks +
                    ",reject[colonist/tag/type/skip/cap/race/other]=" + RejectColonistGate + "/" + RejectWorkTags + "/" + RejectWorkType + "/" + RejectShouldSkip + "/" + RejectCapacity + "/" + RejectRace + "/" + RejectOther +
                    ",nonScan=" + NonScanJobs + "/" + NonScanCalls +
                    ",thingSrc=" + ThingSourceMembers + "/" + ThingSources + "+u" + UnknownThingSources +
                    ",hasThing=" + HasThingTrue + "/" + HasThingCalls +
                    ",jobThing=" + JobThingJobs + "/" + JobThingCalls +
                    ",cellSrc=" + CellSourceMembers + "/" + CellSources + "+u" + UnknownCellSources +
                    ",hasCell=" + HasCellTrue + "/" + HasCellCalls +
                    ",jobCell=" + JobCellJobs + "/" + JobCellCalls +
                    ",closest=" + ClosestFound + "/" + ClosestCalls + "}";
            }
        }
    }
}
