using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal enum PatherInternalPhase093T9
    {
        WillCollideWithPawnAt = 0,
        TryEnterNextPathCell = 1,
        BuildingBlockingNextPathCell = 2,
        NextCellDoorToWaitForOrManuallyOpen = 3,
        NeedNewPath = 4,
        TrySetNewPath = 5,
        GenerateNewPath = 6,
        AtDestinationPosition = 7,
        SetupMoveIntoNextCell = 8,
        CostToMoveIntoCell = 9,
        CostToPayThisTick = 10,
        Count = 11
    }

    /// <summary>
    /// T9 diagnostic-only bounded attribution for Pawn_PathFollower internals.
    /// Stopwatch work is active only while the existing T2 deep-sample PatherTick is active.
    /// No path decision, state transition, reservation, collision result or job result is changed.
    /// </summary>
    internal static class PatherInternalAttribution093T9
    {
        private const int Count = (int)PatherInternalPhase093T9.Count;
        private const int RecentCapacity = 16;
        private const long RecentThresholdUs = 10000L;
        private static readonly string[] Names = new string[]
        {
            "WillCollideWithPawnAt", "TryEnterNextPathCell", "BuildingBlockingNextPathCell",
            "NextCellDoorToWaitForOrManuallyOpen", "NeedNewPath", "TrySetNewPath",
            "GenerateNewPath", "AtDestinationPosition", "SetupMoveIntoNextCell",
            "CostToMoveIntoCell", "CostToPayThisTick"
        };

        private static readonly long[] Calls = new long[Count];
        private static readonly long[] TotalUs = new long[Count];
        private static readonly long[] MaxUs = new long[Count];
        private static readonly long[] Over5 = new long[Count];
        private static readonly long[] Over10 = new long[Count];
        private static readonly long[] Over20 = new long[Count];
        private static readonly RecentPather[] Recent = new RecentPather[RecentCapacity];

        [ThreadStatic] private static bool active;
        [ThreadStatic] private static long[] currentUs;
        [ThreadStatic] private static int[] currentCalls;

        private static long deepPathers;
        private static long deepPatherOver10;
        private static long deepPatherOver20;
        private static long unresolvedOver20;
        private static long maxPatherUs;
        private static int recentPos;
        private static int recentCount;

        internal static bool Active { get { return active; } }

        internal static void BeginPather()
        {
            if (!TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread)
            {
                active = false;
                return;
            }

            if (currentUs == null) currentUs = new long[Count];
            else Array.Clear(currentUs, 0, currentUs.Length);
            if (currentCalls == null) currentCalls = new int[Count];
            else Array.Clear(currentCalls, 0, currentCalls.Length);
            active = true;
            deepPathers++;
        }

        internal static long BeginCall()
        {
            if (!active || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndCall(long started, PatherInternalPhase093T9 phase)
        {
            if (started == 0L || !active) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = TicksToUs(elapsed);
            int i = (int)phase;
            if (i < 0 || i >= Count) return;

            Calls[i]++;
            TotalUs[i] += us;
            currentUs[i] += us;
            currentCalls[i]++;
            if (us > MaxUs[i]) MaxUs[i] = us;
            if (us >= 5000L) Over5[i]++;
            if (us >= 10000L) Over10[i]++;
            if (us >= 20000L) Over20[i]++;
        }

        internal static void EndPather(long started)
        {
            if (!active)
                return;

            long elapsed = started <= 0L ? 0L : Stopwatch.GetTimestamp() - started;
            long patherUs = elapsed <= 0L ? 0L : TicksToUs(elapsed);
            active = false;

            if (patherUs > maxPatherUs) maxPatherUs = patherUs;
            if (patherUs >= 10000L) deepPatherOver10++;
            if (patherUs >= 20000L) deepPatherOver20++;

            long maxInternal = 0L;
            int maxPhase = -1;
            for (int i = 0; i < Count; i++)
            {
                if (currentUs[i] > maxInternal)
                {
                    maxInternal = currentUs[i];
                    maxPhase = i;
                }
            }
            if (patherUs >= 20000L && maxInternal < 5000L) unresolvedOver20++;
            if (patherUs < RecentThresholdUs) return;

            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }

            Recent[recentPos] = new RecentPather(
                RimMTRuntime.MainThreadFrames, gameTick, patherUs, maxInternal, maxPhase,
                currentUs[0], currentUs[1], currentUs[2], currentUs[3], currentUs[4], currentUs[5],
                currentUs[6], currentUs[7], currentUs[8], currentUs[9], currentUs[10]);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T9 Pather internal attribution: deepPathers=").Append(deepPathers)
                .Append(", >10ms=").Append(deepPatherOver10)
                .Append(", >20ms=").Append(deepPatherOver20)
                .Append(", >20msUnresolvedInternal<5ms=").Append(unresolvedOver20)
                .Append(", maxPatherMs=").Append((maxPatherUs / 1000.0).ToString("F2"))
                .Append(". Inclusive method stats: ");
            for (int i = 0; i < Count; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = Calls[i];
                double avg = calls == 0L ? 0.0 : TotalUs[i] / (double)calls;
                sb.Append(Names[i]).Append("(calls=").Append(calls)
                    .Append(",avgUs=").Append(avg.ToString("F1"))
                    .Append(",>5/10/20=").Append(Over5[i]).Append('/').Append(Over10[i]).Append('/').Append(Over20[i])
                    .Append(",maxMs=").Append((MaxUs[i] / 1000.0).ToString("F2")).Append(')');
            }
            return sb.ToString();
        }

        internal static string RecentSummary()
        {
            if (recentCount == 0) return "T9 recent deep PatherTick >=10ms: none.";
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T9 recent deep PatherTick >=10ms (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                RecentPather e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",pather=").Append((e.PatherUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",top=").Append(e.MaxPhase >= 0 && e.MaxPhase < Names.Length ? Names[e.MaxPhase] : "none")
                    .Append(':').Append((e.MaxInternalUs / 1000.0).ToString("F2"))
                    .Append(",collide=").Append((e.CollideUs / 1000.0).ToString("F2"))
                    .Append(",enter=").Append((e.EnterUs / 1000.0).ToString("F2"))
                    .Append(",need=").Append((e.NeedUs / 1000.0).ToString("F2"))
                    .Append(",set=").Append((e.SetUs / 1000.0).ToString("F2"))
                    .Append(",gen=").Append((e.GenerateUs / 1000.0).ToString("F2"))
                    .Append(",setup=").Append((e.SetupUs / 1000.0).ToString("F2"))
                    .Append(",costMove=").Append((e.CostMoveUs / 1000.0).ToString("F2"))
                    .Append(",costPay=").Append((e.CostPayUs / 1000.0).ToString("F2"))
                    .Append(",dest=").Append((e.DestUs / 1000.0).ToString("F2"))
                    .Append(",door=").Append((e.DoorUs / 1000.0).ToString("F2"))
                    .Append(",block=").Append((e.BlockUs / 1000.0).ToString("F2"));
            }
            return sb.ToString();
        }

        internal static string HarmonyCensus()
        {
            try
            {
                StringBuilder sb = new StringBuilder(3072);
                int targets = 0;
                int patchedTargets = 0;
                int foreignPatches = 0;
                MethodBase[] methods = TargetMethods();
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodBase method = methods[i];
                    if (method == null) continue;
                    targets++;
                    Patches info = Harmony.GetPatchInfo(method);
                    int count = info == null ? 0 : info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count;
                    if (count == 0) continue;
                    patchedTargets++;
                    AppendForeign(sb, Names[i], "Prefix", info.Prefixes, ref foreignPatches);
                    AppendForeign(sb, Names[i], "Postfix", info.Postfixes, ref foreignPatches);
                    AppendForeign(sb, Names[i], "Transpiler", info.Transpilers, ref foreignPatches);
                    AppendForeign(sb, Names[i], "Finalizer", info.Finalizers, ref foreignPatches);
                }
                return "T9 Pather Harmony census: targets=" + targets + ", patchedTargets=" + patchedTargets +
                       ", foreignPatches=" + foreignPatches + (sb.Length == 0 ? ". foreign=none" : ". foreign=" + sb.ToString());
            }
            catch (Exception ex)
            {
                return "T9 Pather Harmony census failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static MethodBase[] TargetMethods()
        {
            Type t = typeof(Pawn_PathFollower);
            return new MethodBase[]
            {
                AccessTools.Method(t, "WillCollideWithPawnAt", new Type[] { typeof(IntVec3) }),
                AccessTools.Method(t, "TryEnterNextPathCell"),
                AccessTools.Method(t, "BuildingBlockingNextPathCell"),
                AccessTools.Method(t, "NextCellDoorToWaitForOrManuallyOpen"),
                AccessTools.Method(t, "NeedNewPath"),
                AccessTools.Method(t, "TrySetNewPath"),
                AccessTools.Method(t, "GenerateNewPath"),
                AccessTools.Method(t, "AtDestinationPosition"),
                AccessTools.Method(t, "SetupMoveIntoNextCell"),
                AccessTools.Method(t, "CostToMoveIntoCell", new Type[] { typeof(IntVec3) }),
                AccessTools.Method(t, "CostToPayThisTick")
            };
        }

        private static void AppendForeign(StringBuilder sb, string target, string kind, System.Collections.Generic.IEnumerable<Patch> patches, ref int foreignCount)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                foreignCount++;
                if (sb.Length != 0) sb.Append("; ");
                MethodInfo pm = patch.PatchMethod;
                sb.Append(target).Append('/').Append(kind)
                    .Append(" owner=").Append(patch.owner)
                    .Append(" priority=").Append(patch.priority)
                    .Append(" method=").Append(pm == null ? "<unknown>" : ((pm.DeclaringType == null ? "<unknown>" : pm.DeclaringType.FullName) + "." + pm.Name));
            }
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct RecentPather
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long PatherUs;
            internal readonly long MaxInternalUs;
            internal readonly int MaxPhase;
            internal readonly long CollideUs;
            internal readonly long EnterUs;
            internal readonly long BlockUs;
            internal readonly long DoorUs;
            internal readonly long NeedUs;
            internal readonly long SetUs;
            internal readonly long GenerateUs;
            internal readonly long DestUs;
            internal readonly long SetupUs;
            internal readonly long CostMoveUs;
            internal readonly long CostPayUs;

            internal RecentPather(long frame, int gameTick, long patherUs, long maxInternalUs, int maxPhase,
                long collideUs, long enterUs, long blockUs, long doorUs, long needUs, long setUs,
                long generateUs, long destUs, long setupUs, long costMoveUs, long costPayUs)
            {
                Frame = frame; GameTick = gameTick; PatherUs = patherUs; MaxInternalUs = maxInternalUs; MaxPhase = maxPhase;
                CollideUs = collideUs; EnterUs = enterUs; BlockUs = blockUs; DoorUs = doorUs; NeedUs = needUs; SetUs = setUs;
                GenerateUs = generateUs; DestUs = destUs; SetupUs = setupUs; CostMoveUs = costMoveUs; CostPayUs = costPayUs;
            }
        }
    }
}
