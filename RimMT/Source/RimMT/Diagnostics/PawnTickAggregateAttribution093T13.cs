using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// V0.9.3-T13: low-overhead aggregate attribution for Pawn.Tick.
    /// Reuses the Stopwatch timestamps already collected by T2. No new per-Pawn Stopwatch,
    /// no new Harmony patch on Pawn subtrackers, and no behavior change.
    /// </summary>
    internal static class PawnTickAggregateAttribution093T13
    {
        private const int CategoryCount = 7;
        private const int RecentCapacity = 24;

        private static readonly string[] CategoryNames =
        {
            "PlayerHumanlike", "OtherHumanlike", "PlayerAnimal", "OtherAnimal", "PlayerMech", "OtherMech", "Other"
        };

        private static readonly long[] CategoryCalls = new long[CategoryCount];
        private static readonly long[] CategoryTotalUs = new long[CategoryCount];
        private static readonly long[] CategoryJobUs = new long[CategoryCount];
        private static readonly long[] CategoryPatherUs = new long[CategoryCount];
        private static readonly long[] CategoryResidualUs = new long[CategoryCount];
        private static readonly long[] CategoryMaxUs = new long[CategoryCount];
        private static readonly SlowPawn[] Recent = new SlowPawn[RecentCapacity];

        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static long currentJobTrackerUs;
        [ThreadStatic] private static long currentDetermineUs;
        [ThreadStatic] private static long currentOverrideUs;
        [ThreadStatic] private static long currentPatherUs;

        private static long sampledPawns;
        private static long totalPawnUs;
        private static long totalJobTrackerUs;
        private static long totalDetermineUs;
        private static long totalOverrideUs;
        private static long totalPatherUs;
        private static long totalResidualUs;
        private static long maxPawnUs;
        private static long maxResidualUs;
        private static long over1;
        private static long over5;
        private static long over10;
        private static long over20;
        private static long residualOver1;
        private static long residualOver5;
        private static long residualOver10;
        private static long residualOver20;
        private static long negativeResidualClamp;
        private static int recentPos;
        private static int recentCount;

        internal static void BeginPawn(Pawn pawn, bool deepActive)
        {
            if (!deepActive || !RimMTThreadGuard.IsMainThread)
            {
                currentPawn = null;
                currentJobTrackerUs = currentDetermineUs = currentOverrideUs = currentPatherUs = 0L;
                return;
            }

            currentPawn = pawn;
            currentJobTrackerUs = 0L;
            currentDetermineUs = 0L;
            currentOverrideUs = 0L;
            currentPatherUs = 0L;
        }

        internal static void RecordPhase(PawnTailPhase093T2 phase, long us)
        {
            if (currentPawn == null || us <= 0L) return;
            switch (phase)
            {
                case PawnTailPhase093T2.JobTrackerTick:
                    currentJobTrackerUs += us;
                    break;
                case PawnTailPhase093T2.DetermineNextJob:
                    currentDetermineUs += us;
                    break;
                case PawnTailPhase093T2.CheckForJobOverride:
                    currentOverrideUs += us;
                    break;
                case PawnTailPhase093T2.PatherTick:
                    currentPatherUs += us;
                    break;
            }
        }

        internal static void RecordPawn(Pawn pawn, long pawnUs)
        {
            Pawn p = currentPawn ?? pawn;
            if (p == null || pawnUs <= 0L)
            {
                ClearCurrent();
                return;
            }

            long jobUs = currentJobTrackerUs;
            long patherUs = currentPatherUs;
            long residualUs = pawnUs - jobUs - patherUs;
            if (residualUs < 0L)
            {
                negativeResidualClamp++;
                residualUs = 0L;
            }

            sampledPawns++;
            totalPawnUs += pawnUs;
            totalJobTrackerUs += jobUs;
            totalDetermineUs += currentDetermineUs;
            totalOverrideUs += currentOverrideUs;
            totalPatherUs += patherUs;
            totalResidualUs += residualUs;
            if (pawnUs > maxPawnUs) maxPawnUs = pawnUs;
            if (residualUs > maxResidualUs) maxResidualUs = residualUs;
            if (pawnUs >= 1000L) over1++;
            if (pawnUs >= 5000L) over5++;
            if (pawnUs >= 10000L) over10++;
            if (pawnUs >= 20000L) over20++;
            if (residualUs >= 1000L) residualOver1++;
            if (residualUs >= 5000L) residualOver5++;
            if (residualUs >= 10000L) residualOver10++;
            if (residualUs >= 20000L) residualOver20++;

            int category = CategoryOf(p);
            CategoryCalls[category]++;
            CategoryTotalUs[category] += pawnUs;
            CategoryJobUs[category] += jobUs;
            CategoryPatherUs[category] += patherUs;
            CategoryResidualUs[category] += residualUs;
            if (pawnUs > CategoryMaxUs[category]) CategoryMaxUs[category] = pawnUs;

            if (pawnUs >= 5000L)
            {
                Recent[recentPos] = MakeSlowPawn(p, pawnUs, jobUs, currentDetermineUs, currentOverrideUs, patherUs, residualUs, category);
                recentPos = (recentPos + 1) % RecentCapacity;
                if (recentCount < RecentCapacity) recentCount++;
            }

            ClearCurrent();
        }

        private static void ClearCurrent()
        {
            currentPawn = null;
            currentJobTrackerUs = currentDetermineUs = currentOverrideUs = currentPatherUs = 0L;
        }

        private static int CategoryOf(Pawn pawn)
        {
            try
            {
                if (pawn == null || pawn.def == null || pawn.def.race == null) return 6;
                bool player = pawn.Faction != null && pawn.Faction.IsPlayer;
                if (pawn.def.race.IsMechanoid) return player ? 4 : 5;
                if (pawn.def.race.Humanlike) return player ? 0 : 1;
                return player ? 2 : 3;
            }
            catch
            {
                return 6;
            }
        }

        internal static string Summary()
        {
            double avg = sampledPawns == 0L ? 0.0 : totalPawnUs / (double)sampledPawns;
            double avgJob = sampledPawns == 0L ? 0.0 : totalJobTrackerUs / (double)sampledPawns;
            double avgDetermine = sampledPawns == 0L ? 0.0 : totalDetermineUs / (double)sampledPawns;
            double avgOverride = sampledPawns == 0L ? 0.0 : totalOverrideUs / (double)sampledPawns;
            double avgPather = sampledPawns == 0L ? 0.0 : totalPatherUs / (double)sampledPawns;
            double avgResidual = sampledPawns == 0L ? 0.0 : totalResidualUs / (double)sampledPawns;
            double jobShare = totalPawnUs == 0L ? 0.0 : totalJobTrackerUs * 100.0 / totalPawnUs;
            double patherShare = totalPawnUs == 0L ? 0.0 : totalPatherUs * 100.0 / totalPawnUs;
            double residualShare = totalPawnUs == 0L ? 0.0 : totalResidualUs * 100.0 / totalPawnUs;

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T13 PawnTick aggregate attribution: sampledPawns=").Append(sampledPawns)
              .Append(", avgPawnUs=").Append(avg.ToString("F1"))
              .Append(", avgJobTrackerUs=").Append(avgJob.ToString("F1"))
              .Append(", avgDetermineUs[nested]=").Append(avgDetermine.ToString("F1"))
              .Append(", avgOverrideUs[nested]=").Append(avgOverride.ToString("F1"))
              .Append(", avgPatherUs=").Append(avgPather.ToString("F1"))
              .Append(", avgResidualUs=").Append(avgResidual.ToString("F1"))
              .Append(", shares[job/pather/residual]=")
              .Append(jobShare.ToString("F1")).Append('%').Append('/')
              .Append(patherShare.ToString("F1")).Append('%').Append('/')
              .Append(residualShare.ToString("F1")).Append('%')
              .Append(", pawn>1/5/10/20ms=").Append(over1).Append('/').Append(over5).Append('/').Append(over10).Append('/').Append(over20)
              .Append(", residual>1/5/10/20ms=").Append(residualOver1).Append('/').Append(residualOver5).Append('/').Append(residualOver10).Append('/').Append(residualOver20)
              .Append(", maxPawnMs=").Append((maxPawnUs / 1000.0).ToString("F2"))
              .Append(", maxResidualMs=").Append((maxResidualUs / 1000.0).ToString("F2"))
              .Append(", negativeResidualClamp=").Append(negativeResidualClamp)
              .Append(". JobTracker and Pather are direct-child inclusive totals; Determine/Override are nested evidence and are not subtracted again.");
            return sb.ToString();
        }

        internal static string CategorySummary()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T13 PawnTick categories: ");
            for (int i = 0; i < CategoryCount; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = CategoryCalls[i];
                double totalAvg = calls == 0L ? 0.0 : CategoryTotalUs[i] / (double)calls;
                double jobAvg = calls == 0L ? 0.0 : CategoryJobUs[i] / (double)calls;
                double pathAvg = calls == 0L ? 0.0 : CategoryPatherUs[i] / (double)calls;
                double residualAvg = calls == 0L ? 0.0 : CategoryResidualUs[i] / (double)calls;
                double contribution = totalPawnUs == 0L ? 0.0 : CategoryTotalUs[i] * 100.0 / totalPawnUs;
                sb.Append(CategoryNames[i]).Append("[calls=").Append(calls)
                  .Append(",avgUs=").Append(totalAvg.ToString("F1"))
                  .Append(",job=").Append(jobAvg.ToString("F1"))
                  .Append(",pather=").Append(pathAvg.ToString("F1"))
                  .Append(",residual=").Append(residualAvg.ToString("F1"))
                  .Append(",cpuShare=").Append(contribution.ToString("F1")).Append('%')
                  .Append(",maxMs=").Append((CategoryMaxUs[i] / 1000.0).ToString("F2")).Append(']');
            }
            return sb.ToString();
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "T13 recent sampled Pawn.Tick >=5ms: none.";
            StringBuilder sb = new StringBuilder(8192);
            sb.Append("T13 recent sampled Pawn.Tick >=5ms (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                SlowPawn e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                  .Append(",pawn=").Append(e.PawnDef).Append('#').Append(e.ThingId).Append('[').Append(e.JobDef).Append(']')
                  .Append(",cat=").Append(CategoryNames[e.Category])
                  .Append(",total=").Append((e.TotalUs / 1000.0).ToString("F2"))
                  .Append(",job=").Append((e.JobUs / 1000.0).ToString("F2"))
                  .Append(",det=").Append((e.DetermineUs / 1000.0).ToString("F2"))
                  .Append(",ovr=").Append((e.OverrideUs / 1000.0).ToString("F2"))
                  .Append(",path=").Append((e.PatherUs / 1000.0).ToString("F2"))
                  .Append(",residual=").Append((e.ResidualUs / 1000.0).ToString("F2"));
            }
            return sb.ToString();
        }

        internal static string PawnTickHarmonyCensus()
        {
            MethodBase target = AccessTools.Method(typeof(Pawn), "Tick");
            if (target == null) return "T13 Pawn.Tick Harmony census: target missing.";
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return "T13 Pawn.Tick Harmony census: no patches.";

            StringBuilder sb = new StringBuilder(4096);
            int foreign = 0;
            AppendPatchList(sb, "Prefix", info.Prefixes, ref foreign);
            AppendPatchList(sb, "Postfix", info.Postfixes, ref foreign);
            AppendPatchList(sb, "Transpiler", info.Transpilers, ref foreign);
            AppendPatchList(sb, "Finalizer", info.Finalizers, ref foreign);
            sb.Insert(0, "T13 Pawn.Tick Harmony census: foreignPatches=" + foreign + ". ");
            sb.Append(" Census is on-demand only; T13 does not wrap foreign patch methods with timers.");
            return sb.ToString();
        }

        private static void AppendPatchList(StringBuilder sb, string kind, System.Collections.Generic.IEnumerable<Patch> patches, ref int foreign)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                bool isRimMT = string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal);
                if (!isRimMT) foreign++;
                MethodInfo method = patch.PatchMethod;
                sb.Append(kind).Append("[owner=").Append(patch.owner ?? "<null>")
                  .Append(",priority=").Append(patch.priority)
                  .Append(",method=").Append(method == null ? "<null>" : method.DeclaringType.FullName + "." + method.Name)
                  .Append(isRimMT ? ",RimMT] " : ",FOREIGN] ");
            }
        }

        private static SlowPawn MakeSlowPawn(Pawn pawn, long totalUs, long jobUs, long determineUs, long overrideUs, long patherUs, long residualUs, int category)
        {
            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }
            string pawnDef = pawn.def == null ? "Pawn" : pawn.def.defName;
            string jobDef = "none";
            try { if (pawn.CurJobDef != null) jobDef = pawn.CurJobDef.defName; }
            catch { }
            return new SlowPawn(RimMTRuntime.MainThreadFrames, gameTick, pawn.thingIDNumber, pawnDef, jobDef, category,
                totalUs, jobUs, determineUs, overrideUs, patherUs, residualUs);
        }

        private struct SlowPawn
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly int ThingId;
            internal readonly string PawnDef;
            internal readonly string JobDef;
            internal readonly int Category;
            internal readonly long TotalUs;
            internal readonly long JobUs;
            internal readonly long DetermineUs;
            internal readonly long OverrideUs;
            internal readonly long PatherUs;
            internal readonly long ResidualUs;

            internal SlowPawn(long frame, int gameTick, int thingId, string pawnDef, string jobDef, int category,
                long totalUs, long jobUs, long determineUs, long overrideUs, long patherUs, long residualUs)
            {
                Frame = frame;
                GameTick = gameTick;
                ThingId = thingId;
                PawnDef = pawnDef;
                JobDef = jobDef;
                Category = category;
                TotalUs = totalUs;
                JobUs = jobUs;
                DetermineUs = determineUs;
                OverrideUs = overrideUs;
                PatherUs = patherUs;
                ResidualUs = residualUs;
            }
        }
    }
}
