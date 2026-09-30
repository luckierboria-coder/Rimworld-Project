$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T14 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T14 PlayerHuman Residual Attribution
# Clean diagnostic child of T13/T8. It keeps all T8 production behavior unchanged.
# T14 instruments only direct void Tick-like calls in Pawn.Tick and only performs Stopwatch work
# on PlayerHumanlike pawns at the existing T2 periodic sample boundary further thinned to 1/256 ticks.
# No tracker method receives a separate Harmony patch. No Pawn behavior/state/ordering is changed.

$diagPath = 'RimMT/Source/RimMT/Diagnostics/PlayerHumanResidualAttribution093T14.cs'
$diag = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    internal enum PlayerHumanResidualStage093T14
    {
        BaseComps = 0,
        Health = 1,
        NeedsMind = 2,
        Stance = 3,
        GearInventory = 4,
        AbilityGene = 5,
        Social = 6,
        OtherTracker = 7,
        Count = 8
    }

    /// <summary>
    /// V0.9.3-T14: bounded PlayerHumanlike residual attribution.
    /// Uses the same T2 Pawn.Tick start timestamp, and only activates one quarter of T2's
    /// 1/64 periodic samples (game tick % 256 == 0). The transpiler only inserts guarded
    /// probes around direct tracker Tick calls inside Pawn.Tick; target methods themselves
    /// are never Harmony-patched by T14.
    /// </summary>
    internal static class PlayerHumanResidualAttribution093T14
    {
        internal const int StageCount = (int)PlayerHumanResidualStage093T14.Count;
        private const int RecentCapacity = 24;
        private const int SampleMask = 255;

        private static readonly string[] StageNames =
        {
            "BaseComps", "Health", "NeedsMind", "Stance", "GearInventory", "AbilityGene", "Social", "OtherTracker"
        };

        private static readonly long[] StageCalls = new long[StageCount];
        private static readonly long[] StageTotalUs = new long[StageCount];
        private static readonly long[] StageMaxUs = new long[StageCount];
        private static readonly long[] StageOver1 = new long[StageCount];
        private static readonly long[] StageOver5 = new long[StageCount];
        private static readonly int[] ProbeSites = new int[StageCount];
        private static readonly string[] ProbeMethods = new string[StageCount];
        private static readonly RecentEntry[] Recent = new RecentEntry[RecentCapacity];

        [ThreadStatic] internal static bool Active;
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static long pawnStartTicks;
        [ThreadStatic] private static long currentOriginalUs;
        [ThreadStatic] private static long currentJobUs;
        [ThreadStatic] private static long currentPatherUs;
        [ThreadStatic] private static long[] stageStartTicks;
        [ThreadStatic] private static long[] currentStageUs;

        private static long sampledPawns;
        private static long totalPawnUs;
        private static long totalOriginalUs;
        private static long totalJobUs;
        private static long totalPatherUs;
        private static long totalOriginalResidualUs;
        private static long totalTrackedResidualUs;
        private static long totalUntrackedOriginalResidualUs;
        private static long totalPostfixBeforeT2Us;
        private static long maxOriginalResidualUs;
        private static long maxUntrackedUs;
        private static long maxPostfixUs;
        private static long residualOver1;
        private static long residualOver5;
        private static long residualOver10;
        private static long residualOver20;
        private static long postfixOver1;
        private static long postfixOver5;
        private static long missingOriginalEnd;
        private static long residualClamp;
        private static long untrackedClamp;
        private static long postfixClamp;
        private static int recentPos;
        private static int recentCount;
        private static int retSites;
        private static int skippedEhSites;
        private static bool transpilerSuppressed;
        private static string transpilerReason = string.Empty;

        internal static void BeginPawn(Pawn pawn, bool deepActive, long t2StartTicks)
        {
            ClearCurrent();
            if (!deepActive || t2StartTicks == 0L || !RimMTThreadGuard.IsMainThread || pawn == null)
                return;

            int gameTick = 0;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { return; }
            if ((gameTick & SampleMask) != 0) return;

            try
            {
                if (pawn.def == null || pawn.def.race == null || !pawn.def.race.Humanlike) return;
                if (pawn.Faction == null || !pawn.Faction.IsPlayer) return;
            }
            catch { return; }

            EnsureThreadArrays();
            Array.Clear(stageStartTicks, 0, stageStartTicks.Length);
            Array.Clear(currentStageUs, 0, currentStageUs.Length);
            currentPawn = pawn;
            pawnStartTicks = t2StartTicks;
            currentOriginalUs = 0L;
            currentJobUs = 0L;
            currentPatherUs = 0L;
            Active = true;
        }

        internal static void RecordPhase(PawnTailPhase093T2 phase, long us)
        {
            if (!Active || currentPawn == null || us <= 0L) return;
            if (phase == PawnTailPhase093T2.JobTrackerTick)
                currentJobUs += us;
            else if (phase == PawnTailPhase093T2.PatherTick)
                currentPatherUs += us;
        }

        internal static void StartStage(int stage)
        {
            if (!Active || stage < 0 || stage >= StageCount) return;
            EnsureThreadArrays();
            stageStartTicks[stage] = Stopwatch.GetTimestamp();
        }

        internal static void EndStage(int stage)
        {
            if (!Active || stage < 0 || stage >= StageCount || stageStartTicks == null) return;
            long started = stageStartTicks[stage];
            stageStartTicks[stage] = 0L;
            if (started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = TicksToUs(elapsed);
            currentStageUs[stage] += us;
            StageCalls[stage]++;
            StageTotalUs[stage] += us;
            if (us > StageMaxUs[stage]) StageMaxUs[stage] = us;
            if (us >= 1000L) StageOver1[stage]++;
            if (us >= 5000L) StageOver5[stage]++;
        }

        internal static void OriginalEnd()
        {
            if (!Active || pawnStartTicks == 0L || currentOriginalUs != 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - pawnStartTicks;
            if (elapsed > 0L) currentOriginalUs = TicksToUs(elapsed);
        }

        internal static void RecordPawn(Pawn pawn, long pawnUs)
        {
            if (!Active)
            {
                ClearCurrent();
                return;
            }

            Pawn p = currentPawn;
            if (p == null || pawn == null || p != pawn || pawnUs <= 0L)
            {
                ClearCurrent();
                return;
            }

            long originalUs = currentOriginalUs;
            if (originalUs <= 0L)
            {
                missingOriginalEnd++;
                ClearCurrent();
                return;
            }

            long originalResidualUs = originalUs - currentJobUs - currentPatherUs;
            if (originalResidualUs < 0L)
            {
                residualClamp++;
                originalResidualUs = 0L;
            }

            long trackedUs = 0L;
            for (int i = 0; i < StageCount; i++) trackedUs += currentStageUs[i];
            long untrackedUs = originalResidualUs - trackedUs;
            if (untrackedUs < 0L)
            {
                untrackedClamp++;
                untrackedUs = 0L;
            }

            long postfixUs = pawnUs - originalUs;
            if (postfixUs < 0L)
            {
                postfixClamp++;
                postfixUs = 0L;
            }

            sampledPawns++;
            totalPawnUs += pawnUs;
            totalOriginalUs += originalUs;
            totalJobUs += currentJobUs;
            totalPatherUs += currentPatherUs;
            totalOriginalResidualUs += originalResidualUs;
            totalTrackedResidualUs += trackedUs;
            totalUntrackedOriginalResidualUs += untrackedUs;
            totalPostfixBeforeT2Us += postfixUs;
            if (originalResidualUs > maxOriginalResidualUs) maxOriginalResidualUs = originalResidualUs;
            if (untrackedUs > maxUntrackedUs) maxUntrackedUs = untrackedUs;
            if (postfixUs > maxPostfixUs) maxPostfixUs = postfixUs;
            if (originalResidualUs >= 1000L) residualOver1++;
            if (originalResidualUs >= 5000L) residualOver5++;
            if (originalResidualUs >= 10000L) residualOver10++;
            if (originalResidualUs >= 20000L) residualOver20++;
            if (postfixUs >= 1000L) postfixOver1++;
            if (postfixUs >= 5000L) postfixOver5++;

            if (originalResidualUs >= 2000L || postfixUs >= 1000L)
            {
                int gameTick = -1;
                try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
                catch { }
                string job = "none";
                try { if (p.CurJobDef != null) job = p.CurJobDef.defName; }
                catch { }
                Recent[recentPos] = new RecentEntry(
                    RimMTRuntime.MainThreadFrames, gameTick, p.thingIDNumber, job,
                    pawnUs, originalUs, currentJobUs, currentPatherUs,
                    originalResidualUs, trackedUs, untrackedUs, postfixUs,
                    currentStageUs);
                recentPos = (recentPos + 1) % RecentCapacity;
                if (recentCount < RecentCapacity) recentCount++;
            }

            ClearCurrent();
        }

        internal static void ResetProbeRegistry()
        {
            Array.Clear(ProbeSites, 0, ProbeSites.Length);
            Array.Clear(ProbeMethods, 0, ProbeMethods.Length);
            retSites = 0;
            skippedEhSites = 0;
            transpilerSuppressed = false;
            transpilerReason = string.Empty;
        }

        internal static void RegisterProbe(int stage, string methodName)
        {
            if (stage < 0 || stage >= StageCount) return;
            ProbeSites[stage]++;
            if (string.IsNullOrEmpty(methodName)) return;
            string current = ProbeMethods[stage];
            if (!string.IsNullOrEmpty(current) && current.IndexOf(methodName, StringComparison.Ordinal) >= 0) return;
            ProbeMethods[stage] = string.IsNullOrEmpty(current) ? methodName : current + "," + methodName;
        }

        internal static void SetTranspilerShape(int returns, int skippedEh)
        {
            retSites = returns;
            skippedEhSites = skippedEh;
        }

        internal static void SuppressTranspiler(string reason)
        {
            transpilerSuppressed = true;
            transpilerReason = reason ?? "unspecified";
        }

        internal static string Summary()
        {
            double n = sampledPawns == 0L ? 1.0 : sampledPawns;
            double coverage = totalOriginalResidualUs == 0L ? 0.0 : totalTrackedResidualUs * 100.0 / totalOriginalResidualUs;
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T14 PlayerHuman residual attribution: sampledPawns=").Append(sampledPawns)
              .Append(", samplePolicy=T2-periodic/4 (gameTick%256==0), avgPawnUs=").Append((totalPawnUs / n).ToString("F1"))
              .Append(", avgOriginalUs=").Append((totalOriginalUs / n).ToString("F1"))
              .Append(", avgJobUs=").Append((totalJobUs / n).ToString("F1"))
              .Append(", avgPatherUs=").Append((totalPatherUs / n).ToString("F1"))
              .Append(", avgOriginalResidualUs=").Append((totalOriginalResidualUs / n).ToString("F1"))
              .Append(", avgTrackedResidualUs=").Append((totalTrackedResidualUs / n).ToString("F1"))
              .Append(", trackedCoverage=").Append(coverage.ToString("F1")).Append('%')
              .Append(", avgUntrackedOriginalResidualUs=").Append((totalUntrackedOriginalResidualUs / n).ToString("F1"))
              .Append(", avgPostfixBeforeT2Us=").Append((totalPostfixBeforeT2Us / n).ToString("F1"))
              .Append(", residual>1/5/10/20ms=").Append(residualOver1).Append('/').Append(residualOver5).Append('/').Append(residualOver10).Append('/').Append(residualOver20)
              .Append(", postfix>1/5ms=").Append(postfixOver1).Append('/').Append(postfixOver5)
              .Append(", maxResidualMs=").Append((maxOriginalResidualUs / 1000.0).ToString("F2"))
              .Append(", maxUntrackedMs=").Append((maxUntrackedUs / 1000.0).ToString("F2"))
              .Append(", maxPostfixMs=").Append((maxPostfixUs / 1000.0).ToString("F2"))
              .Append(", missingOriginalEnd=").Append(missingOriginalEnd)
              .Append(", clamps[residual/untracked/postfix]=").Append(residualClamp).Append('/').Append(untrackedClamp).Append('/').Append(postfixClamp)
              .Append(". originalResidual=Pawn.Tick original body - JobTracker - Pather; postfixBeforeT2 is aggregate Harmony/wrapper time before the existing T2 last-priority postfix, not per-owner attribution.");
            return sb.ToString();
        }

        internal static string StageSummary()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T14 PlayerHuman residual stages: ");
            double sampleDenom = sampledPawns == 0L ? 1.0 : sampledPawns;
            for (int i = 0; i < StageCount; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = StageCalls[i];
                sb.Append(StageNames[i]).Append("[sites=").Append(ProbeSites[i])
                  .Append(",calls=").Append(calls)
                  .Append(",avgPerPawnUs=").Append((StageTotalUs[i] / sampleDenom).ToString("F1"))
                  .Append(",avgCallUs=").Append(calls == 0L ? "0.0" : (StageTotalUs[i] / (double)calls).ToString("F1"))
                  .Append(",>1/5ms=").Append(StageOver1[i]).Append('/').Append(StageOver5[i])
                  .Append(",maxMs=").Append((StageMaxUs[i] / 1000.0).ToString("F2")).Append(']');
            }
            return sb.ToString();
        }

        internal static string ProbeSummary()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T14 Pawn.Tick transpiler probes: suppressed=").Append(transpilerSuppressed)
              .Append(", retSites=").Append(retSites)
              .Append(", skippedEhSites=").Append(skippedEhSites);
            if (transpilerSuppressed) sb.Append(", reason=").Append(transpilerReason);
            sb.Append(". ");
            for (int i = 0; i < StageCount; i++)
            {
                if (i != 0) sb.Append("; ");
                sb.Append(StageNames[i]).Append('=').Append(ProbeSites[i]).Append('{').Append(ProbeMethods[i] ?? string.Empty).Append('}');
            }
            return sb.ToString();
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "T14 recent PlayerHuman residual/postfix tails: none.";
            StringBuilder sb = new StringBuilder(8192);
            sb.Append("T14 recent PlayerHuman residual>=2ms or postfix>=1ms (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                RecentEntry e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                  .Append(",Human#").Append(e.ThingId).Append('[').Append(e.Job).Append(']')
                  .Append(",pawn=").Append((e.PawnUs / 1000.0).ToString("F2"))
                  .Append(",orig=").Append((e.OriginalUs / 1000.0).ToString("F2"))
                  .Append(",job=").Append((e.JobUs / 1000.0).ToString("F2"))
                  .Append(",path=").Append((e.PatherUs / 1000.0).ToString("F2"))
                  .Append(",resid=").Append((e.ResidualUs / 1000.0).ToString("F2"))
                  .Append(",tracked=").Append((e.TrackedUs / 1000.0).ToString("F2"))
                  .Append(",untracked=").Append((e.UntrackedUs / 1000.0).ToString("F2"))
                  .Append(",postfix=").Append((e.PostfixUs / 1000.0).ToString("F2"))
                  .Append(",stages=").Append(e.StageText);
            }
            return sb.ToString();
        }

        private static void EnsureThreadArrays()
        {
            if (stageStartTicks == null || stageStartTicks.Length != StageCount) stageStartTicks = new long[StageCount];
            if (currentStageUs == null || currentStageUs.Length != StageCount) currentStageUs = new long[StageCount];
        }

        private static void ClearCurrent()
        {
            Active = false;
            currentPawn = null;
            pawnStartTicks = 0L;
            currentOriginalUs = 0L;
            currentJobUs = 0L;
            currentPatherUs = 0L;
            if (stageStartTicks != null) Array.Clear(stageStartTicks, 0, stageStartTicks.Length);
            if (currentStageUs != null) Array.Clear(currentStageUs, 0, currentStageUs.Length);
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct RecentEntry
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly int ThingId;
            internal readonly string Job;
            internal readonly long PawnUs;
            internal readonly long OriginalUs;
            internal readonly long JobUs;
            internal readonly long PatherUs;
            internal readonly long ResidualUs;
            internal readonly long TrackedUs;
            internal readonly long UntrackedUs;
            internal readonly long PostfixUs;
            internal readonly string StageText;

            internal RecentEntry(long frame, int gameTick, int thingId, string job,
                long pawnUs, long originalUs, long jobUs, long patherUs,
                long residualUs, long trackedUs, long untrackedUs, long postfixUs, long[] stages)
            {
                Frame = frame; GameTick = gameTick; ThingId = thingId; Job = job;
                PawnUs = pawnUs; OriginalUs = originalUs; JobUs = jobUs; PatherUs = patherUs;
                ResidualUs = residualUs; TrackedUs = trackedUs; UntrackedUs = untrackedUs; PostfixUs = postfixUs;
                StringBuilder sb = new StringBuilder(256);
                if (stages != null)
                {
                    for (int i = 0; i < StageCount; i++)
                    {
                        if (stages[i] <= 0L) continue;
                        if (sb.Length != 0) sb.Append('/');
                        sb.Append(StageNames[i]).Append(':').Append((stages[i] / 1000.0).ToString("F2"));
                    }
                }
                StageText = sb.Length == 0 ? "none" : sb.ToString();
            }
        }
    }
}
'@
Set-Content $diagPath $diag -Encoding UTF8

