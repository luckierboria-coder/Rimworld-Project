using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace PawnIdleDiagnostics15
{
    public sealed class IdleDiagSettings : ModSettings
    {
        public bool autoDiagnose = true;
        public bool addPawnGizmo = true;
        public bool mirrorSummaryToPlayerLog = true;
        public int cooldownTicks = 2500;
        public int maxCandidatesPerGiver = 200;
        public int maxTotalCandidates = 3000;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref autoDiagnose, "autoDiagnose", true);
            Scribe_Values.Look(ref addPawnGizmo, "addPawnGizmo", true);
            Scribe_Values.Look(ref mirrorSummaryToPlayerLog, "mirrorSummaryToPlayerLog", true);
            Scribe_Values.Look(ref cooldownTicks, "cooldownTicks", 2500);
            Scribe_Values.Look(ref maxCandidatesPerGiver, "maxCandidatesPerGiver", 200);
            Scribe_Values.Look(ref maxTotalCandidates, "maxTotalCandidates", 3000);
        }
    }

    public sealed class IdleDiagMod : Mod
    {
        internal static IdleDiagSettings Settings;

        public IdleDiagMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<IdleDiagSettings>();
        }

        public override string SettingsCategory() => "Pawn Idle Diagnostics 1.5";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Auto-diagnose player colonists when an idle/wander job starts", ref Settings.autoDiagnose,
                "Schedules a read-only diagnostic snapshot when a colonist enters an idle-style job.");
            listing.CheckboxLabeled("Add 'Diagnose work/idle now' gizmo to player pawns", ref Settings.addPawnGizmo,
                "Lets you manually request a diagnostic for the selected pawn.");
            listing.CheckboxLabeled("Mirror one-line report summaries to Player.log", ref Settings.mirrorSummaryToPlayerLog,
                "Full reports are always written to PawnIdleDiagnostics.log.");

            listing.GapLine();
            listing.Label("Automatic per-pawn cooldown: " + Settings.cooldownTicks + " ticks");
            Settings.cooldownTicks = (int)listing.Slider(Settings.cooldownTicks, 250f, 15000f);
            listing.Label("Max candidates per WorkGiver: " + Settings.maxCandidatesPerGiver);
            Settings.maxCandidatesPerGiver = (int)listing.Slider(Settings.maxCandidatesPerGiver, 25f, 1000f);
            listing.Label("Max total candidates per pawn report: " + Settings.maxTotalCandidates);
            Settings.maxTotalCandidates = (int)listing.Slider(Settings.maxTotalCandidates, 500f, 10000f);
            listing.Gap();
            listing.Label("Output: " + IdleDiagLog.LogPath);
            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            Harmony harmony = new Harmony("allen.pawn.idle.diagnostics15");
            harmony.PatchAll();
            IdleDiagLog.InitializeSession();
            IdleDiagLog.WritePatchInventory();
            Log.Message("[IdleDiag] Pawn Idle Diagnostics 1.5 loaded. Read-only diagnostics enabled. Output: " + IdleDiagLog.LogPath);
        }
    }

    public sealed class IdleDiagGameComponent : GameComponent
    {
        public IdleDiagGameComponent(Game game) { }

        public override void GameComponentTick()
        {
            IdleDiagManager.ProcessQueue();
        }

        public override void LoadedGame()
        {
            IdleDiagManager.ResetRuntime();
            IdleDiagLog.WriteLine("");
            IdleDiagLog.WriteLine("=== Loaded game at " + DateTime.Now.ToString("s") + " ===");
        }

        public override void StartedNewGame()
        {
            IdleDiagManager.ResetRuntime();
            IdleDiagLog.WriteLine("");
            IdleDiagLog.WriteLine("=== Started new game at " + DateTime.Now.ToString("s") + " ===");
        }
    }

    internal sealed class PendingDiag
    {
        public Pawn pawn;
        public int scheduledTick;
        public bool manual;
        public string trigger;
        public string previousJob;
        public string idleJob;
        public string sourceNode;
        public string thinkTree;
        public string lastEndCondition;
    }

    internal sealed class WorkNodeTrace
    {
        public int tick;
        public bool emergency;
        public bool resultValid;
        public string resultJob;
        public string exception;
    }

    internal static class IdleDiagManager
    {
        private static readonly Queue<PendingDiag> queue = new Queue<PendingDiag>();
        private static readonly HashSet<int> queuedPawnIds = new HashSet<int>();
        private static readonly Dictionary<int, int> lastAutoDiagTick = new Dictionary<int, int>();
        private static readonly Dictionary<int, WorkNodeTrace> normalWorkTrace = new Dictionary<int, WorkNodeTrace>();
        private static readonly Dictionary<int, WorkNodeTrace> emergencyWorkTrace = new Dictionary<int, WorkNodeTrace>();
        private static int lastProcessTick = -99999;

        internal static void ResetRuntime()
        {
            queue.Clear();
            queuedPawnIds.Clear();
            lastAutoDiagTick.Clear();
            normalWorkTrace.Clear();
            emergencyWorkTrace.Clear();
            lastProcessTick = -99999;
        }

        internal static bool Eligible(Pawn pawn)
        {
            return pawn != null &&
                   !pawn.Destroyed &&
                   pawn.RaceProps != null &&
                   pawn.RaceProps.Humanlike &&
                   pawn.Faction == Faction.OfPlayer &&
                   pawn.Spawned;
        }

        internal static void RecordWorkNodeBegin(Pawn pawn, bool emergency)
        {
            if (!Eligible(pawn)) return;
            WorkNodeTrace trace = new WorkNodeTrace
            {
                tick = Find.TickManager?.TicksGame ?? -1,
                emergency = emergency,
                resultValid = false,
                resultJob = null,
                exception = null
            };
            if (emergency) emergencyWorkTrace[pawn.thingIDNumber] = trace;
            else normalWorkTrace[pawn.thingIDNumber] = trace;
        }

        internal static void RecordWorkNodeEnd(Pawn pawn, bool emergency, ThinkResult result)
        {
            if (!Eligible(pawn)) return;
            Dictionary<int, WorkNodeTrace> dict = emergency ? emergencyWorkTrace : normalWorkTrace;
            if (!dict.TryGetValue(pawn.thingIDNumber, out WorkNodeTrace trace))
            {
                trace = new WorkNodeTrace { emergency = emergency };
                dict[pawn.thingIDNumber] = trace;
            }
            trace.tick = Find.TickManager?.TicksGame ?? -1;
            trace.resultValid = result.IsValid;
            trace.resultJob = result.Job?.def?.defName;
        }

        internal static void RecordWorkNodeException(Pawn pawn, bool emergency, Exception ex)
        {
            if (!Eligible(pawn) || ex == null) return;
            Dictionary<int, WorkNodeTrace> dict = emergency ? emergencyWorkTrace : normalWorkTrace;
            if (!dict.TryGetValue(pawn.thingIDNumber, out WorkNodeTrace trace))
            {
                trace = new WorkNodeTrace { emergency = emergency };
                dict[pawn.thingIDNumber] = trace;
            }
            trace.tick = Find.TickManager?.TicksGame ?? -1;
            trace.exception = ex.GetType().FullName + ": " + ex.Message;
        }

        internal static WorkNodeTrace GetNormalTrace(Pawn pawn)
        {
            if (pawn == null) return null;
            normalWorkTrace.TryGetValue(pawn.thingIDNumber, out WorkNodeTrace trace);
            return trace;
        }

        internal static WorkNodeTrace GetEmergencyTrace(Pawn pawn)
        {
            if (pawn == null) return null;
            emergencyWorkTrace.TryGetValue(pawn.thingIDNumber, out WorkNodeTrace trace);
            return trace;
        }

        internal static void Schedule(Pawn pawn, bool manual, string trigger,
            string previousJob = null, string idleJob = null, string sourceNode = null,
            string thinkTree = null, string lastEndCondition = null)
        {
            if (!Eligible(pawn)) return;

            int tick = Find.TickManager?.TicksGame ?? 0;
            IdleDiagSettings settings = IdleDiagMod.Settings ?? new IdleDiagSettings();

            if (!manual)
            {
                if (!settings.autoDiagnose) return;
                if (lastAutoDiagTick.TryGetValue(pawn.thingIDNumber, out int last) &&
                    tick - last < settings.cooldownTicks)
                    return;
                lastAutoDiagTick[pawn.thingIDNumber] = tick;
            }

            if (queuedPawnIds.Contains(pawn.thingIDNumber)) return;

            queue.Enqueue(new PendingDiag
            {
                pawn = pawn,
                scheduledTick = tick,
                manual = manual,
                trigger = trigger,
                previousJob = previousJob,
                idleJob = idleJob,
                sourceNode = sourceNode,
                thinkTree = thinkTree,
                lastEndCondition = lastEndCondition
            });
            queuedPawnIds.Add(pawn.thingIDNumber);

            if (manual)
                Messages.Message("Idle diagnostic queued for " + pawn.LabelShortCap + ".", MessageTypeDefOf.NeutralEvent, false);
        }

        internal static void ProcessQueue()
        {
            if (queue.Count == 0 || Find.TickManager == null) return;
            int tick = Find.TickManager.TicksGame;
            if (tick == lastProcessTick || tick % 30 != 0) return;
            lastProcessTick = tick;

            PendingDiag pending = queue.Dequeue();
            if (pending.pawn != null)
                queuedPawnIds.Remove(pending.pawn.thingIDNumber);

            if (!Eligible(pending.pawn)) return;

            try
            {
                string report = IdleDiagnostic.BuildReport(pending);
                IdleDiagLog.WriteLine(report);
                IdleDiagLog.WriteLine("");

                IdleDiagSettings settings = IdleDiagMod.Settings ?? new IdleDiagSettings();
                if (settings.mirrorSummaryToPlayerLog)
                    Log.Message("[IdleDiag] report written for " + pending.pawn.LabelShortCap +
                                " (trigger=" + pending.trigger + "). File: " + IdleDiagLog.LogPath);
                if (pending.manual)
                    Messages.Message("Idle diagnostic written for " + pending.pawn.LabelShortCap + ".", MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception ex)
            {
                string msg = "[IdleDiag] diagnostic failed for " + pending.pawn.ToStringSafe() + ": " + ex;
                IdleDiagLog.WriteLine(msg);
                Log.Error(msg);
            }
        }
    }

    internal static class IdleDiagnostic
    {
        private sealed class ScanStats
        {
            public string giver;
            public string workType;
            public int priority;
            public string gate;
            public int candidates;
            public int forbidden;
            public int noHasJob;
            public int hasJob;
            public int unreachable;
            public int reachable;
            public int invalid;
            public int exceptions;
            public bool truncated;
            public string exampleReachable;
            public string exampleUnreachable;
            public string firstException;
        }

        internal static string BuildReport(PendingDiag pending)
        {
            Pawn pawn = pending.pawn;
            StringBuilder sb = new StringBuilder(16384);
            int tick = Find.TickManager?.TicksGame ?? -1;

            sb.AppendLine("================================================================================");
            sb.AppendLine("PAWN IDLE DIAGNOSTIC");
            sb.AppendLine("timestamp=" + DateTime.Now.ToString("s") + " tick=" + tick);
            sb.AppendLine("pawn=" + pawn.LabelShortCap + " thingID=" + pawn.thingIDNumber +
                          " kind=" + pawn.kindDef?.defName + " map=" + pawn.Map?.uniqueID +
                          " pos=" + pawn.Position);
            sb.AppendLine("trigger=" + pending.trigger + " manual=" + pending.manual +
                          " scheduledTick=" + pending.scheduledTick);
            sb.AppendLine("idleJob=" + Safe(pending.idleJob) +
                          " previousJob=" + Safe(pending.previousJob) +
                          " sourceNode=" + Safe(pending.sourceNode) +
                          " thinkTree=" + Safe(pending.thinkTree) +
                          " lastEndCondition=" + Safe(pending.lastEndCondition));

            AppendPawnState(sb, pawn);
            AppendWorkNodeTrace(sb, pawn, tick);
            AppendAreaState(sb, pawn);
            AppendWorkPriorities(sb, pawn);
            AppendNeeds(sb, pawn);

            int totalCandidates = 0;
            int passGivers = 0;
            int blockedGivers = 0;
            int reachableCandidates = 0;
            int unreachableCandidates = 0;
            int exceptionCount = 0;
            List<ScanStats> stats = DeepScanWorkGivers(pawn, ref totalCandidates);

            sb.AppendLine("--- WorkGiver deep scan (read-only; no JobOnThing/JobOnCell calls) ---");
            foreach (ScanStats st in stats)
            {
                if (st.gate == "PASS") passGivers++;
                else blockedGivers++;
                reachableCandidates += st.reachable;
                unreachableCandidates += st.unreachable;
                exceptionCount += st.exceptions;

                sb.Append(st.giver)
                  .Append(" workType=").Append(Safe(st.workType))
                  .Append(" prio=").Append(st.priority)
                  .Append(" gate=").Append(st.gate);

                if (st.gate == "PASS")
                {
                    sb.Append(" candidates=").Append(st.candidates)
                      .Append(" forbidden=").Append(st.forbidden)
                      .Append(" noHasJob=").Append(st.noHasJob)
                      .Append(" hasJob=").Append(st.hasJob)
                      .Append(" reachable=").Append(st.reachable)
                      .Append(" unreachable=").Append(st.unreachable)
                      .Append(" invalid=").Append(st.invalid)
                      .Append(" exceptions=").Append(st.exceptions);
                    if (st.truncated) sb.Append(" TRUNCATED");
                    if (!string.IsNullOrEmpty(st.exampleReachable))
                        sb.Append(" exampleReachable=").Append(st.exampleReachable);
                    if (!string.IsNullOrEmpty(st.exampleUnreachable))
                        sb.Append(" exampleUnreachable=").Append(st.exampleUnreachable);
                    if (!string.IsNullOrEmpty(st.firstException))
                        sb.Append(" firstException=").Append(st.firstException);
                }
                sb.AppendLine();
            }

            sb.AppendLine("--- Diagnostic synthesis ---");
            WorkNodeTrace normalTrace = IdleDiagManager.GetNormalTrace(pawn);
            bool recentWorkNode = normalTrace != null && tick - normalTrace.tick >= 0 && tick - normalTrace.tick <= 120;

            if (!recentWorkNode)
                sb.AppendLine("FINDING: Normal JobGiver_Work was not observed within the last 120 ticks. The ThinkTree may have selected a higher-priority branch before normal work was evaluated (timetable, Lord duty, needs, another modded ThinkNode, etc.).");
            else if (!normalTrace.resultValid)
                sb.AppendLine("FINDING: Normal JobGiver_Work was evaluated recently and returned NoJob.");
            else
                sb.AppendLine("FINDING: Normal JobGiver_Work recently returned a valid job (" + Safe(normalTrace.resultJob) + "); later interruption/override should be investigated.");

            if (reachableCandidates > 0 && recentWorkNode && !normalTrace.resultValid)
                sb.AppendLine("STRONG SIGNAL: Deep scan found " + reachableCandidates + " reachable HasJob candidates even though vanilla JobGiver_Work returned NoJob. Suspect a patched JobGiver_Work search path, JobOnThing/JobOnCell returning null, priority-group interaction, or state changing between the real scan and this snapshot.");

            if (unreachableCandidates > 0 && reachableCandidates == 0)
                sb.AppendLine("STRONG SIGNAL: WorkGivers report HasJob candidates, but none of the sampled candidates are reachable. Pathing/area/door/danger restrictions are prime suspects.");

            Area area = pawn.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
            if (area != null)
            {
                bool pawnInside = false;
                try { pawnInside = pawn.Position.InBounds(pawn.Map) && area[pawn.Position]; } catch { }
                if (!pawnInside)
                    sb.AppendLine("STRONG SIGNAL: Pawn current position is outside its effective allowed area or the area lookup failed.");
                if (area.TrueCount == 0)
                    sb.AppendLine("STRONG SIGNAL: Effective allowed area has zero allowed cells.");
            }

            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
                sb.AppendLine("STRONG SIGNAL: pawn.workSettings is null or EverWork=false; normal work priority is effectively zero.");

            if (exceptionCount > 0)
                sb.AppendLine("STRONG SIGNAL: " + exceptionCount + " WorkGiver diagnostic calls threw exceptions. See per-giver firstException fields; a broken WorkGiver/mod can make automatic work disappear.");

            sb.AppendLine("summary: passGivers=" + passGivers +
                          " blockedGivers=" + blockedGivers +
                          " totalCandidatesSampled=" + totalCandidates +
                          " reachableHasJob=" + reachableCandidates +
                          " unreachableHasJob=" + unreachableCandidates +
                          " exceptions=" + exceptionCount);
            sb.AppendLine("================================================================================");
            return sb.ToString();
        }

        private static void AppendPawnState(StringBuilder sb, Pawn pawn)
        {
            sb.AppendLine("--- Pawn state ---");
            sb.AppendLine("spawned=" + pawn.Spawned +
                          " drafted=" + pawn.Drafted +
                          " downed=" + pawn.Downed +
                          " dead=" + pawn.Dead +
                          " mentalState=" + (pawn.InMentalState ? pawn.MentalStateDef?.defName : "none") +
                          " burning=" + pawn.IsBurning());

            string assignment = pawn.timetable?.CurrentAssignment?.defName ?? "no timetable";
            sb.AppendLine("timetable=" + assignment +
                          " workSettings=" + (pawn.workSettings == null ? "null" : "present") +
                          " everWork=" + (pawn.workSettings?.EverWork.ToString() ?? "n/a"));

            Lord lord = null;
            try { lord = pawn.GetLord(); } catch { }
            sb.AppendLine("lord=" + (lord?.LordJob?.GetType().FullName ?? "none") +
                          " duty=" + (pawn.mindState?.duty?.def?.defName ?? "none") +
                          " mindIdle=" + (pawn.mindState?.IsIdle.ToString() ?? "n/a") +
                          " currentJob=" + (pawn.CurJob?.def?.defName ?? "none") +
                          " currentJobGiver=" + (pawn.CurJob?.jobGiver?.GetType().FullName ?? "none") +
                          " currentWorkGiver=" + (pawn.CurJob?.workGiverDef?.defName ?? "none"));
        }

        private static void AppendWorkNodeTrace(StringBuilder sb, Pawn pawn, int tick)
        {
            sb.AppendLine("--- Observed vanilla work-node trace ---");
            WorkNodeTrace normal = IdleDiagManager.GetNormalTrace(pawn);
            WorkNodeTrace emergency = IdleDiagManager.GetEmergencyTrace(pawn);
            sb.AppendLine("normal=" + FormatTrace(normal, tick));
            sb.AppendLine("emergency=" + FormatTrace(emergency, tick));
        }

        private static string FormatTrace(WorkNodeTrace trace, int now)
        {
            if (trace == null) return "not observed";
            return "tick=" + trace.tick +
                   " ageTicks=" + (now - trace.tick) +
                   " resultValid=" + trace.resultValid +
                   " resultJob=" + Safe(trace.resultJob) +
                   " exception=" + Safe(trace.exception);
        }

        private static void AppendAreaState(StringBuilder sb, Pawn pawn)
        {
            sb.AppendLine("--- Area / forbid / reachability state ---");
            try
            {
                Area raw = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                Area effective = pawn.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
                sb.AppendLine("rawArea=" + DescribeArea(raw, pawn) +
                              " effectiveArea=" + DescribeArea(effective, pawn));
                bool posForbidden = pawn.Position.IsForbidden(pawn);
                bool canReachSelf = pawn.CanReach(pawn.Position, PathEndMode.OnCell, Danger.Some);
                sb.AppendLine("positionForbidden=" + posForbidden + " canReachOwnCell=" + canReachSelf);
            }
            catch (Exception ex)
            {
                sb.AppendLine("AREA CHECK EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message);
            }
        }

        private static string DescribeArea(Area area, Pawn pawn)
        {
            if (area == null) return "Unrestricted";
            string mapText = "unknown";
            bool registered = false;
            bool posInside = false;
            try
            {
                mapText = area.Map?.uniqueID.ToString() ?? "null";
                registered = pawn.Map?.areaManager?.AllAreas?.Contains(area) == true;
                if (pawn.Map != null && pawn.Position.InBounds(pawn.Map) && area.Map == pawn.Map)
                    posInside = area[pawn.Position];
            }
            catch { }
            return area.Label + "#" + area.ID +
                   "(map=" + mapText +
                   ", trueCells=" + area.TrueCount +
                   ", registered=" + registered +
                   ", pawnInside=" + posInside + ")";
        }

        private static void AppendWorkPriorities(StringBuilder sb, Pawn pawn)
        {
            sb.AppendLine("--- Work priorities ---");
            if (pawn.workSettings == null)
            {
                sb.AppendLine("workSettings=null");
                return;
            }

            List<string> enabled = new List<string>();
            List<string> disabled = new List<string>();
            foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                try
                {
                    bool hardDisabled = pawn.WorkTypeIsDisabled(wt);
                    int prio = pawn.workSettings.GetPriority(wt);
                    string item = wt.defName + "=" + prio + (hardDisabled ? "(disabled)" : "");
                    if (!hardDisabled && prio > 0) enabled.Add(item);
                    else disabled.Add(item);
                }
                catch (Exception ex)
                {
                    disabled.Add(wt.defName + "=EX:" + ex.GetType().Name);
                }
            }
            sb.AppendLine("enabled=" + (enabled.Count == 0 ? "<none>" : string.Join(", ", enabled)));
            sb.AppendLine("disabledOrPriority0=" + (disabled.Count == 0 ? "<none>" : string.Join(", ", disabled)));
        }

        private static void AppendNeeds(StringBuilder sb, Pawn pawn)
        {
            sb.AppendLine("--- Needs / capacities ---");
            try
            {
                if (pawn.needs?.AllNeeds != null)
                {
                    List<string> needs = new List<string>();
                    foreach (Need need in pawn.needs.AllNeeds)
                        needs.Add(need.def.defName + "=" + need.CurLevelPercentage.ToString("0.000"));
                    sb.AppendLine("needs=" + string.Join(", ", needs));
                }
                else
                {
                    sb.AppendLine("needs=<none>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("needs=EX:" + ex.GetType().Name + ":" + ex.Message);
            }

            try
            {
                sb.AppendLine("Moving=" + pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving).ToString("0.000") +
                              " Manipulation=" + pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation).ToString("0.000") +
                              " Consciousness=" + pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness).ToString("0.000"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("capacities=EX:" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static List<ScanStats> DeepScanWorkGivers(Pawn pawn, ref int totalCandidates)
        {
            List<ScanStats> output = new List<ScanStats>();
            if (pawn.workSettings == null || pawn.Map == null)
                return output;

            List<WorkGiver> givers;
            try
            {
                givers = pawn.workSettings.WorkGiversInOrderNormal;
            }
            catch (Exception ex)
            {
                output.Add(new ScanStats
                {
                    giver = "<WorkGiversInOrderNormal>",
                    gate = "EX:" + ex.GetType().Name + ":" + ex.Message
                });
                return output;
            }

            IdleDiagSettings settings = IdleDiagMod.Settings ?? new IdleDiagSettings();
            int totalCap = Math.Max(100, settings.maxTotalCandidates);
            int perGiverCap = Math.Max(10, settings.maxCandidatesPerGiver);

            foreach (WorkGiver giver in givers)
            {
                if (giver == null || giver.def == null) continue;
                ScanStats st = new ScanStats
                {
                    giver = giver.def.defName + "[" + giver.GetType().FullName + "]",
                    workType = giver.def.workType?.defName,
                    priority = giver.def.workType != null ? pawn.workSettings.GetPriority(giver.def.workType) : -1,
                    gate = ExplainGate(pawn, giver)
                };
                output.Add(st);

                if (st.gate != "PASS") continue;
                WorkGiver_Scanner scanner = giver as WorkGiver_Scanner;
                if (scanner == null)
                {
                    st.gate = "PASS-NONSCAN(not re-invoked for side-effect safety)";
                    continue;
                }

                if (totalCandidates >= totalCap)
                {
                    st.truncated = true;
                    continue;
                }

                if (scanner.def.scanThings)
                    ScanThings(pawn, scanner, st, perGiverCap, totalCap, ref totalCandidates);

                if (scanner.def.scanCells && totalCandidates < totalCap)
                    ScanCells(pawn, scanner, st, perGiverCap, totalCap, ref totalCandidates);
            }

            return output;
        }

        private static string ExplainGate(Pawn pawn, WorkGiver giver)
        {
            try
            {
                if (!(giver.def.nonColonistsCanDo || pawn.IsColonist || pawn.IsColonyMech || pawn.IsMutant))
                    return "BLOCK:notColonist";
                if (pawn.WorkTagIsDisabled(giver.def.workTags))
                    return "BLOCK:workTagsDisabled(" + giver.def.workTags + ")";
                if (giver.def.workType != null && pawn.WorkTypeIsDisabled(giver.def.workType))
                    return "BLOCK:workTypeDisabled(" + giver.def.workType.defName + ")";
                if (giver.ShouldSkip(pawn))
                    return "BLOCK:ShouldSkip";
                PawnCapacityDef missing = giver.MissingRequiredCapacity(pawn);
                if (missing != null)
                    return "BLOCK:missingCapacity(" + missing.defName + ")";
                if (pawn.RaceProps.IsMechanoid && !giver.def.canBeDoneByMechs)
                    return "BLOCK:notForMechs";
                if (pawn.IsMutant && !giver.def.canBeDoneByMutants)
                    return "BLOCK:notForMutants";
                return "PASS";
            }
            catch (Exception ex)
            {
                return "GATE-EX:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        private static void ScanThings(Pawn pawn, WorkGiver_Scanner scanner, ScanStats st,
            int perGiverCap, int totalCap, ref int totalCandidates)
        {
            IEnumerable<Thing> source = null;
            try
            {
                source = scanner.PotentialWorkThingsGlobal(pawn);
                if (source == null)
                    source = pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
            }
            catch (Exception ex)
            {
                AddException(st, "PotentialWorkThingsGlobal", ex);
                return;
            }

            int local = 0;
            IEnumerator<Thing> e = null;
            try
            {
                e = source.GetEnumerator();
                while (local < perGiverCap && totalCandidates < totalCap && e.MoveNext())
                {
                    Thing t = e.Current;
                    local++;
                    totalCandidates++;
                    st.candidates++;

                    if (t == null || t.Destroyed || !t.Spawned || t.Map != pawn.Map)
                    {
                        st.invalid++;
                        continue;
                    }

                    bool forbidden;
                    try { forbidden = t.IsForbidden(pawn); }
                    catch (Exception ex)
                    {
                        AddException(st, "IsForbidden(" + t.ToStringSafe() + ")", ex);
                        continue;
                    }
                    if (forbidden)
                    {
                        st.forbidden++;
                        continue;
                    }

                    bool has;
                    try { has = scanner.HasJobOnThing(pawn, t); }
                    catch (Exception ex)
                    {
                        AddException(st, "HasJobOnThing(" + t.ToStringSafe() + ")", ex);
                        continue;
                    }
                    if (!has)
                    {
                        st.noHasJob++;
                        continue;
                    }

                    st.hasJob++;
                    bool reachable = true;
                    if (!scanner.AllowUnreachable)
                    {
                        try
                        {
                            reachable = pawn.CanReach(t, scanner.PathEndMode, scanner.MaxPathDanger(pawn));
                        }
                        catch (Exception ex)
                        {
                            AddException(st, "CanReach(" + t.ToStringSafe() + ")", ex);
                            reachable = false;
                        }
                    }

                    if (reachable)
                    {
                        st.reachable++;
                        if (st.exampleReachable == null)
                            st.exampleReachable = t.ToStringSafe() + "@" + t.Position;
                    }
                    else
                    {
                        st.unreachable++;
                        if (st.exampleUnreachable == null)
                            st.exampleUnreachable = t.ToStringSafe() + "@" + t.Position;
                    }
                }

                if (local >= perGiverCap || totalCandidates >= totalCap)
                    st.truncated = true;
            }
            catch (Exception ex)
            {
                AddException(st, "enumerateThings", ex);
            }
            finally
            {
                (e as IDisposable)?.Dispose();
            }
        }

        private static void ScanCells(Pawn pawn, WorkGiver_Scanner scanner, ScanStats st,
            int perGiverCap, int totalCap, ref int totalCandidates)
        {
            IEnumerable<IntVec3> source;
            try
            {
                source = scanner.PotentialWorkCellsGlobal(pawn);
                if (source == null) return;
            }
            catch (Exception ex)
            {
                AddException(st, "PotentialWorkCellsGlobal", ex);
                return;
            }

            int local = 0;
            IEnumerator<IntVec3> e = null;
            try
            {
                e = source.GetEnumerator();
                while (local < perGiverCap && totalCandidates < totalCap && e.MoveNext())
                {
                    IntVec3 c = e.Current;
                    local++;
                    totalCandidates++;
                    st.candidates++;

                    if (!c.IsValid || !c.InBounds(pawn.Map))
                    {
                        st.invalid++;
                        continue;
                    }

                    bool forbidden;
                    try { forbidden = c.IsForbidden(pawn); }
                    catch (Exception ex)
                    {
                        AddException(st, "Cell.IsForbidden(" + c + ")", ex);
                        continue;
                    }
                    if (forbidden)
                    {
                        st.forbidden++;
                        continue;
                    }

                    bool has;
                    try { has = scanner.HasJobOnCell(pawn, c); }
                    catch (Exception ex)
                    {
                        AddException(st, "HasJobOnCell(" + c + ")", ex);
                        continue;
                    }
                    if (!has)
                    {
                        st.noHasJob++;
                        continue;
                    }

                    st.hasJob++;
                    bool reachable = true;
                    if (!scanner.AllowUnreachable)
                    {
                        try
                        {
                            reachable = pawn.CanReach(c, scanner.PathEndMode, scanner.MaxPathDanger(pawn));
                        }
                        catch (Exception ex)
                        {
                            AddException(st, "CanReachCell(" + c + ")", ex);
                            reachable = false;
                        }
                    }

                    if (reachable)
                    {
                        st.reachable++;
                        if (st.exampleReachable == null) st.exampleReachable = "cell@" + c;
                    }
                    else
                    {
                        st.unreachable++;
                        if (st.exampleUnreachable == null) st.exampleUnreachable = "cell@" + c;
                    }
                }

                if (local >= perGiverCap || totalCandidates >= totalCap)
                    st.truncated = true;
            }
            catch (Exception ex)
            {
                AddException(st, "enumerateCells", ex);
            }
            finally
            {
                (e as IDisposable)?.Dispose();
            }
        }

        private static void AddException(ScanStats st, string where, Exception ex)
        {
            st.exceptions++;
            if (st.firstException == null)
                st.firstException = where + ":" + ex.GetType().Name + ":" + ex.Message;
        }

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "<none>" : s;
    }

    internal static class IdleDiagLog
    {
        internal static string LogPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "PawnIdleDiagnostics.log");

        internal static void InitializeSession()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024)
                {
                    string old = LogPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogPath, old);
                }
                WriteLine("");
                WriteLine("################################################################################");
                WriteLine("Pawn Idle Diagnostics 1.5 session " + DateTime.Now.ToString("s"));
                WriteLine("################################################################################");
            }
            catch (Exception ex)
            {
                Log.Warning("[IdleDiag] Could not initialize separate log file: " + ex.Message);
            }
        }

        internal static void WriteLine(string text)
        {
            try
            {
                File.AppendAllText(LogPath, text + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Warning("[IdleDiag] Could not write diagnostic file: " + ex.Message);
            }
        }

        internal static void WritePatchInventory()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- Harmony patch inventory on critical AI methods ---");
            AppendPatchOwners(sb, AccessTools.Method(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage)), "JobGiver_Work.TryIssueJobPackage");
            AppendPatchOwners(sb, AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)), "Pawn_JobTracker.StartJob");
            AppendPatchOwners(sb, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), "Pawn_JobTracker.DetermineNextJob");
            AppendPatchOwners(sb, AccessTools.Method(typeof(ThinkNode_PrioritySorter), nameof(ThinkNode_PrioritySorter.TryIssueJobPackage)), "ThinkNode_PrioritySorter.TryIssueJobPackage");
            AppendPatchOwners(sb, AccessTools.PropertyGetter(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap)), "AreaRestrictionInPawnCurrentMap.get");
            AppendPatchOwners(sb, AccessTools.PropertySetter(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap)), "AreaRestrictionInPawnCurrentMap.set");
            WriteLine(sb.ToString());
        }

        private static void AppendPatchOwners(StringBuilder sb, MethodBase method, string label)
        {
            if (method == null)
            {
                sb.AppendLine(label + ": METHOD-NOT-FOUND");
                return;
            }

            Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                sb.AppendLine(label + ": none");
                return;
            }

            List<string> owners = new List<string>();
            owners.AddRange(info.Prefixes.Select(p => "pre:" + p.owner));
            owners.AddRange(info.Postfixes.Select(p => "post:" + p.owner));
            owners.AddRange(info.Transpilers.Select(p => "trans:" + p.owner));
            owners.AddRange(info.Finalizers.Select(p => "final:" + p.owner));
            sb.AppendLine(label + ": " + (owners.Count == 0 ? "none" : string.Join(", ", owners.Distinct())));
        }
    }

    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    internal static class Patch_JobGiver_Work_Trace
    {
        private static void Prefix(JobGiver_Work __instance, Pawn pawn)
        {
            IdleDiagManager.RecordWorkNodeBegin(pawn, __instance.emergency);
        }

        private static void Postfix(JobGiver_Work __instance, Pawn pawn, ThinkResult __result)
        {
            IdleDiagManager.RecordWorkNodeEnd(pawn, __instance.emergency, __result);
        }

        private static Exception Finalizer(JobGiver_Work __instance, Pawn pawn, Exception __exception)
        {
            if (__exception != null)
                IdleDiagManager.RecordWorkNodeException(pawn, __instance.emergency, __exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_PawnJobTracker_StartJob_IdleTrigger
    {
        private static void Prefix(
            Pawn_JobTracker __instance,
            Job newJob,
            JobCondition lastJobEndCondition,
            ThinkNode jobGiver,
            ThinkTreeDef thinkTree,
            Pawn ___pawn)
        {
            if (!IdleDiagManager.Eligible(___pawn) || newJob?.def == null)
                return;

            string defName = newJob.def.defName ?? "";
            bool looksIdle = newJob.def.isIdle ||
                             defName.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             defName.Equals("Wait", StringComparison.OrdinalIgnoreCase) ||
                             defName.StartsWith("Wait_", StringComparison.OrdinalIgnoreCase);

            if (!looksIdle)
                return;

            IdleDiagManager.Schedule(
                ___pawn,
                false,
                "StartJob:" + defName,
                __instance.curJob?.def?.defName,
                defName,
                jobGiver?.GetType().FullName,
                thinkTree?.defName,
                lastJobEndCondition.ToString());
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_Pawn_GetGizmos_IdleDiag
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo gizmo in __result)
                yield return gizmo;

            IdleDiagSettings settings = IdleDiagMod.Settings ?? new IdleDiagSettings();
            if (!settings.addPawnGizmo || !IdleDiagManager.Eligible(__instance))
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "Diagnose work/idle now",
                defaultDesc = "Write a read-only diagnostic report explaining why this pawn may be idle or wandering. Full report goes to PawnIdleDiagnostics.log.",
                action = delegate
                {
                    IdleDiagManager.Schedule(
                        __instance,
                        true,
                        "manual gizmo",
                        __instance.CurJob?.def?.defName,
                        __instance.CurJob?.def?.defName,
                        __instance.CurJob?.jobGiver?.GetType().FullName,
                        __instance.CurJob?.jobGiverThinkTree?.defName,
                        "manual");
                }
            };
        }
    }
}
