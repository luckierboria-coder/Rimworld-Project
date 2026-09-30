using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMTWaitProbe
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string Id = "allen.rimmt.waitprobe";
        internal const string Version = "0.4.2";

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(Id);
                int core = CoreTrace.Install(harmony);
                int dcp = DcpTrace.Install(harmony);
                Diagnostics.Initialize();
                Log.Message("[RimMT Wait/DCP Deep Diagnostics v0.4.2] Installed read-only authority trace: core=" + core +
                    ", dcp=" + dcp + ". No GenClosest, WorkGiver, job, queue, draft state or ThinkResult is modified.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT Wait/DCP Deep Diagnostics v0.4.2] bootstrap failed: " + ex);
            }
        }
    }

    public sealed class WaitAuthorityDiagnosticsMod : Mod
    {
        public WaitAuthorityDiagnosticsMod(ModContentPack content) : base(content) { }

        public override string SettingsCategory()
        {
            return "RimMT Wait/DCP Deep Diagnostics";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect button = new Rect(inRect.x, inRect.y, 360f, 38f);
            if (Widgets.ButtonText(button, "立即写入等待/征召诊断报告"))
            {
                string path = Diagnostics.WriteReport("manual-settings-button");
                Messages.Message("诊断报告已写入: " + path, MessageTypeDefOf.TaskCompletion, false);
            }
            Widgets.Label(new Rect(inRect.x, inRect.y + 52f, inRect.width, 90f),
                "插件会自动记录征召变化、DCP 权威、强制任务链和长期 Wait。它不修改 Pawn、任务或队列。\n" +
                "报告: " + Diagnostics.ReportPath);
        }
    }

    public sealed class WaitAuthorityGameComponent : GameComponent
    {
        public WaitAuthorityGameComponent(Game game) { }

        public override void GameComponentTick()
        {
            Game game = Current.Game;
            int tick = game == null || game.tickManager == null ? 0 : game.tickManager.TicksGame;
            if (tick % 60 == 0) Diagnostics.Census(tick);
        }
    }

    internal static class CoreTrace
    {
        private static readonly FieldInfo DraftPawnField = AccessTools.Field(typeof(Pawn_DraftController), "pawn");

        internal static int Install(Harmony harmony)
        {
            int count = 0;
            count += Patch(harmony, AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted"), nameof(DraftPrefix), nameof(DraftPostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_DraftController), "DraftControllerTick"), null, nameof(DraftTickPostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), null, nameof(DeterminePostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "ShouldStartJobFromThinkTree"), null, nameof(ShouldStartThinkPostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "StartJob"), nameof(StartJobPrefix), nameof(StartJobPostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "EndCurrentJob"), nameof(EndJobPrefix), null);
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob"), nameof(OrderedPrefix), nameof(OrderedPostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "StartPath"), nameof(StartPathPrefix), nameof(StartPathPostfix));
            count += Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherFailed"), nameof(PatherFailedPrefix), null);
            return count;
        }

        private static int Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            if (target == null) return 0;
            try
            {
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(CoreTrace), prefix) { priority = int.MaxValue },
                    postfix == null ? null : new HarmonyMethod(typeof(CoreTrace), postfix) { priority = Priority.Last });
                Diagnostics.RegisterTarget(target);
                return 1;
            }
            catch (Exception ex)
            {
                Diagnostics.RecordRaw("core hook skipped target=" + target.DeclaringType.FullName + "." + target.Name +
                    " error=" + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        public static void DraftPrefix(Pawn_DraftController __instance, bool value, out DraftChangeState __state)
        {
            Pawn pawn = GetPawn(__instance);
            bool before = false;
            try { before = __instance.Drafted; } catch { }
            __state = new DraftChangeState(pawn, before, value, Diagnostics.CompactStack(2));
        }

        public static void DraftPostfix(Pawn_DraftController __instance, bool value, DraftChangeState __state)
        {
            if (__state == null || !Diagnostics.Eligible(__state.Pawn)) return;
            bool after;
            try { after = __instance.Drafted; } catch { return; }
            if (__state.Before == after) return;
            Diagnostics.Record(__state.Pawn, "DRAFT-SET", "before=" + __state.Before + " requested=" + value +
                " after=" + after + " caller=" + __state.Caller, true);
            Diagnostics.AcceptDraftTransition(__state.Pawn, after);
            Diagnostics.ObservePawn(__state.Pawn, "setter");
        }

        public static void DraftTickPostfix(Pawn_DraftController __instance)
        {
            Pawn pawn = GetPawn(__instance);
            if (Diagnostics.Eligible(pawn)) Diagnostics.ObserveDraftOnly(pawn, "draft-controller-tick");
        }

        public static void DeterminePostfix(Pawn_JobTracker __instance, Pawn ___pawn, ThinkResult __result)
        {
            if (!Diagnostics.Eligible(___pawn)) return;
            Job job = null;
            ThinkNode source = null;
            try { job = __result.Job; source = __result.SourceNode; } catch { }
            if (!Diagnostics.IsWait(job)) return;
            Diagnostics.Record(___pawn, "THINK-WAIT", "result=" + Diagnostics.JobText(job) +
                " source=" + (source == null ? "<null>" : source.GetType().FullName) +
                " state=" + Diagnostics.PawnText(___pawn), job != null && job.def == JobDefOf.Wait_Combat);
        }

        public static void ShouldStartThinkPostfix(Pawn ___pawn, ThinkResult thinkResult, bool __result, bool __runOriginal)
        {
            if (!Diagnostics.Eligible(___pawn)) return;
            Job job = null;
            try { job = thinkResult.Job; } catch { }
            if (__result && !Diagnostics.IsWait(job)) return;
            if (!Diagnostics.Interesting(___pawn, job) && !Diagnostics.IsDcpActive(___pawn)) return;
            Diagnostics.Record(___pawn, "THINK-START-DECISION", "accepted=" + __result +
                " originalRan=" + __runOriginal + " proposed=" + Diagnostics.JobText(job) +
                " " + Diagnostics.PawnText(___pawn), !__result);
        }

        public static void StartJobPrefix(Pawn ___pawn, Job newJob, JobCondition lastJobEndCondition, ThinkNode jobGiver, bool fromQueue, out JobCallState __state)
        {
            __state = new JobCallState(___pawn, newJob, Diagnostics.JobText(___pawn == null ? null : ___pawn.CurJob),
                "end=" + lastJobEndCondition + " giver=" + (jobGiver == null ? "<null>" : jobGiver.GetType().FullName) +
                " fromQueue=" + fromQueue + " caller=" + Diagnostics.CompactStack(2));
        }

        public static void StartJobPostfix(Pawn ___pawn, Job newJob, JobCallState __state)
        {
            if (__state == null || !Diagnostics.Eligible(___pawn)) return;
            if (!Diagnostics.Interesting(___pawn, newJob)) return;
            Diagnostics.Record(___pawn, "START-JOB", "old=" + __state.OldJob + " requested=" + Diagnostics.JobText(newJob) +
                " actual=" + Diagnostics.JobText(___pawn.CurJob) + " " + __state.Context, Diagnostics.IsWait(newJob));
            Diagnostics.ObservePawn(___pawn, "start-job");
        }

        public static void EndJobPrefix(Pawn ___pawn, JobCondition condition, bool startNewJob)
        {
            if (!Diagnostics.Eligible(___pawn) || !Diagnostics.Interesting(___pawn, ___pawn.CurJob)) return;
            Diagnostics.Record(___pawn, "END-JOB", "job=" + Diagnostics.JobText(___pawn.CurJob) +
                " condition=" + condition + " startNew=" + startNewJob + " caller=" + Diagnostics.CompactStack(2), false);
        }

        public static void OrderedPrefix(Pawn ___pawn, Job job, JobTag? tag, bool requestQueueing, out JobCallState __state)
        {
            __state = new JobCallState(___pawn, job, Diagnostics.JobText(___pawn == null ? null : ___pawn.CurJob),
                "tag=" + (tag.HasValue ? tag.Value.ToString() : "<null>") + " requestQueue=" + requestQueueing +
                " caller=" + Diagnostics.CompactStack(2));
        }

        public static void OrderedPostfix(Pawn ___pawn, Job job, bool __result, JobCallState __state)
        {
            if (__state == null || !Diagnostics.Eligible(___pawn)) return;
            Diagnostics.Record(___pawn, "ORDERED-JOB", "accepted=" + __result + " old=" + __state.OldJob +
                " requested=" + Diagnostics.JobText(job) + " actual=" + Diagnostics.JobText(___pawn.CurJob) +
                " " + __state.Context, true);
            Diagnostics.ObservePawn(___pawn, "ordered-job");
        }

        public static void StartPathPrefix(Pawn ___pawn, LocalTargetInfo dest, PathEndMode peMode, out PathCallState __state)
        {
            __state = new PathCallState(___pawn, Diagnostics.JobText(___pawn == null ? null : ___pawn.CurJob),
                Diagnostics.TargetText(dest), peMode);
        }

        public static void StartPathPostfix(Pawn_PathFollower __instance, Pawn ___pawn, PathCallState __state)
        {
            if (__state == null || !Diagnostics.Eligible(___pawn)) return;
            Job current = ___pawn.CurJob;
            bool replacedByWait = Diagnostics.IsWait(current) && !__state.Job.StartsWith("Wait", StringComparison.Ordinal);
            if (!replacedByWait) return;
            Diagnostics.Record(___pawn, "STARTPATH-FAILED", "originalJob=" + __state.Job + " dest=" + __state.Destination +
                " mode=" + __state.Mode + " after=" + Diagnostics.JobText(current) + " moving=" +
                (__instance != null && __instance.Moving), true);
        }

        public static void PatherFailedPrefix(Pawn ___pawn)
        {
            if (!Diagnostics.Eligible(___pawn)) return;
            Diagnostics.Record(___pawn, "PATHER-FAILED", "jobBeforeFailure=" + Diagnostics.JobText(___pawn.CurJob) +
                " state=" + Diagnostics.PawnText(___pawn), true);
        }

        private static Pawn GetPawn(Pawn_DraftController controller)
        {
            try { return DraftPawnField == null ? null : DraftPawnField.GetValue(controller) as Pawn; }
            catch { return null; }
        }
    }

    internal static class DcpTrace
    {
        private static readonly HashSet<int> ObservedMarks = new HashSet<int>();
        internal static Type GateType;
        internal static FieldInfo ActiveField;
        internal static FieldInfo SettingsField;
        internal static Type SettingsType;

        internal static int Install(Harmony harmony)
        {
            GateType = AccessTools.TypeByName("DraftedCommandPriority.PlayerCommandGate");
            Type modType = AccessTools.TypeByName("DraftedCommandPriority.DcpMod");
            SettingsType = AccessTools.TypeByName("DraftedCommandPriority.DcpSettings");
            if (GateType == null)
            {
                Diagnostics.RecordRaw("DCP assembly/type not found; core diagnostics remain active.");
                return 0;
            }

            ActiveField = AccessTools.Field(GateType, "Active");
            SettingsField = modType == null ? null : AccessTools.Field(modType, "Settings");
            int count = 0;
            count += Patch(harmony, AccessTools.Method(GateType, "Mark"), nameof(MarkPostfix));
            count += Patch(harmony, AccessTools.Method(GateType, "Clear"), nameof(ClearPostfix));
            count += Patch(harmony, AccessTools.Method(GateType, "HasLivePlayerChain"), nameof(LivePostfix));
            return count;
        }

        private static int Patch(Harmony harmony, MethodBase target, string postfix)
        {
            if (target == null) return 0;
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(DcpTrace), postfix) { priority = Priority.Last });
                Diagnostics.RegisterTarget(target);
                return 1;
            }
            catch (Exception ex)
            {
                Diagnostics.RecordRaw("DCP hook skipped target=" + target.DeclaringType.FullName + "." + target.Name +
                    " error=" + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        public static void MarkPostfix(Pawn pawn)
        {
            if (Diagnostics.Eligible(pawn) && ObservedMarks.Add(pawn.thingIDNumber))
                Diagnostics.Record(pawn, "DCP-MARK", Diagnostics.PawnText(pawn), true);
        }

        public static void ClearPostfix(Pawn pawn)
        {
            if (Diagnostics.Eligible(pawn) && ObservedMarks.Remove(pawn.thingIDNumber))
                Diagnostics.Record(pawn, "DCP-CLEAR", Diagnostics.PawnText(pawn), false);
        }

        public static void LivePostfix(Pawn pawn, Pawn_JobTracker tracker, bool __result)
        {
            if (!Diagnostics.Eligible(pawn)) return;
            bool changed = Diagnostics.ObserveDcpLive(pawn, __result);
            if ((__result && !pawn.Drafted) || (changed && __result))
                Diagnostics.Record(pawn, "DCP-LIVE", "result=" + __result + " " + Diagnostics.PawnText(pawn), true);
        }

        internal static bool IsActive(int pawnId)
        {
            try
            {
                IEnumerable values = ActiveField == null ? null : ActiveField.GetValue(null) as IEnumerable;
                if (values == null) return false;
                foreach (object value in values) if (value is int && (int)value == pawnId) return true;
            }
            catch { }
            return false;
        }

        internal static string SettingsText()
        {
            try
            {
                object settings = SettingsField == null ? null : SettingsField.GetValue(null);
                if (settings == null || SettingsType == null) return "settings=<null>";
                return "enabled=" + Field(settings, "enabled") + " meleeAutoAttack=" + Field(settings, "meleeAutoAttack") +
                    " radius=" + Field(settings, "meleeAutoAttackRadius") + " logBlocked=" + Field(settings, "logBlockedJobs");
            }
            catch (Exception ex) { return "settingsError=" + ex.GetType().Name; }
        }

        private static object Field(object instance, string name)
        {
            FieldInfo field = AccessTools.Field(SettingsType, name);
            return field == null ? "<missing>" : field.GetValue(instance);
        }
    }

    internal static class Diagnostics
    {
        private const int EventLimit = 768;
        private const int LongWaitTicks = 600;
        private static readonly Dictionary<int, PawnState> States = new Dictionary<int, PawnState>();
        private static readonly List<string> Events = new List<string>(EventLimit);
        private static readonly List<MethodBase> Targets = new List<MethodBase>();
        private static int eventNumber;
        private static int lastAutoReportTick = -999999;

        internal static string ReportPath => Path.Combine(GenFilePaths.ConfigFolderPath, "RimMT-WaitAuthority-Diagnostics-latest.txt");

        internal static void Initialize()
        {
            RecordRaw("session-start version=" + Bootstrap.Version + " time=" + DateTime.Now.ToString("O"));
        }

        internal static void RegisterTarget(MethodBase target)
        {
            if (target != null && !Targets.Contains(target)) Targets.Add(target);
        }

        internal static bool Eligible(Pawn pawn)
        {
            return Current.Game != null && pawn != null && pawn.Faction == Faction.OfPlayer &&
                pawn.RaceProps != null && pawn.RaceProps.Humanlike;
        }

        internal static bool IsWait(Job job)
        {
            if (job == null || job.def == null) return false;
            return job.def == JobDefOf.Wait || job.def == JobDefOf.Wait_Combat || job.def.isIdle ||
                (job.def.defName != null && job.def.defName.StartsWith("Wait", StringComparison.Ordinal));
        }

        internal static bool Interesting(Pawn pawn, Job job)
        {
            return Eligible(pawn) && (pawn.Drafted || IsWait(job) || (job != null && job.playerForced) || IsDcpActive(pawn));
        }

        internal static void ObservePawn(Pawn pawn, string source)
        {
            if (!Eligible(pawn)) return;
            int tick = Tick;
            PawnState state = GetState(pawn);
            ObserveDraftOnly(pawn, source);
            bool drafted = false;
            try { drafted = pawn.Drafted; } catch { }

            string job = JobText(pawn.CurJob);
            if (state.JobText != job)
            {
                if (drafted || IsWait(pawn.CurJob) || IsDcpActive(pawn) || state.WasInteresting)
                    Record(pawn, "JOB-OBSERVED", "before=" + state.JobText + " after=" + job + " observer=" + source +
                        " " + PawnText(pawn), IsWait(pawn.CurJob));
                state.JobText = job;
            }

            bool waiting = IsWait(pawn.CurJob);
            if (waiting && state.WaitSince < 0) state.WaitSince = tick;
            if (!waiting) state.WaitSince = -1;
            state.WasInteresting = drafted || waiting || IsDcpActive(pawn);
            state.LastSeenTick = tick;
        }

        internal static void ObserveDraftOnly(Pawn pawn, string source)
        {
            if (!Eligible(pawn)) return;
            PawnState state = GetState(pawn);
            bool drafted;
            try { drafted = pawn.Drafted; } catch { return; }
            if (!state.DraftKnown)
            {
                state.DraftKnown = true;
                state.Drafted = drafted;
                if (drafted) Record(pawn, "INITIAL-DRAFT", "source=" + source + " " + PawnText(pawn), true);
                return;
            }
            if (state.Drafted == drafted) return;
            bool before = state.Drafted;
            state.Drafted = drafted;
            Record(pawn, "DIRECT-DRAFT-CHANGE", "before=" + before + " after=" + drafted +
                " observer=" + source + " (setter hook did not establish this transition) " + PawnText(pawn), true);
        }

        internal static void AcceptDraftTransition(Pawn pawn, bool drafted)
        {
            if (!Eligible(pawn)) return;
            PawnState state = GetState(pawn);
            state.DraftKnown = true;
            state.Drafted = drafted;
        }

        internal static bool ObserveDcpLive(Pawn pawn, bool live)
        {
            PawnState state = GetState(pawn);
            bool changed = !state.DcpKnown || state.DcpLive != live;
            if (!state.DcpKnown || state.DcpLive != live)
            {
                state.DcpKnown = true;
                state.DcpLive = live;
                if (live) Record(pawn, "DCP-LIVE-TRANSITION", "live=true " + PawnText(pawn), true);
            }
            return changed;
        }

        internal static void Census(int tick)
        {
            Game game = Current.Game;
            if (game == null || game.Maps == null) return;
            List<Pawn> pawns = new List<Pawn>();
            foreach (Map map in game.Maps)
            {
                if (map == null || map.mapPawns == null) continue;
                pawns.AddRange(map.mapPawns.FreeColonistsSpawned.Where(Eligible));
            }

            int waiting = 0;
            int waitCombat = 0;
            int anomalies = 0;
            foreach (Pawn pawn in pawns)
            {
                ObservePawn(pawn, "60-tick-census");
                PawnState state = GetState(pawn);
                bool isWait = IsWait(pawn.CurJob);
                if (isWait) waiting++;
                if (pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Wait_Combat) waitCombat++;

                bool active = IsDcpActive(pawn);
                bool forced = HasForcedChain(pawn);
                bool anomaly = (active && (!pawn.Drafted || !forced)) || (!pawn.Drafted && forced) ||
                    (state.WaitSince >= 0 && tick - state.WaitSince >= LongWaitTicks) ||
                    (pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Wait_Combat);
                if (anomaly) anomalies++;

                if (state.WaitSince >= 0 && tick - state.WaitSince >= LongWaitTicks && tick - state.LastLongWaitLog >= 600)
                {
                    state.LastLongWaitLog = tick;
                    Record(pawn, "LONG-WAIT", "durationTicks=" + (tick - state.WaitSince) + " " + PawnText(pawn), true);
                }
                if (active && (!pawn.Drafted || !forced) && tick - state.LastAuthorityLog >= 300)
                {
                    state.LastAuthorityLog = tick;
                    Record(pawn, "DCP-AUTHORITY-ANOMALY", "active=true drafted=" + pawn.Drafted +
                        " forcedChain=" + forced + " " + PawnText(pawn), true);
                }
            }

            if ((anomalies > 0 || waiting >= 4 || waitCombat > 0) && tick - lastAutoReportTick >= 300)
            {
                lastAutoReportTick = tick;
                WriteReport("auto census pawns=" + pawns.Count + " waiting=" + waiting + " waitCombat=" + waitCombat + " anomalies=" + anomalies);
            }
        }

        internal static void Record(Pawn pawn, string kind, string detail, bool important)
        {
            string row = "#" + (++eventNumber) + " tick=" + Tick + " kind=" + kind + " pawn=" +
                (pawn == null ? "<null>" : pawn.LabelShortCap + "#" + pawn.thingIDNumber) + " " + detail;
            AddEvent(row);
            if (important) Log.Message("[RimMT Wait/DCP Deep Diagnostics v0.4.2] " + row);
        }

        internal static void RecordRaw(string detail)
        {
            AddEvent("#" + (++eventNumber) + " tick=" + Tick + " kind=SYSTEM " + detail);
        }

        private static void AddEvent(string row)
        {
            Events.Add(row);
            if (Events.Count > EventLimit) Events.RemoveRange(0, Events.Count - EventLimit);
        }

        internal static string WriteReport(string reason)
        {
            try
            {
                StringBuilder sb = new StringBuilder(65536);
                sb.AppendLine("RimMT Wait/DCP Deep Diagnostics v0.4.2");
                sb.AppendLine("Generated=" + DateTime.Now.ToString("O"));
                sb.AppendLine("Tick=" + Tick);
                sb.AppendLine("Reason=" + reason);
                sb.AppendLine("DCP=" + DcpTrace.SettingsText());
                sb.AppendLine();
                sb.AppendLine("=== Current player-human pawn census ===");
                Game game = Current.Game;
                if (game != null && game.Maps != null)
                {
                    foreach (Map map in game.Maps)
                    {
                        if (map == null || map.mapPawns == null) continue;
                        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.Where(Eligible).OrderBy(p => p.thingIDNumber))
                        {
                            PawnState state = GetState(pawn);
                            sb.AppendLine(PawnText(pawn) + " waitSince=" + state.WaitSince +
                                " waitDuration=" + (state.WaitSince < 0 ? 0 : Tick - state.WaitSince));
                        }
                    }
                }
                sb.AppendLine();
                sb.AppendLine("=== Harmony authority census ===");
                foreach (MethodBase target in Targets) sb.AppendLine(PatchText(target));
                sb.AppendLine();
                sb.AppendLine("=== Event ring ===");
                foreach (string row in Events) sb.AppendLine(row);
                File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
                return ReportPath;
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT Wait/DCP Deep Diagnostics v0.4.2] report write failed: " + ex);
                return ReportPath;
            }
        }

        internal static string PawnText(Pawn pawn)
        {
            if (pawn == null) return "pawn=<null>";
            JobQueue queue = pawn.jobs == null ? null : pawn.jobs.jobQueue;
            bool stunned = pawn.stances != null && pawn.stances.stunner != null && pawn.stances.stunner.Stunned;
            string assignment = "<none>";
            try { assignment = pawn.timetable == null || pawn.timetable.CurrentAssignment == null ? "<none>" : pawn.timetable.CurrentAssignment.defName; } catch { }
            return "drafted=" + pawn.Drafted + " dcpActive=" + IsDcpActive(pawn) + " dcpLive=" + GetState(pawn).DcpLive +
                " job=" + JobText(pawn.CurJob) + " queue=" + (queue == null ? -1 : queue.Count) +
                " queueForced=" + (queue != null && queue.AnyPlayerForced) + " assignment=" + assignment +
                " mental=" + pawn.InMentalState + " downed=" + pawn.Downed + " stunned=" + stunned +
                " spawned=" + pawn.Spawned + " pos=" + pawn.Position;
        }

        internal static string JobText(Job job)
        {
            if (job == null) return "<none>";
            return (job.def == null ? "<nullDef>" : job.def.defName) + "#" + job.loadID +
                "[forced=" + job.playerForced + ",interruptedForced=" + job.playerInterruptedForced +
                ",A=" + TargetText(job.targetA) + ",B=" + TargetText(job.targetB) +
                ",C=" + TargetText(job.targetC) + "]";
        }

        internal static string TargetText(LocalTargetInfo target)
        {
            if (!target.IsValid) return "<invalid>";
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                return thing == null ? "<nullThing>" : thing.LabelShortCap + "#" + thing.thingIDNumber + "@" + thing.Position;
            }
            return target.Cell.ToString();
        }

        internal static bool IsDcpActive(Pawn pawn)
        {
            return pawn != null && DcpTrace.IsActive(pawn.thingIDNumber);
        }

        private static bool HasForcedChain(Pawn pawn)
        {
            if (pawn == null || pawn.jobs == null) return false;
            if (pawn.CurJob != null && pawn.CurJob.playerForced) return true;
            return pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.AnyPlayerForced;
        }

        internal static string CompactStack(int skip)
        {
            StackFrame[] frames = new StackTrace(skip, false).GetFrames();
            if (frames == null) return "<no-stack>";
            List<string> rows = new List<string>(12);
            for (int i = 0; i < frames.Length && rows.Count < 12; i++)
            {
                MethodBase method = frames[i].GetMethod();
                if (method == null) continue;
                string full = (method.DeclaringType == null ? "<global>" : method.DeclaringType.FullName) + "." + method.Name;
                if (full.StartsWith("RimMTWaitProbe.", StringComparison.Ordinal) || full.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
                    full.StartsWith("System.Reflection.", StringComparison.Ordinal)) continue;
                rows.Add(full);
            }
            return rows.Count == 0 ? "<filtered>" : string.Join(" <- ", rows.ToArray());
        }

        private static string PatchText(MethodBase target)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(target);
                string owners = info == null ? "<none>" : string.Join(",", info.Owners.ToArray());
                return target.DeclaringType.FullName + "." + target.Name + " owners=" + owners;
            }
            catch (Exception ex) { return target.Name + " patchInfoError=" + ex.GetType().Name; }
        }

        private static PawnState GetState(Pawn pawn)
        {
            int id = pawn == null ? -1 : pawn.thingIDNumber;
            PawnState state;
            if (!States.TryGetValue(id, out state))
            {
                state = new PawnState();
                States.Add(id, state);
            }
            return state;
        }

        private static int Tick
        {
            get
            {
                Game game = Current.Game;
                return game == null || game.tickManager == null ? -1 : game.tickManager.TicksGame;
            }
        }
    }

    internal sealed class PawnState
    {
        internal bool DraftKnown;
        internal bool Drafted;
        internal bool DcpKnown;
        internal bool DcpLive;
        internal bool WasInteresting;
        internal string JobText = "<unknown>";
        internal int WaitSince = -1;
        internal int LastSeenTick = -1;
        internal int LastLongWaitLog = -999999;
        internal int LastAuthorityLog = -999999;
    }

    internal sealed class DraftChangeState
    {
        internal readonly Pawn Pawn;
        internal readonly bool Before;
        internal readonly bool Requested;
        internal readonly string Caller;
        internal DraftChangeState(Pawn pawn, bool before, bool requested, string caller)
        {
            Pawn = pawn; Before = before; Requested = requested; Caller = caller;
        }
    }

    internal sealed class JobCallState
    {
        internal readonly Pawn Pawn;
        internal readonly Job Requested;
        internal readonly string OldJob;
        internal readonly string Context;
        internal JobCallState(Pawn pawn, Job requested, string oldJob, string context)
        {
            Pawn = pawn; Requested = requested; OldJob = oldJob; Context = context;
        }
    }

    internal sealed class PathCallState
    {
        internal readonly Pawn Pawn;
        internal readonly string Job;
        internal readonly string Destination;
        internal readonly PathEndMode Mode;
        internal PathCallState(Pawn pawn, string job, string destination, PathEndMode mode)
        {
            Pawn = pawn; Job = job; Destination = destination; Mode = mode;
        }
    }
}