$patchPath = 'RimMT/Source/RimMT/Patches/PlayerHumanResidualPatches093T14.cs'
$patch = @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class PlayerHumanResidualPatches093T14
    {
        private const int MaxProbeSites = 18;
        private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(PlayerHumanResidualAttribution093T14), "Active");
        private static readonly MethodInfo StartStageMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "StartStage");
        private static readonly MethodInfo EndStageMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "EndStage");
        private static readonly MethodInfo OriginalEndMethod = AccessTools.Method(typeof(PlayerHumanResidualAttribution093T14), "OriginalEnd");

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            MethodBase target = AccessTools.Method(typeof(Pawn), "Tick");
            if (target == null)
            {
                PlayerHumanResidualAttribution093T14.SuppressTranspiler("Pawn.Tick target missing");
                Log.Warning("[RimMT] T14 PlayerHuman residual attribution failed closed: Pawn.Tick not found.");
                return;
            }
            try
            {
                HarmonyMethod transpiler = new HarmonyMethod(typeof(PlayerHumanResidualPatches093T14), nameof(Transpiler)) { priority = Priority.Last };
                harmony.Patch(target, transpiler: transpiler);
                Log.Message("[RimMT] T14 PlayerHuman residual attribution installed on Pawn.Tick. Stopwatch work is limited to PlayerHumanlike T2 periodic samples thinned to 1/256 ticks; no tracker method is separately Harmony-patched.");
            }
            catch (Exception ex)
            {
                PlayerHumanResidualAttribution093T14.SuppressTranspiler(ex.GetType().Name + ": " + ex.Message);
                Log.Warning("[RimMT] T14 PlayerHuman residual attribution failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            PlayerHumanResidualAttribution093T14.ResetProbeRegistry();

            int[] stages = new int[list.Count];
            for (int i = 0; i < stages.Length; i++) stages[i] = -1;
            int explicitSites = 0;
            int otherSites = 0;
            int skippedEh = 0;
            int returns = 0;

            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode == OpCodes.Ret) returns++;
                MethodInfo method = (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) ? ci.operand as MethodInfo : null;
                if (method == null || method.ReturnType != typeof(void)) continue;
                int stage = Classify(method);
                if (stage < 0) continue;
                if (ci.blocks != null && ci.blocks.Count != 0)
                {
                    skippedEh++;
                    continue;
                }
                stages[i] = stage;
                if (stage == (int)PlayerHumanResidualStage093T14.OtherTracker) otherSites++;
                else explicitSites++;
            }

            if (explicitSites > MaxProbeSites || returns <= 0 || ActiveField == null || StartStageMethod == null || EndStageMethod == null || OriginalEndMethod == null)
            {
                string reason = "shape unsafe: explicitSites=" + explicitSites + ", returns=" + returns;
                PlayerHumanResidualAttribution093T14.SuppressTranspiler(reason);
                PlayerHumanResidualAttribution093T14.SetTranspilerShape(returns, skippedEh);
                return list;
            }

            int remainingForOther = Math.Max(0, MaxProbeSites - explicitSites);
            if (otherSites > remainingForOther)
            {
                int kept = 0;
                for (int i = 0; i < stages.Length; i++)
                {
                    if (stages[i] != (int)PlayerHumanResidualStage093T14.OtherTracker) continue;
                    if (kept < remainingForOther) kept++;
                    else stages[i] = -1;
                }
            }

            List<CodeInstruction> output = new List<CodeInstruction>(list.Count + 128);
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                int stage = stages[i];
                if (stage >= 0)
                {
                    MethodInfo method = ci.operand as MethodInfo;
                    string fullName = method == null ? "<unknown>" : ((method.DeclaringType == null ? "<null>" : method.DeclaringType.FullName) + "." + method.Name);
                    PlayerHumanResidualAttribution093T14.RegisterProbe(stage, fullName);

                    Label callLabel = generator.DefineLabel();
                    CodeInstruction activeCheck = new CodeInstruction(OpCodes.Ldsfld, ActiveField);
                    MoveLabels(ci, activeCheck);
                    ci.labels.Add(callLabel);

                    output.Add(activeCheck);
                    output.Add(new CodeInstruction(OpCodes.Brfalse_S, callLabel));
                    output.Add(new CodeInstruction(OpCodes.Ldc_I4, stage));
                    output.Add(new CodeInstruction(OpCodes.Call, StartStageMethod));
                    output.Add(ci);

                    Label afterEnd = generator.DefineLabel();
                    output.Add(new CodeInstruction(OpCodes.Ldsfld, ActiveField));
                    output.Add(new CodeInstruction(OpCodes.Brfalse_S, afterEnd));
                    output.Add(new CodeInstruction(OpCodes.Ldc_I4, stage));
                    output.Add(new CodeInstruction(OpCodes.Call, EndStageMethod));
                    CodeInstruction nop = new CodeInstruction(OpCodes.Nop);
                    nop.labels.Add(afterEnd);
                    output.Add(nop);
                    continue;
                }

                if (ci.opcode == OpCodes.Ret && (ci.blocks == null || ci.blocks.Count == 0))
                {
                    Label retLabel = generator.DefineLabel();
                    CodeInstruction activeCheck = new CodeInstruction(OpCodes.Ldsfld, ActiveField);
                    MoveLabels(ci, activeCheck);
                    ci.labels.Add(retLabel);
                    output.Add(activeCheck);
                    output.Add(new CodeInstruction(OpCodes.Brfalse_S, retLabel));
                    output.Add(new CodeInstruction(OpCodes.Call, OriginalEndMethod));
                    output.Add(ci);
                    continue;
                }

                output.Add(ci);
            }

            PlayerHumanResidualAttribution093T14.SetTranspilerShape(returns, skippedEh);
            return output;
        }

        private static int Classify(MethodInfo method)
        {
            if (method == null || method.DeclaringType == null) return -1;
            string type = method.DeclaringType.FullName ?? string.Empty;
            string name = method.Name ?? string.Empty;

            if (type == "Verse.ThingWithComps" && (name == "Tick" || name.StartsWith("TickInterval", StringComparison.Ordinal)))
                return (int)PlayerHumanResidualStage093T14.BaseComps;

            if (type.IndexOf("Pawn_JobTracker", StringComparison.Ordinal) >= 0 || type.IndexOf("Pawn_PathFollower", StringComparison.Ordinal) >= 0)
                return -1;

            if (name.IndexOf("Tick", StringComparison.OrdinalIgnoreCase) < 0) return -1;

            if (type.IndexOf("Pawn_HealthTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.Health;
            if (type.IndexOf("Pawn_NeedsTracker", StringComparison.Ordinal) >= 0 || type.IndexOf("Pawn_MindState", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.NeedsMind;
            if (type.IndexOf("Pawn_StanceTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.Stance;
            if (type.IndexOf("Pawn_EquipmentTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_ApparelTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_InventoryTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.GearInventory;
            if (type.IndexOf("Pawn_AbilityTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_GeneTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_PsychicEntropyTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_RoyaltyTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.AbilityGene;
            if (type.IndexOf("Pawn_RelationsTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_InteractionsTracker", StringComparison.Ordinal) >= 0 ||
                type.IndexOf("Pawn_GuestTracker", StringComparison.Ordinal) >= 0)
                return (int)PlayerHumanResidualStage093T14.Social;

            if ((type.StartsWith("Verse.Pawn_", StringComparison.Ordinal) || type.StartsWith("RimWorld.Pawn_", StringComparison.Ordinal)) &&
                type.IndexOf("PathFollower", StringComparison.Ordinal) < 0 && type.IndexOf("JobTracker", StringComparison.Ordinal) < 0)
                return (int)PlayerHumanResidualStage093T14.OtherTracker;

            return -1;
        }

        private static void MoveLabels(CodeInstruction from, CodeInstruction to)
        {
            if (from == null || to == null || from.labels == null || from.labels.Count == 0) return;
            to.labels.AddRange(from.labels);
            from.labels.Clear();
        }
    }
}
'@
Set-Content $patchPath $patch -Encoding UTF8

$t2DiagPath = 'RimMT/Source/RimMT/Diagnostics/TailPawnAttribution093T2.cs'
$t2Diag = Get-Content $t2DiagPath -Raw
$t2Diag = Replace-OrThrow $t2Diag @'
            PawnTickAggregateAttribution093T13.RecordPhase(phase, us);
            Calls[p]++;
'@ @'
            PawnTickAggregateAttribution093T13.RecordPhase(phase, us);
            PlayerHumanResidualAttribution093T14.RecordPhase(phase, us);
            Calls[p]++;
'@ 'T14 reuse T2 Job/Pather phase elapsed values'
$t2Diag = Replace-OrThrow $t2Diag @'
            PawnTickAggregateAttribution093T13.RecordPawn(pawn, us);
            int p = (int)PawnTailPhase093T2.PawnTick;
'@ @'
            PawnTickAggregateAttribution093T13.RecordPawn(pawn, us);
            PlayerHumanResidualAttribution093T14.RecordPawn(pawn, us);
            int p = (int)PawnTailPhase093T2.PawnTick;
'@ 'T14 consume same T2 Pawn.Tick elapsed value'
Set-Content $t2DiagPath $t2Diag -Encoding UTF8

$t2PatchPath = 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs'
$t2Patch = Get-Content $t2PatchPath -Raw
$t2Patch = Replace-OrThrow $t2Patch @'
        public static void PawnPrefix(Pawn __instance, ref long __state)
        {
            bool deep = TailPawnAttribution093T2.DeepActive;
            PawnTickAggregateAttribution093T13.BeginPawn(__instance, deep);
            __state = deep ? TailPawnAttribution093T2.BeginPhase() : 0L;
        }
'@ @'
        public static void PawnPrefix(Pawn __instance, ref long __state)
        {
            bool deep = TailPawnAttribution093T2.DeepActive;
            PawnTickAggregateAttribution093T13.BeginPawn(__instance, deep);
            __state = deep ? TailPawnAttribution093T2.BeginPhase() : 0L;
            PlayerHumanResidualAttribution093T14.BeginPawn(__instance, deep, __state);
        }
'@ 'T14 bind PlayerHuman sample to exact T2 Pawn.Tick start timestamp'
Set-Content $t2PatchPath $t2Patch -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPawnPatches093T2.Apply(harmony);
'@ @'
            TailPawnPatches093T2.Apply(harmony);
            PlayerHumanResidualPatches093T14.Apply(harmony);
'@ 'install T14 Pawn.Tick measurement transpiler after T2 prefix/postfix'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t13-pawntick-aggregate-attribution";' 'internal const string Version = "0.9.3-t14-playerhuman-residual-attribution";' 'T14 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T13 PawnTick Aggregate Attribution initialized. T8 production behavior retained; T13 reuses T2 timestamps for category/residual attribution and adds no subtracker Harmony timers.' '[RimMT] V0.9.3-T14 PlayerHuman Residual Attribution initialized. T8 production behavior retained; T13 aggregate attribution retained; T14 measures PlayerHumanlike original residual using a bounded Pawn.Tick transpiler at 1/256 periodic samples.' 'T14 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(PawnTickAggregateAttribution093T13.PawnTickHarmonyCensus());
'@ @'
            sb.AppendLine(PawnTickAggregateAttribution093T13.PawnTickHarmonyCensus());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.Summary());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.StageSummary());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.ProbeSummary());
            sb.AppendLine(PlayerHumanResidualAttribution093T14.RecentSummary());
'@ 'T14 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T13 PawnTick Aggregate Attribution' 'V0.9.3-T14 PlayerHuman Residual Attribution' 'T14 report title'
$report = Replace-OrThrow $report 'T8 production behavior retained + T13 low-overhead PawnTick aggregate attribution reusing T2 timestamps; no T9/T10/T11/T12 probe chain; no subtracker Harmony timers;' 'T8 production behavior retained + T13 aggregate attribution + T14 PlayerHuman residual decomposition via one Pawn.Tick measurement-only transpiler; T14 Stopwatch work only at 1/256 periodic PlayerHuman samples; no tracker method Harmony timers; no T9/T10/T11/T12 probe chain;' 'T14 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T13 PawnTick Aggregate Attribution', 'V0.9.3-T14 PlayerHuman Residual Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T14: clean T13/T8 child; PlayerHumanlike original Pawn.Tick residual decomposition via one bounded transpiler; actual Stopwatch work only on gameTick%256==0 T2 periodic samples; no tracker methods separately patched.'