using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    internal enum TryEnterStage093T12
    {
        Blocker = 0,
        NextDoor = 1,
        Position = 2,
        Clamor = 3,
        Filth = 4,
        Snow = 5,
        DoorTouch = 6,
        DoorOpen = 7,
        DoorClose = 8,
        Rope = 9,
        NeedNewPath = 10,
        TrySetNewPath = 11,
        AtDestination = 12,
        SetupMove = 13,
        Count = 14
    }

    internal static class TryEnterCleanAttribution093T12
    {
        private const int Recent10Capacity = 24;
        private const int Recent20Capacity = 24;
        private static readonly StageStat[] StageStats = new StageStat[(int)TryEnterStage093T12.Count];
        private static readonly RecentEntry[] Recent10 = new RecentEntry[Recent10Capacity];
        private static readonly RecentEntry[] Recent20 = new RecentEntry[Recent20Capacity];

        [ThreadStatic] private static bool active;
        [ThreadStatic] private static Pawn pawn;
        [ThreadStatic] private static long enterStarted;
        [ThreadStatic] private static long blockerUs;
        [ThreadStatic] private static long nextDoorUs;
        [ThreadStatic] private static long positionUs;
        [ThreadStatic] private static long clamorUs;
        [ThreadStatic] private static long filthUs;
        [ThreadStatic] private static long snowUs;
        [ThreadStatic] private static long doorTouchUs;
        [ThreadStatic] private static long doorOpenUs;
        [ThreadStatic] private static long doorCloseUs;
        [ThreadStatic] private static long ropeUs;
        [ThreadStatic] private static long needUs;
        [ThreadStatic] private static long setUs;
        [ThreadStatic] private static long destUs;
        [ThreadStatic] private static long setupUs;
        [ThreadStatic] private static IntVec3 startCell;

        private static long enters;
        private static long totalUs;
        private static long maxUs;
        private static long over5;
        private static long over10;
        private static long over20;
        private static long residualTotalUs;
        private static long residualMaxUs;
        private static long residualOver10;
        private static long residualOver20;
        private static long negativeResidualClamp;
        private static int recent10Pos;
        private static int recent10Count;
        private static int recent20Pos;
        private static int recent20Count;

        internal static bool Active => active;

        internal static long BeginEnter(Pawn p)
        {
            if (!TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread)
            {
                active = false;
                return 0L;
            }

            active = true;
            pawn = p;
            enterStarted = Stopwatch.GetTimestamp();
            blockerUs = nextDoorUs = positionUs = clamorUs = filthUs = snowUs = 0L;
            doorTouchUs = doorOpenUs = doorCloseUs = ropeUs = needUs = setUs = destUs = setupUs = 0L;
            try { startCell = p == null ? IntVec3.Invalid : p.Position; }
            catch { startCell = IntVec3.Invalid; }
            return enterStarted;
        }

        internal static void EndEnter(long started)
        {
            if (!active || started == 0L)
                return;

            long ticks = Stopwatch.GetTimestamp() - started;
            long us = ticks <= 0L ? 0L : TicksToUs(ticks);
            long known = blockerUs + nextDoorUs + positionUs + clamorUs + filthUs + snowUs +
                         doorTouchUs + doorOpenUs + doorCloseUs + ropeUs + needUs + setUs + destUs + setupUs;
            long residual = us - known;
            if (residual < 0L)
            {
                negativeResidualClamp++;
                residual = 0L;
            }

            enters++;
            totalUs += us;
            residualTotalUs += residual;
            if (us > maxUs) maxUs = us;
            if (residual > residualMaxUs) residualMaxUs = residual;
            if (us >= 5000L) over5++;
            if (us >= 10000L) over10++;
            if (us >= 20000L) over20++;
            if (residual >= 10000L) residualOver10++;
            if (residual >= 20000L) residualOver20++;

            if (us >= 10000L)
            {
                RecentEntry e = MakeRecent(us, residual);
                Recent10[recent10Pos] = e;
                recent10Pos = (recent10Pos + 1) % Recent10Capacity;
                if (recent10Count < Recent10Capacity) recent10Count++;
                if (us >= 20000L)
                {
                    Recent20[recent20Pos] = e;
                    recent20Pos = (recent20Pos + 1) % Recent20Capacity;
                    if (recent20Count < Recent20Capacity) recent20Count++;
                }
            }

            active = false;
            pawn = null;
            enterStarted = 0L;
        }

        internal static bool ShouldMeasureThing(Thing thing)
        {
            return active && thing != null && ReferenceEquals(thing, pawn) && RimMTThreadGuard.IsMainThread;
        }

        internal static long BeginStage(TryEnterStage093T12 stage)
        {
            if (!active || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndStage(TryEnterStage093T12 stage, long started)
        {
            if (!active || started == 0L) return;
            long ticks = Stopwatch.GetTimestamp() - started;
            if (ticks <= 0L) return;
            long us = TicksToUs(ticks);
            AddLocal(stage, us);
            int i = (int)stage;
            if (i < 0 || i >= StageStats.Length) return;
            StageStat s = StageStats[i];
            s.Calls++;
            s.TotalUs += us;
            if (us > s.MaxUs) s.MaxUs = us;
            if (us >= 1000L) s.Over1++;
            if (us >= 5000L) s.Over5++;
            if (us >= 10000L) s.Over10++;
        }

        private static void AddLocal(TryEnterStage093T12 stage, long us)
        {
            switch (stage)
            {
                case TryEnterStage093T12.Blocker: blockerUs += us; break;
                case TryEnterStage093T12.NextDoor: nextDoorUs += us; break;
                case TryEnterStage093T12.Position: positionUs += us; break;
                case TryEnterStage093T12.Clamor: clamorUs += us; break;
                case TryEnterStage093T12.Filth: filthUs += us; break;
                case TryEnterStage093T12.Snow: snowUs += us; break;
                case TryEnterStage093T12.DoorTouch: doorTouchUs += us; break;
                case TryEnterStage093T12.DoorOpen: doorOpenUs += us; break;
                case TryEnterStage093T12.DoorClose: doorCloseUs += us; break;
                case TryEnterStage093T12.Rope: ropeUs += us; break;
                case TryEnterStage093T12.NeedNewPath: needUs += us; break;
                case TryEnterStage093T12.TrySetNewPath: setUs += us; break;
                case TryEnterStage093T12.AtDestination: destUs += us; break;
                case TryEnterStage093T12.SetupMove: setupUs += us; break;
            }
        }

        internal static string Summary()
        {
            double avg = enters == 0L ? 0.0 : totalUs / (double)enters;
            double residualAvg = enters == 0L ? 0.0 : residualTotalUs / (double)enters;
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T12 clean TryEnter attribution: enters=").Append(enters)
              .Append(", avgUs=").Append(avg.ToString("F1"))
              .Append(", >5/10/20=").Append(over5).Append('/').Append(over10).Append('/').Append(over20)
              .Append(", maxMs=").Append((maxUs / 1000.0).ToString("F2"))
              .Append(", residual[avgUs=").Append(residualAvg.ToString("F1"))
              .Append(",>10/20=").Append(residualOver10).Append('/').Append(residualOver20)
              .Append(",maxMs=").Append((residualMaxUs / 1000.0).ToString("F2")).Append(']')
              .Append(", stages=");
            for (int i = 0; i < StageStats.Length; i++)
            {
                if (i != 0) sb.Append(';');
                StageStat s = StageStats[i];
                double a = s.Calls == 0L ? 0.0 : s.TotalUs / (double)s.Calls;
                sb.Append(((TryEnterStage093T12)i).ToString()).Append("(calls=").Append(s.Calls)
                  .Append(",avgUs=").Append(a.ToString("F1"))
                  .Append(",>1/5/10=").Append(s.Over1).Append('/').Append(s.Over5).Append('/').Append(s.Over10)
                  .Append(",maxMs=").Append((s.MaxUs / 1000.0).ToString("F2")).Append(')');
            }
            sb.Append(", negativeResidualClamp=").Append(negativeResidualClamp)
              .Append(". Diagnostic-only; built directly on T8 without T9/T10/T11 probes.");
            return sb.ToString();
        }

        internal static string Recent10Summary() => RecentSummary(Recent10, Recent10Capacity, recent10Pos, recent10Count, 10);
        internal static string Recent20Summary() => RecentSummary(Recent20, Recent20Capacity, recent20Pos, recent20Count, 20);

        private static string RecentSummary(RecentEntry[] ring, int capacity, int pos, int count, int threshold)
        {
            if (count <= 0) return "T12 recent TryEnter >=" + threshold + "ms: none.";
            StringBuilder sb = new StringBuilder(threshold == 20 ? 8192 : 6144);
            sb.Append("T12 recent TryEnter >=").Append(threshold).Append("ms (oldest->newest): ");
            int start = count == capacity ? pos : 0;
            for (int i = 0; i < count; i++)
            {
                if (i != 0) sb.Append("; ");
                AppendEntry(sb, ring[(start + i) % capacity]);
            }
            return sb.ToString();
        }

        private static RecentEntry MakeRecent(long total, long residual)
        {
            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }
            Pawn p = pawn;
            string pawnDef = p != null && p.def != null ? p.def.defName : "<null>";
            string jobDef = p != null && p.CurJob != null && p.CurJob.def != null ? p.CurJob.def.defName : "none";
            IntVec3 end = IntVec3.Invalid;
            try { if (p != null) end = p.Position; }
            catch { }
            return new RecentEntry(RimMTRuntime.MainThreadFrames, gameTick, total, residual,
                blockerUs, nextDoorUs, positionUs, clamorUs, filthUs, snowUs, doorTouchUs, doorOpenUs,
                doorCloseUs, ropeUs, needUs, setUs, destUs, setupUs, pawnDef, jobDef, startCell, end);
        }

        private static void AppendEntry(StringBuilder sb, RecentEntry e)
        {
            sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
              .Append(",total=").Append((e.TotalUs / 1000.0).ToString("F2"))
              .Append(",pos=").Append((e.PositionUs / 1000.0).ToString("F2"))
              .Append(",clamor=").Append((e.ClamorUs / 1000.0).ToString("F2"))
              .Append(",filth=").Append((e.FilthUs / 1000.0).ToString("F2"))
              .Append(",snow=").Append((e.SnowUs / 1000.0).ToString("F2"))
              .Append(",door=").Append(((e.DoorTouchUs + e.DoorOpenUs + e.DoorCloseUs) / 1000.0).ToString("F2"))
              .Append(",block=").Append((e.BlockerUs / 1000.0).ToString("F2"))
              .Append(",nextDoor=").Append((e.NextDoorUs / 1000.0).ToString("F2"))
              .Append(",rope=").Append((e.RopeUs / 1000.0).ToString("F2"))
              .Append(",need=").Append((e.NeedUs / 1000.0).ToString("F2"))
              .Append(",set=").Append((e.SetUs / 1000.0).ToString("F2"))
              .Append(",dest=").Append((e.DestUs / 1000.0).ToString("F2"))
              .Append(",setup=").Append((e.SetupUs / 1000.0).ToString("F2"))
              .Append(",residual=").Append((e.ResidualUs / 1000.0).ToString("F2"))
              .Append(",pawn=").Append(e.PawnDef).Append('[').Append(e.JobDef).Append(']')
              .Append(",cell=").Append(e.StartCell).Append("->").Append(e.EndCell);
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private sealed class StageStat
        {
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
            internal long Over1;
            internal long Over5;
            internal long Over10;
        }

        private struct RecentEntry
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TotalUs;
            internal readonly long ResidualUs;
            internal readonly long BlockerUs;
            internal readonly long NextDoorUs;
            internal readonly long PositionUs;
            internal readonly long ClamorUs;
            internal readonly long FilthUs;
            internal readonly long SnowUs;
            internal readonly long DoorTouchUs;
            internal readonly long DoorOpenUs;
            internal readonly long DoorCloseUs;
            internal readonly long RopeUs;
            internal readonly long NeedUs;
            internal readonly long SetUs;
            internal readonly long DestUs;
            internal readonly long SetupUs;
            internal readonly string PawnDef;
            internal readonly string JobDef;
            internal readonly IntVec3 StartCell;
            internal readonly IntVec3 EndCell;

            internal RecentEntry(long frame, int gameTick, long totalUs, long residualUs,
                long blockerUs, long nextDoorUs, long positionUs, long clamorUs, long filthUs, long snowUs,
                long doorTouchUs, long doorOpenUs, long doorCloseUs, long ropeUs, long needUs, long setUs,
                long destUs, long setupUs, string pawnDef, string jobDef, IntVec3 startCell, IntVec3 endCell)
            {
                Frame = frame;
                GameTick = gameTick;
                TotalUs = totalUs;
                ResidualUs = residualUs;
                BlockerUs = blockerUs;
                NextDoorUs = nextDoorUs;
                PositionUs = positionUs;
                ClamorUs = clamorUs;
                FilthUs = filthUs;
                SnowUs = snowUs;
                DoorTouchUs = doorTouchUs;
                DoorOpenUs = doorOpenUs;
                DoorCloseUs = doorCloseUs;
                RopeUs = ropeUs;
                NeedUs = needUs;
                SetUs = setUs;
                DestUs = destUs;
                SetupUs = setupUs;
                PawnDef = pawnDef;
                JobDef = jobDef;
                StartCell = startCell;
                EndCell = endCell;
            }
        }
    }
}
