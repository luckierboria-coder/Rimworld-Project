using System;
using System.Diagnostics;
using System.Text;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T11 diagnostic-only correlation for Pawn_PathFollower.GenerateNewPath.
    /// Active only while the existing T9 bounded Pather probe is active.
    /// Correlates total GenerateNewPath time with nested T3 PathFinder time and directly timed
    /// VFE GenerateNewPath prefix / Pathfinding Framework debugging postfix methods.
    /// No path state, result, traversal parameter, Harmony owner order or job state is changed.
    /// </summary>
    internal static class GenerateNewPathAttribution093T11
    {
        private const int Recent10Capacity = 16;
        private const int Recent20Capacity = 32;
        private static readonly GenerateEvent[] Recent10 = new GenerateEvent[Recent10Capacity];
        private static readonly GenerateEvent[] Recent20 = new GenerateEvent[Recent20Capacity];

        [ThreadStatic] private static bool active;
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static IntVec3 currentStart;
        [ThreadStatic] private static IntVec3 currentDestination;
        [ThreadStatic] private static PathEndMode currentPeMode;
        [ThreadStatic] private static long currentFindUs;
        [ThreadStatic] private static long currentFindMaxUs;
        [ThreadStatic] private static int currentFindCalls;
        [ThreadStatic] private static long currentVfeUs;
        [ThreadStatic] private static long currentPfUs;
        [ThreadStatic] private static bool currentVfeSeen;
        [ThreadStatic] private static bool currentVfeSkippedOriginal;

        private static long generates;
        private static long totalUs;
        private static long maxUs;
        private static long over10;
        private static long over20;
        private static long over50;
        private static long nestedFindCalls;
        private static long nestedFindUs;
        private static long nestedFindMaxUs;
        private static long normalFindDominant20;
        private static long vfeSkipped20;
        private static long vfeSkippedFindDominant20;
        private static long pfDominant20;
        private static long other20;
        private static long negativeBodyClamp;

        private static bool vfeFound;
        private static bool vfeInstrumented;
        private static bool pfFound;
        private static bool pfInstrumented;
        private static long vfeCalls;
        private static long vfeTotalUs;
        private static long vfeMaxUs;
        private static long vfeSkippedCalls;
        private static long pfCalls;
        private static long pfTotalUs;
        private static long pfMaxUs;

        private static int recent10Pos;
        private static int recent10Count;
        private static int recent20Pos;
        private static int recent20Count;

        internal static bool Active { get { return active; } }

        internal static void SetInstallState(bool foundVfe, bool timedVfe, bool foundPf, bool timedPf)
        {
            vfeFound = foundVfe;
            vfeInstrumented = timedVfe;
            pfFound = foundPf;
            pfInstrumented = timedPf;
        }

        internal static void BeginGenerate(Pawn pawn, LocalTargetInfo destination, PathEndMode peMode)
        {
            if (!PatherInternalAttribution093T9.Active || !TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread)
            {
                active = false;
                return;
            }

            active = true;
            currentPawn = pawn;
            currentStart = pawn == null ? IntVec3.Invalid : pawn.Position;
            try { currentDestination = destination.IsValid ? destination.Cell : IntVec3.Invalid; }
            catch { currentDestination = IntVec3.Invalid; }
            currentPeMode = peMode;
            currentFindUs = 0L;
            currentFindMaxUs = 0L;
            currentFindCalls = 0;
            currentVfeUs = 0L;
            currentPfUs = 0L;
            currentVfeSeen = false;
            currentVfeSkippedOriginal = false;
        }

        internal static void EndGenerate(long started)
        {
            if (!active)
                return;

            long elapsedTicks = started <= 0L ? 0L : Stopwatch.GetTimestamp() - started;
            long us = elapsedTicks <= 0L ? 0L : TicksToUs(elapsedTicks);

            Pawn pawn = currentPawn;
            IntVec3 start = currentStart;
            IntVec3 dest = currentDestination;
            PathEndMode mode = currentPeMode;
            long findUs = currentFindUs;
            long findMaxUs = currentFindMaxUs;
            int findCalls = currentFindCalls;
            long vfeUs = currentVfeUs;
            long pfUs = currentPfUs;
            bool vfeSeen = currentVfeSeen;
            bool vfeSkipped = currentVfeSkippedOriginal;

            active = false;
            currentPawn = null;

            if (us <= 0L) return;
            generates++;
            totalUs += us;
            if (us > maxUs) maxUs = us;
            if (us >= 10000L) over10++;
            if (us >= 20000L) over20++;
            if (us >= 50000L) over50++;

            long bodyApproxUs = us - vfeUs - pfUs;
            if (bodyApproxUs < 0L)
            {
                bodyApproxUs = 0L;
                negativeBodyClamp++;
            }

            if (us >= 20000L)
            {
                if (vfeSkipped)
                {
                    vfeSkipped20++;
                    if (findUs * 100L >= Math.Max(1L, vfeUs) * 80L) vfeSkippedFindDominant20++;
                }
                else if (findUs * 100L >= us * 80L)
                {
                    normalFindDominant20++;
                }
                else if (pfUs * 100L >= us * 50L)
                {
                    pfDominant20++;
                }
                else
                {
                    other20++;
                }
            }

            if (us < 10000L) return;

            string pawnDef = SafePawnDef(pawn);
            string jobDef = SafeJobDef(pawn);
            int distSq = DistanceSquared(start, dest);
            GenerateEvent e = new GenerateEvent(
                RimMTRuntime.MainThreadFrames, SafeGameTick(), us, bodyApproxUs,
                findCalls, findUs, findMaxUs, vfeUs, pfUs, vfeSeen, vfeSkipped,
                pawnDef, jobDef, start, dest, distSq, mode);
            PushRecent10(e);
            if (us >= 20000L) PushRecent20(e);
        }

        internal static void NoteFindPath(long us)
        {
            if (!active || us <= 0L) return;
            currentFindCalls++;
            currentFindUs += us;
            if (us > currentFindMaxUs) currentFindMaxUs = us;
            nestedFindCalls++;
            nestedFindUs += us;
            if (us > nestedFindMaxUs) nestedFindMaxUs = us;
        }

        internal static long BeginForeignCall()
        {
            if (!active || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndVfe(long started, bool prefixResult)
        {
            if (started == 0L || !active) return;
            long us = ElapsedUs(started);
            currentVfeSeen = true;
            currentVfeUs += us;
            if (!prefixResult) currentVfeSkippedOriginal = true;
            vfeCalls++;
            vfeTotalUs += us;
            if (us > vfeMaxUs) vfeMaxUs = us;
            if (!prefixResult) vfeSkippedCalls++;
        }

        internal static void EndPf(long started)
        {
            if (started == 0L || !active) return;
            long us = ElapsedUs(started);
            currentPfUs += us;
            pfCalls++;
            pfTotalUs += us;
            if (us > pfMaxUs) pfMaxUs = us;
        }

        internal static string Summary()
        {
            double avg = generates == 0L ? 0.0 : totalUs / (double)generates;
            double findAvg = nestedFindCalls == 0L ? 0.0 : nestedFindUs / (double)nestedFindCalls;
            double vfeAvg = vfeCalls == 0L ? 0.0 : vfeTotalUs / (double)vfeCalls;
            double pfAvg = pfCalls == 0L ? 0.0 : pfTotalUs / (double)pfCalls;
            return "T11 GenerateNewPath attribution: generates=" + generates +
                   ", avgUs=" + avg.ToString("F1") +
                   ", >10/20/50=" + over10 + "/" + over20 + "/" + over50 +
                   ", maxMs=" + (maxUs / 1000.0).ToString("F2") +
                   ", nestedFind[calls=" + nestedFindCalls + ",avgUs=" + findAvg.ToString("F1") + ",maxMs=" + (nestedFindMaxUs / 1000.0).ToString("F2") + "]" +
                   ", VFE[found=" + vfeFound + ",instrumented=" + vfeInstrumented + ",calls=" + vfeCalls + ",avgUs=" + vfeAvg.ToString("F1") + ",maxMs=" + (vfeMaxUs / 1000.0).ToString("F2") + ",skippedOriginal=" + vfeSkippedCalls + "]" +
                   ", PF[found=" + pfFound + ",instrumented=" + pfInstrumented + ",calls=" + pfCalls + ",avgUs=" + pfAvg.ToString("F1") + ",maxMs=" + (pfMaxUs / 1000.0).ToString("F2") + "]" +
                   ", >=20 classification[normalFind>=80%=" + normalFindDominant20 +
                   ",vfeSkipped=" + vfeSkipped20 + ",vfeSkippedFind>=80%Vfe=" + vfeSkippedFindDominant20 +
                   ",pf>=50%=" + pfDominant20 + ",other=" + other20 + "]" +
                   ", negativeBodyClamp=" + negativeBodyClamp +
                   ". bodyApprox=Generate inclusive time minus directly measured VFE prefix and PF postfix; nested FindPath is reported separately and may be inside VFE when VFE skips original.";
        }

        internal static string Recent10Summary()
        {
            return FormatRecent("T11 recent GenerateNewPath >=10ms", Recent10, Recent10Capacity, recent10Pos, recent10Count);
        }

        internal static string Recent20Summary()
        {
            return FormatRecent("T11 recent GenerateNewPath >=20ms", Recent20, Recent20Capacity, recent20Pos, recent20Count);
        }

        private static string FormatRecent(string title, GenerateEvent[] ring, int capacity, int pos, int count)
        {
            if (count <= 0) return title + ": none.";
            StringBuilder sb = new StringBuilder(count > 16 ? 8192 : 4096);
            sb.Append(title).Append(" (oldest->newest): ");
            int startIndex = count == capacity ? pos : 0;
            for (int i = 0; i < count; i++)
            {
                if (i != 0) sb.Append("; ");
                GenerateEvent e = ring[(startIndex + i) % capacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",gen=").Append((e.GenerateUs / 1000.0).ToString("F2"))
                    .Append(",body=").Append((e.BodyApproxUs / 1000.0).ToString("F2"))
                    .Append(",find=").Append((e.FindUs / 1000.0).ToString("F2"))
                    .Append("[").Append(e.FindCalls).Append(" calls,max=").Append((e.FindMaxUs / 1000.0).ToString("F2")).Append("]")
                    .Append(",vfe=").Append((e.VfeUs / 1000.0).ToString("F2"))
                    .Append("[seen=").Append(e.VfeSeen).Append(",skip=").Append(e.VfeSkipped).Append("]")
                    .Append(",pf=").Append((e.PfUs / 1000.0).ToString("F2"))
                    .Append(",pawn=").Append(e.PawnDef).Append('[').Append(e.JobDef).Append(']')
                    .Append(",start=").Append(e.Start).Append(",dest=").Append(e.Destination)
                    .Append(",distSq=").Append(e.DistanceSq)
                    .Append(",peMode=").Append(e.PeMode);
            }
            return sb.ToString();
        }

        private static void PushRecent10(GenerateEvent e)
        {
            Recent10[recent10Pos] = e;
            recent10Pos = (recent10Pos + 1) % Recent10Capacity;
            if (recent10Count < Recent10Capacity) recent10Count++;
        }

        private static void PushRecent20(GenerateEvent e)
        {
            Recent20[recent20Pos] = e;
            recent20Pos = (recent20Pos + 1) % Recent20Capacity;
            if (recent20Count < Recent20Capacity) recent20Count++;
        }

        private static int DistanceSquared(IntVec3 a, IntVec3 b)
        {
            if (!a.IsValid || !b.IsValid) return -1;
            long dx = (long)a.x - b.x;
            long dz = (long)a.z - b.z;
            long value = dx * dx + dz * dz;
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static int SafeGameTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private static string SafePawnDef(Pawn pawn)
        {
            try { return pawn == null || pawn.def == null ? "<null>" : pawn.def.defName ?? "<unnamed>"; }
            catch { return "<error>"; }
        }

        private static string SafeJobDef(Pawn pawn)
        {
            try { return pawn == null || pawn.CurJobDef == null ? "none" : pawn.CurJobDef.defName ?? "<unnamed>"; }
            catch { return "<error>"; }
        }

        private static long ElapsedUs(long started)
        {
            long ticks = Stopwatch.GetTimestamp() - started;
            return ticks <= 0L ? 0L : TicksToUs(ticks);
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct GenerateEvent
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long GenerateUs;
            internal readonly long BodyApproxUs;
            internal readonly int FindCalls;
            internal readonly long FindUs;
            internal readonly long FindMaxUs;
            internal readonly long VfeUs;
            internal readonly long PfUs;
            internal readonly bool VfeSeen;
            internal readonly bool VfeSkipped;
            internal readonly string PawnDef;
            internal readonly string JobDef;
            internal readonly IntVec3 Start;
            internal readonly IntVec3 Destination;
            internal readonly int DistanceSq;
            internal readonly PathEndMode PeMode;

            internal GenerateEvent(long frame, int gameTick, long generateUs, long bodyApproxUs,
                int findCalls, long findUs, long findMaxUs, long vfeUs, long pfUs, bool vfeSeen, bool vfeSkipped,
                string pawnDef, string jobDef, IntVec3 start, IntVec3 destination, int distanceSq, PathEndMode peMode)
            {
                Frame = frame;
                GameTick = gameTick;
                GenerateUs = generateUs;
                BodyApproxUs = bodyApproxUs;
                FindCalls = findCalls;
                FindUs = findUs;
                FindMaxUs = findMaxUs;
                VfeUs = vfeUs;
                PfUs = pfUs;
                VfeSeen = vfeSeen;
                VfeSkipped = vfeSkipped;
                PawnDef = pawnDef;
                JobDef = jobDef;
                Start = start;
                Destination = destination;
                DistanceSq = distanceSq;
                PeMode = peMode;
            }
        }
    }
}
