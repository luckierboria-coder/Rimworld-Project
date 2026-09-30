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
