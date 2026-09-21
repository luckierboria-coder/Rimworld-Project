using System;
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

namespace PawnIdleDiagnostics15
{
    public sealed class IdleDiagSettings : ModSettings
    {
        public bool autoArmOnRealWander = true;
        public bool addPawnGizmo = true;
        public bool mirrorSummaryToPlayerLog = true;
        public int cooldownTicks = 2500;
        public int maxTraceLines = 260;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref autoArmOnRealWander, "autoArmOnRealWander", true);
            Scribe_Values.Look(ref addPawnGizmo, "addPawnGizmo", true);
            Scribe_Values.Look(ref mirrorSummaryToPlayerLog, "mirrorSummaryToPlayerLog", true);
            Scribe_Values.Look(ref cooldownTicks, "cooldownTicks", 2500);
            Scribe_Values.Look(ref maxTraceLines, "maxTraceLines", 260);
        }
    }

    public sealed class IdleDiagMod : Mod
    {
        internal static IdleDiagSettings Settings;

        public IdleDiagMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<IdleDiagSettings>();
        }

        public override string SettingsCategory() => "Pawn Idle Diagnostics 1.5 v1.1";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled(
                "Auto-arm exact trace when a colonist enters Wait_Wander",
                ref Settings.autoArmOnRealWander,
                "Only real colony wandering arms a trace. Wait_MaintainPosture and transient waits are ignored.");
            listing.CheckboxLabeled(
                "Add 'Arm next work-chain trace' gizmo",
                ref Settings.addPawnGizmo,
                "The next normal JobGiver_Work call for this pawn will be traced.");
            listing.CheckboxLabeled(
                "Mirror one-line trace result to Player.log",
                ref Settings.mirrorSummaryToPlayerLog,
                "Full traces are written to PawnIdleWorkTrace.log.");

            listing.GapLine();
            listing.Label("Automatic per-pawn cooldown: " + Settings.cooldownTicks + " ticks");
            Settings.cooldownTicks = (int)listing.Slider(Settings.cooldownTicks, 250f, 15000f);
            listing.Label("Maximum detail lines per exact trace: " + Settings.maxTraceLines);
            Settings.maxTraceLines = (int)listing.Slider(Settings.maxTraceLines, 80f, 1000f);
            listing.Gap();
            listing.Label("Output: " + WorkTraceLog.LogPath);
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
            WorkTraceLog.InitializeSession();
            WorkTraceLog.WritePatchInventory();
            Log.Message("[IdleDiag v1.1] Exact work-chain tracer loaded. It does not issue jobs or change AI results. Output: " + WorkTraceLog.LogPath);
        }
    }

    internal sealed class WorkTraceState
    {
        public Pawn pawn;
        public int tick;
        public string trigger;
        public string entryJob;
        public string beforeNearby;
        public string afterNearby;
        public Exception exception;
        public readonly List<string> lines = new List<string>();
        public bool truncated;

        public void Add(string line)
        {
            int cap = Math.Max(80, IdleDiagMod.Settings?.maxTraceLines ?? 260);
            if (lines.Count < cap)
            {
                lines.Add(line);
            }
            else if (!truncated)
            {
                truncated = true;
                lines.Add("... TRACE TRUNCATED ...");
            }
        }
    }

    internal static class WorkTraceManager
    {
        private static readonly Dictionary<int, string> armed = new Dictionary<int, string>();
        private static readonly Dictionary<int, int> lastAutoArmTick = new Dictionary<int, int>();
        private static readonly Dictionary<int, WorkTraceState> active = new Dictionary<int, WorkTraceState>();

        internal static bool Eligible(Pawn pawn)
        {
            return pawn != null &&
                   !pawn.Destroyed &&
                   pawn.Spawned &&
                   pawn.RaceProps != null &&
                   pawn.RaceProps.Humanlike &&
                   pawn.Faction == Faction.OfPlayer;
        }

        internal static void Arm(Pawn pawn, string trigger, bool manual)
        {
            if (!Eligible(pawn)) return;

            int tick = Find.TickManager?.TicksGame ?? 0;
            if (!manual)
            {
                IdleDiagSettings settings = IdleDiagMod.Settings ?? new IdleDiagSettings();
                if (!settings.autoArmOnRealWander) return;
                if (lastAutoArmTick.TryGetValue(pawn.thingIDNumber, out int last) &&
                    tick - last < settings.cooldownTicks)
                    return;
                lastAutoArmTick[pawn.thingIDNumber] = tick;
            }

            armed[pawn.thingIDNumber] = trigger;
            if (manual)
                Messages.Message("Exact work-chain trace armed for " + pawn.LabelShortCap + ".", MessageTypeDefOf.NeutralEvent, false);
        }

        internal static WorkTraceState Begin(JobGiver_Work giver, Pawn pawn)
        {
            if (giver == null || giver.emergency || !Eligible(pawn))
                return null;

            if (!armed.TryGetValue(pawn.thingIDNumber, out string trigger))
                return null;

            armed.Remove(pawn.thingIDNumber);

            WorkTraceState state = new WorkTraceState
            {
                pawn = pawn,
                tick = Find.TickManager?.TicksGame ?? -1,
                trigger = trigger,
                entryJob = pawn.CurJob?.def?.defName
            };
            active[pawn.thingIDNumber] = state;

            state.Add("TRACE-BEGIN pawn=" + pawn.LabelShortCap +
                      " tick=" + state.tick +
                      " trigger=" + trigger +
                      " pos=" + pawn.Position +
                      " timetable=" + (pawn.timetable?.CurrentAssignment?.defName ?? "<none>") +
                      " entryJob=" + (state.entryJob ?? "<none>"));
            state.Add("area=" + DescribeArea(pawn.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap, pawn) +
                      " forbiddenHere=" + SafeBool(() => pawn.Position.IsForbidden(pawn)) +
                      " mindIdle=" + (pawn.mindState?.IsIdle.ToString() ?? "<none>"));
            return state;
        }

        internal static WorkTraceState Get(Pawn pawn)
        {
            if (pawn == null) return null;
            active.TryGetValue(pawn.thingIDNumber, out WorkTraceState state);
            return state;
        }

        internal static void CaptureStage(Pawn pawn, string stage, ThinkResult result)
        {
            WorkTraceState state = Get(pawn);
            if (state == null) return;

            string text = DescribeResult(result);
            if (stage == "before-nearby") state.beforeNearby = text;
            if (stage == "after-nearby") state.afterNearby = text;
            state.Add("RESULT-STAGE " + stage + " => " + text);
        }

        internal static void CaptureException(Pawn pawn, Exception ex)
        {
            WorkTraceState state = Get(pawn);
            if (state == null || ex == null) return;
            state.exception = ex;
            state.Add("ACTUAL-EXCEPTION " + ex.GetType().FullName + ": " + ex.Message);
        }

        internal static void Finish(JobGiver_Work giver, Pawn pawn, ThinkResult finalResult)
        {
            WorkTraceState state = Get(pawn);
            if (state == null) return;

            try
            {
                string finalText = DescribeResult(finalResult);
                state.Add("ACTUAL-FINAL => " + finalText);

                if (!finalResult.IsValid)
                {
                    state.Add("--- MIRROR OF VANILLA 1.5 WORK SELECTION (same tick, diagnostic rerun) ---");
                    Rand.PushState();
                    try
                    {
                        MirrorVanillaWorkSelection.Run(giver, pawn, state);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                }
                else
                {
                    state.Add("Mirror skipped because actual final result is already a valid work job.");
                }

                WriteState(state, finalResult);
            }
            catch (Exception ex)
            {
                state.Add("TRACE-FINISH-EXCEPTION " + ex.GetType().FullName + ": " + ex.Message);
                WriteState(state, finalResult);
            }
            finally
            {
                active.Remove(pawn.thingIDNumber);
            }
        }

        private static void WriteState(WorkTraceState state, ThinkResult finalResult)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("EXACT WORK-CHAIN TRACE");
            sb.AppendLine("timestamp=" + DateTime.Now.ToString("s") +
                          " tick=" + state.tick +
                          " pawn=" + state.pawn.LabelShortCap +
                          " thingID=" + state.pawn.thingIDNumber);
            sb.AppendLine("trigger=" + state.trigger);
            sb.AppendLine("beforeNearby=" + (state.beforeNearby ?? "<not captured>"));
            sb.AppendLine("afterNearby=" + (state.afterNearby ?? "<not captured>"));
            if (state.exception != null)
                sb.AppendLine("actualException=" + state.exception.GetType().FullName + ": " + state.exception.Message);
            foreach (string line in state.lines)
                sb.AppendLine(line);
            sb.AppendLine("================================================================================");
            WorkTraceLog.WriteLine(sb.ToString());

            if (IdleDiagMod.Settings?.mirrorSummaryToPlayerLog ?? true)
            {
                Log.Message("[IdleDiag v1.1] exact trace for " + state.pawn.LabelShortCap +
                            ": final=" + DescribeResult(finalResult) +
                            ", beforeNearby=" + (state.beforeNearby ?? "<not captured>") +
                            ", file=" + WorkTraceLog.LogPath);
            }
        }

        internal static string DescribeResult(ThinkResult result)
        {
            if (!result.IsValid) return "NoJob";
            Job job = result.Job;
            return "Job=" + (job?.def?.defName ?? "<null>") +
                   " workGiver=" + (job?.workGiverDef?.defName ?? "<none>");
        }

        private static string DescribeArea(Area area, Pawn pawn)
        {
            if (area == null) return "Unrestricted";
            try
            {
                return area.Label + "#" + area.ID +
                       " map=" + (area.Map?.uniqueID.ToString() ?? "null") +
                       " trueCells=" + area.TrueCount +
                       " pawnInside=" + (pawn.Map == area.Map && pawn.Position.InBounds(pawn.Map) && area[pawn.Position]);
            }
            catch (Exception ex)
            {
                return "AREA-EX:" + ex.GetType().Name;
            }
        }

        private static string SafeBool(Func<bool> fn)
        {
            try { return fn().ToString(); }
            catch (Exception ex) { return "EX:" + ex.GetType().Name; }
        }
    }

    internal static class MirrorVanillaWorkSelection
    {
        private static readonly MethodInfo PawnCanUseWorkGiverMethod =
            AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver");

        internal static void Run(JobGiver_Work instance, Pawn pawn, WorkTraceState trace)
        {
            if (pawn == null || pawn.workSettings == null || pawn.Map == null)
            {
                trace.Add("MIRROR abort: pawn/workSettings/map missing.");
                return;
            }

            List<WorkGiver> list;
            try
            {
                list = pawn.workSettings.WorkGiversInOrderNormal;
            }
            catch (Exception ex)
            {
                trace.Add("MIRROR WorkGiversInOrderNormal EX: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            int previousPriorityInType = -999;
            TargetInfo bestTarget = TargetInfo.Invalid;
            WorkGiver_Scanner scannerWhoProvidedTarget = null;
            int passedGivers = 0;
            int skippedGivers = 0;

            for (int j = 0; j < list.Count; j++)
            {
                WorkGiver workGiver = list[j];
                if (workGiver == null || workGiver.def == null)
                    continue;

                if (workGiver.def.priorityInType != previousPriorityInType && bestTarget.IsValid)
                {
                    trace.Add("!!! POISONED-PRIORITY BREAK at index=" + j +
                              " nextGiver=" + workGiver.def.defName +
                              " nextPriorityInType=" + workGiver.def.priorityInType +
                              " previousPriorityInType=" + previousPriorityInType +
                              " staleTarget=" + TargetText(bestTarget) +
                              " staleProvider=" + GiverText(scannerWhoProvidedTarget) +
                              ". Lower work givers are NOT scanned by vanilla after this break.");
                    trace.Add("MIRROR-FINAL => NoJob due to retained target after JobOnX returned null.");
                    return;
                }

                bool canUse;
                try
                {
                    canUse = PawnCanUse(instance, pawn, workGiver);
                }
                catch (Exception ex)
                {
                    trace.Add("GATE-EX giver=" + GiverText(workGiver) + " => " + ex.GetType().Name + ": " + ex.Message);
                    skippedGivers++;
                    continue;
                }

                if (!canUse)
                {
                    skippedGivers++;
                    continue;
                }

                passedGivers++;
                trace.Add("GIVER index=" + j +
                          " def=" + workGiver.def.defName +
                          " type=" + workGiver.GetType().FullName +
                          " asm=" + workGiver.GetType().Assembly.GetName().Name +
                          " workType=" + (workGiver.def.workType?.defName ?? "<none>") +
                          " pawnPrio=" + (workGiver.def.workType != null ? pawn.workSettings.GetPriority(workGiver.def.workType).ToString() : "<none>") +
                          " priorityInType=" + workGiver.def.priorityInType);

                try
                {
                    Job nonScan = workGiver.NonScanJob(pawn);
                    if (nonScan != null)
                    {
                        trace.Add("  NONSCAN => " + nonScan.def?.defName + " ; MIRROR would return valid job here.");
                        trace.Add("MIRROR-FINAL => Job=" + nonScan.def?.defName + " provider=" + workGiver.def.defName);
                        return;
                    }

                    WorkGiver_Scanner scanner = workGiver as WorkGiver_Scanner;
                    if (scanner != null)
                    {
                        ScanOneGiverLikeVanilla(pawn, scanner, ref bestTarget, ref scannerWhoProvidedTarget, trace);
                    }
                }
                catch (Exception ex)
                {
                    trace.Add("  SCAN-EX " + ex.GetType().Name + ": " + ex.Message);
                }

                if (bestTarget.IsValid)
                {
                    Job job = null;
                    try
                    {
                        job = !bestTarget.HasThing
                            ? scannerWhoProvidedTarget.JobOnCell(pawn, bestTarget.Cell)
                            : scannerWhoProvidedTarget.JobOnThing(pawn, bestTarget.Thing);
                    }
                    catch (Exception ex)
                    {
                        trace.Add("  JOB-ON-X EX provider=" + GiverText(scannerWhoProvidedTarget) +
                                  " target=" + TargetText(bestTarget) +
                                  " => " + ex.GetType().Name + ": " + ex.Message);
                    }

                    if (job != null)
                    {
                        trace.Add("  JOB-ON-X => Job=" + job.def?.defName +
                                  " provider=" + GiverText(scannerWhoProvidedTarget) +
                                  " target=" + TargetText(bestTarget) +
                                  " ; MIRROR would return valid job here.");
                        trace.Add("MIRROR-FINAL => Job=" + job.def?.defName +
                                  " provider=" + scannerWhoProvidedTarget.def?.defName);
                        return;
                    }

                    trace.Add("  !!! JOB-ON-X RETURNED NULL provider=" + GiverText(scannerWhoProvidedTarget) +
                              " target=" + TargetText(bestTarget) +
                              ". bestTarget remains valid, so a priorityInType change on the next usable loop iteration can terminate the entire work scan.");
                }

                previousPriorityInType = workGiver.def.priorityInType;
            }

            trace.Add("MIRROR-FINAL => NoJob after full loop. passedGivers=" + passedGivers +
                      " skippedGivers=" + skippedGivers +
                      " retainedTarget=" + (bestTarget.IsValid ? TargetText(bestTarget) : "<none>") +
                      " retainedProvider=" + GiverText(scannerWhoProvidedTarget));
        }

        private static bool PawnCanUse(JobGiver_Work instance, Pawn pawn, WorkGiver giver)
        {
            if (PawnCanUseWorkGiverMethod == null)
                return FallbackPawnCanUse(pawn, giver);

            try
            {
                return (bool)PawnCanUseWorkGiverMethod.Invoke(instance, new object[] { pawn, giver });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }

        private static bool FallbackPawnCanUse(Pawn pawn, WorkGiver giver)
        {
            if (!giver.def.nonColonistsCanDo && !pawn.IsColonist && !pawn.IsColonyMech)
                return false;
            if (pawn.WorkTagIsDisabled(giver.def.workTags))
                return false;
            if (giver.def.workType != null && pawn.WorkTypeIsDisabled(giver.def.workType))
                return false;
            if (giver.ShouldSkip(pawn))
                return false;
            if (giver.MissingRequiredCapacity(pawn) != null)
                return false;
            if (pawn.RaceProps.IsMechanoid && !giver.def.canBeDoneByMechs)
                return false;
            return true;
        }

        private static void ScanOneGiverLikeVanilla(
            Pawn pawn,
            WorkGiver_Scanner scanner,
            ref TargetInfo bestTarget,
            ref WorkGiver_Scanner scannerWhoProvidedTarget,
            WorkTraceState trace)
        {
            if (scanner.def.scanThings)
            {
                IEnumerable<Thing> enumerable = scanner.PotentialWorkThingsGlobal(pawn);
                bool carriedCandidate = pawn.carryTracker?.CarriedThing != null &&
                                        scanner.PotentialWorkThingRequest.Accepts(pawn.carryTracker.CarriedThing) &&
                                        Validator(scanner, pawn, pawn.carryTracker.CarriedThing);

                Thing thing;
                if (scanner.Prioritized)
                {
                    IEnumerable<Thing> searchSet = enumerable ?? pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
                    thing = !scanner.AllowUnreachable
                        ? GenClosest.ClosestThing_Global_Reachable(
                            pawn.Position,
                            pawn.Map,
                            searchSet,
                            scanner.PathEndMode,
                            TraverseParms.For(pawn, scanner.MaxPathDanger(pawn)),
                            9999f,
                            t => Validator(scanner, pawn, t),
                            x => scanner.GetPriority(pawn, x))
                        : GenClosest.ClosestThing_Global(
                            pawn.Position,
                            searchSet,
                            99999f,
                            t => Validator(scanner, pawn, t),
                            x => scanner.GetPriority(pawn, x));

                    if (carriedCandidate)
                    {
                        if (thing != null)
                        {
                            float carriedPriority = scanner.GetPriority(pawn, pawn.carryTracker.CarriedThing);
                            float foundPriority = scanner.GetPriority(pawn, thing);
                            if (carriedPriority >= foundPriority)
                                thing = pawn.carryTracker.CarriedThing;
                        }
                        else
                        {
                            thing = pawn.carryTracker.CarriedThing;
                        }
                    }
                }
                else if (carriedCandidate)
                {
                    thing = pawn.carryTracker.CarriedThing;
                }
                else if (scanner.AllowUnreachable)
                {
                    IEnumerable<Thing> searchSet = enumerable ?? pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
                    thing = GenClosest.ClosestThing_Global(
                        pawn.Position,
                        searchSet,
                        99999f,
                        t => Validator(scanner, pawn, t));
                }
                else
                {
                    thing = GenClosest.ClosestThingReachable(
                        pawn.Position,
                        pawn.Map,
                        scanner.PotentialWorkThingRequest,
                        scanner.PathEndMode,
                        TraverseParms.For(pawn, scanner.MaxPathDanger(pawn)),
                        9999f,
                        t => Validator(scanner, pawn, t),
                        enumerable,
                        0,
                        scanner.MaxRegionsToScanBeforeGlobalSearch,
                        enumerable != null);
                }

                if (thing != null)
                {
                    bestTarget = thing;
                    scannerWhoProvidedTarget = scanner;
                    trace.Add("  TARGET-THING => " + TargetText(bestTarget));
                }
            }

            if (scanner.def.scanCells)
            {
                IntVec3 pawnPosition = pawn.Position;
                float closestDistSquared = 99999f;
                float bestPriority = float.MinValue;
                bool prioritized = scanner.Prioritized;
                bool allowUnreachable = scanner.AllowUnreachable;
                Danger maxPathDanger = scanner.MaxPathDanger(pawn);
                IEnumerable<IntVec3> cells = scanner.PotentialWorkCellsGlobal(pawn);

                foreach (IntVec3 c in cells)
                {
                    bool choose = false;
                    float distSquared = (c - pawnPosition).LengthHorizontalSquared;
                    float priority = 0f;

                    if (prioritized)
                    {
                        if (!c.IsForbidden(pawn) && scanner.HasJobOnCell(pawn, c))
                        {
                            if (!allowUnreachable && !pawn.CanReach(c, scanner.PathEndMode, maxPathDanger))
                                continue;
                            priority = scanner.GetPriority(pawn, c);
                            if (priority > bestPriority || (priority == bestPriority && distSquared < closestDistSquared))
                                choose = true;
                        }
                    }
                    else if (distSquared < closestDistSquared &&
                             !c.IsForbidden(pawn) &&
                             scanner.HasJobOnCell(pawn, c))
                    {
                        if (!allowUnreachable && !pawn.CanReach(c, scanner.PathEndMode, maxPathDanger))
                            continue;
                        choose = true;
                    }

                    if (choose)
                    {
                        bestTarget = new TargetInfo(c, pawn.Map);
                        scannerWhoProvidedTarget = scanner;
                        closestDistSquared = distSquared;
                        bestPriority = priority;
                    }
                }

                if (bestTarget.IsValid && scannerWhoProvidedTarget == scanner)
                    trace.Add("  TARGET-CELL => " + TargetText(bestTarget));
            }
        }

        private static bool Validator(WorkGiver_Scanner scanner, Pawn pawn, Thing thing)
        {
            return thing != null && !thing.IsForbidden(pawn) && scanner.HasJobOnThing(pawn, thing);
        }

        private static string GiverText(WorkGiver giver)
        {
            if (giver == null) return "<none>";
            return (giver.def?.defName ?? "<no-def>") +
                   "[" + giver.GetType().FullName + "|" + giver.GetType().Assembly.GetName().Name + "]";
        }

        private static string TargetText(TargetInfo target)
        {
            if (!target.IsValid) return "<invalid>";
            if (target.HasThing)
            {
                Thing t = target.Thing;
                return (t?.ToStringSafe() ?? "<null-thing>") +
                       "@(" + (t?.Position.ToString() ?? "?") + ")";
            }
            return "cell@" + target.Cell;
        }
    }

    internal static class WorkTraceLog
    {
        internal static string LogPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "PawnIdleWorkTrace.log");

        internal static void InitializeSession()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 4 * 1024 * 1024)
                {
                    string old = LogPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogPath, old);
                }

                WriteLine("");
                WriteLine("################################################################################");
                WriteLine("Pawn Idle Diagnostics 1.5 v1.1 exact work-chain session " + DateTime.Now.ToString("s"));
                WriteLine("################################################################################");
            }
            catch (Exception ex)
            {
                Log.Warning("[IdleDiag v1.1] Could not initialize trace file: " + ex.Message);
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
                Log.Warning("[IdleDiag v1.1] Could not write trace file: " + ex.Message);
            }
        }

        internal static void WritePatchInventory()
        {
            MethodBase target = AccessTools.Method(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- Harmony patch inventory: JobGiver_Work.TryIssueJobPackage ---");
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null)
            {
                sb.AppendLine("none");
            }
            else
            {
                AppendPatches(sb, "PREFIX", info.Prefixes);
                AppendPatches(sb, "POSTFIX", info.Postfixes);
                AppendPatches(sb, "TRANSPILER", info.Transpilers);
                AppendPatches(sb, "FINALIZER", info.Finalizers);
            }
            WriteLine(sb.ToString());
        }

        private static void AppendPatches(StringBuilder sb, string kind, IEnumerable<Patch> patches)
        {
            foreach (Patch p in patches)
            {
                sb.AppendLine(kind +
                              " owner=" + p.owner +
                              " priority=" + p.priority +
                              " index=" + p.index +
                              " method=" + p.PatchMethod?.DeclaringType?.FullName + "." + p.PatchMethod?.Name +
                              " before=[" + string.Join(",", p.before ?? Array.Empty<string>()) + "]" +
                              " after=[" + string.Join(",", p.after ?? Array.Empty<string>()) + "]");
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_StartJob_ArmRealWander
    {
        private static void Prefix(Job newJob, ThinkNode jobGiver, Pawn ___pawn)
        {
            if (!WorkTraceManager.Eligible(___pawn) || newJob?.def == null)
                return;

            string defName = newJob.def.defName ?? "";
            string giverName = jobGiver?.GetType().FullName ?? "";
            bool realWander =
                defName.Equals("Wait_Wander", StringComparison.OrdinalIgnoreCase) ||
                giverName.IndexOf("JobGiver_Wander", StringComparison.OrdinalIgnoreCase) >= 0;

            if (realWander)
                WorkTraceManager.Arm(___pawn, "entered " + defName + " via " + giverName, false);
        }
    }

    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    internal static class Patch_WorkTrace_Begin
    {
        private static void Prefix(JobGiver_Work __instance, Pawn pawn, ref WorkTraceState __state)
        {
            __state = WorkTraceManager.Begin(__instance, pawn);
        }

        private static Exception Finalizer(Pawn pawn, Exception __exception)
        {
            if (__exception != null)
                WorkTraceManager.CaptureException(pawn, __exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    [HarmonyBefore("PureMJ.MjRimMods.WhileYouAreNearby")]
    [HarmonyPriority(Priority.First)]
    internal static class Patch_WorkTrace_BeforeNearby
    {
        private static void Postfix(Pawn pawn, ThinkResult __result)
        {
            WorkTraceManager.CaptureStage(pawn, "before-nearby", __result);
        }
    }

    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    [HarmonyAfter("PureMJ.MjRimMods.WhileYouAreNearby")]
    [HarmonyPriority(Priority.Last)]
    internal static class Patch_WorkTrace_AfterNearby
    {
        private static void Postfix(JobGiver_Work __instance, Pawn pawn, ThinkResult __result)
        {
            WorkTraceManager.CaptureStage(pawn, "after-nearby", __result);
            WorkTraceManager.Finish(__instance, pawn, __result);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_Pawn_GetGizmos_WorkTrace
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo gizmo in __result)
                yield return gizmo;

            if (!(IdleDiagMod.Settings?.addPawnGizmo ?? true) || !WorkTraceManager.Eligible(__instance))
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "Arm next work-chain trace",
                defaultDesc = "Trace the next normal JobGiver_Work pass for this pawn, including the exact vanilla priority-group behavior and result stages around While You Are Nearby.",
                action = delegate
                {
                    WorkTraceManager.Arm(__instance, "manual gizmo", true);
                }
            };
        }
    }
}
