using System;
using System.Diagnostics;
using System.Text;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T10 diagnostic-only attribution for the two known foreign TryEnterNextPathCell postfixes.
    /// Active only inside T9/T2 deep Pather samples. It never changes path, terrain, graphics,
    /// fog, phasing, Harmony order, or any return/result value.
    /// </summary>
    internal static class TryEnterForeignAttribution093T10
    {
        private const int RecentCapacity = 16;
        private const long RecentThresholdUs = 5000L;
        private static readonly RecentTryEnter[] Recent = new RecentTryEnter[RecentCapacity];

        [ThreadStatic] private static bool enterActive;
        [ThreadStatic] private static Pawn enterPawn;
        [ThreadStatic] private static TerrainDef terrainBefore;
        [ThreadStatic] private static long pfUs;
        [ThreadStatic] private static long vfeUs;
        [ThreadStatic] private static int pfDepth;
        [ThreadStatic] private static int vfeDepth;
        [ThreadStatic] private static int terrainUpdatedCalls;
        [ThreadStatic] private static int graphicsDirtyCalls;
        [ThreadStatic] private static TerrainDef terrainUpdatedPrevious;
        [ThreadStatic] private static TerrainDef terrainUpdatedCurrent;
        [ThreadStatic] private static bool vfeFoggedKnown;
        [ThreadStatic] private static bool vfeFogged;
        [ThreadStatic] private static bool vfePhasingQueried;
        [ThreadStatic] private static bool vfePhasing;
        [ThreadStatic] private static int vfeFloodCalls;

        private static bool pfPatchFound;
        private static bool pfPatchInstrumented;
        private static bool vfePatchFound;
        private static bool vfePatchInstrumented;
        private static bool pfTerrainUpdatedInstrumented;
        private static bool pfSetGraphicsDirtyInstrumented;
        private static bool vfeIsPhasingInstrumented;
        private static bool vfeFloodInstrumented;

        private static long enters;
        private static long over5;
        private static long over10;
        private static long over20;
        private static long totalUs;
        private static long maxUs;
        private static long baseTotalUs;
        private static long baseMaxUs;
        private static long pfCalls;
        private static long pfTotalUs;
        private static long pfMaxUs;
        private static long vfeCalls;
        private static long vfeTotalUs;
        private static long vfeMaxUs;
        private static long terrainChangedEnters;
        private static long terrainUpdatedEnters;
        private static long graphicsDirtyEnters;
        private static long vfeFoggedEnters;
        private static long vfePhasingTrueEnters;
        private static long vfeFloodEnters;
        private static long negativeBaseClamp;
        private static int recentPos;
        private static int recentCount;

        internal static void SetInstallState(bool pfFound, bool pfTimed, bool vfeFound, bool vfeTimed,
            bool terrainUpdatedTimed, bool graphicsDirtyTimed, bool phasingTimed, bool floodTimed)
        {
            pfPatchFound = pfFound;
            pfPatchInstrumented = pfTimed;
            vfePatchFound = vfeFound;
            vfePatchInstrumented = vfeTimed;
            pfTerrainUpdatedInstrumented = terrainUpdatedTimed;
            pfSetGraphicsDirtyInstrumented = graphicsDirtyTimed;
            vfeIsPhasingInstrumented = phasingTimed;
            vfeFloodInstrumented = floodTimed;
        }

        internal static void BeginTryEnter(Pawn pawn, long started)
        {
            if (started == 0L || !PatherInternalAttribution093T9.Active || !RimMTThreadGuard.IsMainThread)
            {
                enterActive = false;
                return;
            }

            enterActive = true;
            enterPawn = pawn;
            terrainBefore = SafeTerrain(pawn);
            pfUs = 0L;
            vfeUs = 0L;
            pfDepth = 0;
            vfeDepth = 0;
            terrainUpdatedCalls = 0;
            graphicsDirtyCalls = 0;
            terrainUpdatedPrevious = null;
            terrainUpdatedCurrent = null;
            vfeFoggedKnown = false;
            vfeFogged = false;
            vfePhasingQueried = false;
            vfePhasing = false;
            vfeFloodCalls = 0;
        }

        internal static void EndTryEnter(Pawn pawn, long started)
        {
            if (!enterActive || started == 0L)
                return;

            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            long enterUs = elapsedTicks <= 0L ? 0L : TicksToUs(elapsedTicks);
            TerrainDef after = SafeTerrain(pawn ?? enterPawn);
            bool changed = !ReferenceEquals(terrainBefore, after);
            long approxBase = enterUs - pfUs - vfeUs;
            if (approxBase < 0L)
            {
                negativeBaseClamp++;
                approxBase = 0L;
            }

            enters++;
            totalUs += enterUs;
            baseTotalUs += approxBase;
            if (enterUs > maxUs) maxUs = enterUs;
            if (approxBase > baseMaxUs) baseMaxUs = approxBase;
            if (enterUs >= 5000L) over5++;
            if (enterUs >= 10000L) over10++;
            if (enterUs >= 20000L) over20++;
            if (changed) terrainChangedEnters++;
            if (terrainUpdatedCalls > 0) terrainUpdatedEnters++;
            if (graphicsDirtyCalls > 0) graphicsDirtyEnters++;
            if (vfeFoggedKnown && vfeFogged) vfeFoggedEnters++;
            if (vfePhasingQueried && vfePhasing) vfePhasingTrueEnters++;
            if (vfeFloodCalls > 0) vfeFloodEnters++;

            if (enterUs >= RecentThresholdUs)
            {
                int gameTick = -1;
                try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
                catch { }

                Pawn p = pawn ?? enterPawn;
                string pawnDef = p != null && p.def != null ? p.def.defName : "<null>";
                string jobDef = p != null && p.CurJob != null && p.CurJob.def != null ? p.CurJob.def.defName : "none";
                Recent[recentPos] = new RecentTryEnter(
                    RimMTRuntime.MainThreadFrames, gameTick, enterUs, approxBase, pfUs, vfeUs,
                    pawnDef, jobDef, terrainBefore == null ? "<null>" : terrainBefore.defName,
                    after == null ? "<null>" : after.defName, changed,
                    terrainUpdatedCalls, graphicsDirtyCalls,
                    terrainUpdatedPrevious == null ? "<null>" : terrainUpdatedPrevious.defName,
                    terrainUpdatedCurrent == null ? "<null>" : terrainUpdatedCurrent.defName,
                    vfeFoggedKnown, vfeFogged, vfePhasingQueried, vfePhasing, vfeFloodCalls);
                recentPos = (recentPos + 1) % RecentCapacity;
                if (recentCount < RecentCapacity) recentCount++;
            }

            enterActive = false;
            enterPawn = null;
            terrainBefore = null;
            pfDepth = 0;
            vfeDepth = 0;
        }

        internal static long BeginPathfindingFramework(Pawn pawn)
        {
            if (!enterActive || !RimMTThreadGuard.IsMainThread) return 0L;
            pfDepth++;
            pfCalls++;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndPathfindingFramework(long started)
        {
            if (started != 0L && enterActive)
            {
                long ticks = Stopwatch.GetTimestamp() - started;
                long us = ticks <= 0L ? 0L : TicksToUs(ticks);
                pfUs += us;
                pfTotalUs += us;
                if (us > pfMaxUs) pfMaxUs = us;
            }
            if (pfDepth > 0) pfDepth--;
        }

        internal static long BeginVfe(Pawn_PathFollower pather, Pawn pawn)
        {
            if (!enterActive || !RimMTThreadGuard.IsMainThread) return 0L;
            vfeDepth++;
            vfeCalls++;
            vfeFoggedKnown = false;
            try
            {
                if (pawn != null && pawn.Spawned && pawn.Map != null && pather != null && pather.nextCell.IsValid && pather.nextCell.InBounds(pawn.Map))
                {
                    vfeFogged = pather.nextCell.Fogged(pawn.Map);
                    vfeFoggedKnown = true;
                }
            }
            catch { vfeFoggedKnown = false; }
            return Stopwatch.GetTimestamp();
        }

        internal static void EndVfe(long started)
        {
            if (started != 0L && enterActive)
            {
                long ticks = Stopwatch.GetTimestamp() - started;
                long us = ticks <= 0L ? 0L : TicksToUs(ticks);
                vfeUs += us;
                vfeTotalUs += us;
                if (us > vfeMaxUs) vfeMaxUs = us;
            }
            if (vfeDepth > 0) vfeDepth--;
        }

        internal static void NoteTerrainUpdated(TerrainDef previous, TerrainDef current)
        {
            if (!enterActive || pfDepth <= 0) return;
            terrainUpdatedCalls++;
            terrainUpdatedPrevious = previous;
            terrainUpdatedCurrent = current;
        }

        internal static void NoteGraphicsDirty()
        {
            if (!enterActive || pfDepth <= 0) return;
            graphicsDirtyCalls++;
        }

        internal static void NotePhasingResult(bool result)
        {
            if (!enterActive || vfeDepth <= 0) return;
            vfePhasingQueried = true;
            vfePhasing = result;
        }

        internal static void NoteFloodUnfog()
        {
            if (!enterActive || vfeDepth <= 0) return;
            vfeFloodCalls++;
        }

        internal static string Summary()
        {
            double avg = enters == 0L ? 0.0 : totalUs / (double)enters;
            double baseAvg = enters == 0L ? 0.0 : baseTotalUs / (double)enters;
            double pfAvg = pfCalls == 0L ? 0.0 : pfTotalUs / (double)pfCalls;
            double vfeAvg = vfeCalls == 0L ? 0.0 : vfeTotalUs / (double)vfeCalls;
            return "T10 TryEnter foreign-postfix attribution: enters=" + enters +
                ", >5/10/20=" + over5 + "/" + over10 + "/" + over20 +
                ", avgUs=" + avg.ToString("F1") + ", maxMs=" + (maxUs / 1000.0).ToString("F2") +
                ", baseApprox[avgUs=" + baseAvg.ToString("F1") + ",maxMs=" + (baseMaxUs / 1000.0).ToString("F2") + "]" +
                ", PF[found=" + pfPatchFound + ",instrumented=" + pfPatchInstrumented + ",calls=" + pfCalls +
                ",avgUs=" + pfAvg.ToString("F1") + ",maxMs=" + (pfMaxUs / 1000.0).ToString("F2") +
                ",terrainChangedEnters=" + terrainChangedEnters + ",terrainUpdatedEnters=" + terrainUpdatedEnters +
                ",graphicsDirtyEnters=" + graphicsDirtyEnters + ",terrainUpdatedHook=" + pfTerrainUpdatedInstrumented +
                ",graphicsDirtyHook=" + pfSetGraphicsDirtyInstrumented + "]" +
                ", VFE[found=" + vfePatchFound + ",instrumented=" + vfePatchInstrumented + ",calls=" + vfeCalls +
                ",avgUs=" + vfeAvg.ToString("F1") + ",maxMs=" + (vfeMaxUs / 1000.0).ToString("F2") +
                ",foggedEnters=" + vfeFoggedEnters + ",phasingTrueEnters=" + vfePhasingTrueEnters +
                ",floodEnters=" + vfeFloodEnters + ",isPhasingHook=" + vfeIsPhasingInstrumented +
                ",floodHook=" + vfeFloodInstrumented + "]" +
                ", negativeBaseClamp=" + negativeBaseClamp +
                ". Diagnostic-only; baseApprox=whole TryEnter inclusive time minus directly measured PF/VFE postfix time; no Harmony owner order is changed.";
        }

        internal static string RecentSummary()
        {
            if (recentCount == 0) return "T10 recent TryEnterNextPathCell >=5ms: none.";
            StringBuilder sb = new StringBuilder(6144);
            sb.Append("T10 recent TryEnterNextPathCell >=5ms (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                RecentTryEnter e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",total=").Append((e.TotalUs / 1000.0).ToString("F2"))
                    .Append(",base=").Append((e.BaseUs / 1000.0).ToString("F2"))
                    .Append(",pf=").Append((e.PfUs / 1000.0).ToString("F2"))
                    .Append(",vfe=").Append((e.VfeUs / 1000.0).ToString("F2"))
                    .Append(",pawn=").Append(e.PawnDef).Append('[').Append(e.JobDef).Append(']')
                    .Append(",terrain=").Append(e.TerrainBefore).Append("->").Append(e.TerrainAfter)
                    .Append(",changed=").Append(e.TerrainChanged)
                    .Append(",pfTerrainUpdated=").Append(e.TerrainUpdatedCalls)
                    .Append('(').Append(e.HelperPrevious).Append("->").Append(e.HelperCurrent).Append(')')
                    .Append(",pfGraphicsDirty=").Append(e.GraphicsDirtyCalls)
                    .Append(",vfeFogged=").Append(e.VfeFoggedKnown ? e.VfeFogged.ToString() : "?")
                    .Append(",vfePhasing=").Append(e.VfePhasingQueried ? e.VfePhasing.ToString() : "?")
                    .Append(",vfeFlood=").Append(e.VfeFloodCalls);
            }
            return sb.ToString();
        }

        private static TerrainDef SafeTerrain(Pawn pawn)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map)) return null;
                return pawn.Position.GetTerrain(pawn.Map);
            }
            catch { return null; }
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct RecentTryEnter
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TotalUs;
            internal readonly long BaseUs;
            internal readonly long PfUs;
            internal readonly long VfeUs;
            internal readonly string PawnDef;
            internal readonly string JobDef;
            internal readonly string TerrainBefore;
            internal readonly string TerrainAfter;
            internal readonly bool TerrainChanged;
            internal readonly int TerrainUpdatedCalls;
            internal readonly int GraphicsDirtyCalls;
            internal readonly string HelperPrevious;
            internal readonly string HelperCurrent;
            internal readonly bool VfeFoggedKnown;
            internal readonly bool VfeFogged;
            internal readonly bool VfePhasingQueried;
            internal readonly bool VfePhasing;
            internal readonly int VfeFloodCalls;

            internal RecentTryEnter(long frame, int gameTick, long totalUs, long baseUs, long pfUs, long vfeUs,
                string pawnDef, string jobDef, string terrainBefore, string terrainAfter, bool terrainChanged,
                int terrainUpdatedCalls, int graphicsDirtyCalls, string helperPrevious, string helperCurrent,
                bool vfeFoggedKnown, bool vfeFogged, bool vfePhasingQueried, bool vfePhasing, int vfeFloodCalls)
            {
                Frame = frame; GameTick = gameTick; TotalUs = totalUs; BaseUs = baseUs; PfUs = pfUs; VfeUs = vfeUs;
                PawnDef = pawnDef; JobDef = jobDef; TerrainBefore = terrainBefore; TerrainAfter = terrainAfter;
                TerrainChanged = terrainChanged; TerrainUpdatedCalls = terrainUpdatedCalls; GraphicsDirtyCalls = graphicsDirtyCalls;
                HelperPrevious = helperPrevious; HelperCurrent = helperCurrent; VfeFoggedKnown = vfeFoggedKnown;
                VfeFogged = vfeFogged; VfePhasingQueried = vfePhasingQueried; VfePhasing = vfePhasing;
                VfeFloodCalls = vfeFloodCalls;
            }
        }
    }
}
