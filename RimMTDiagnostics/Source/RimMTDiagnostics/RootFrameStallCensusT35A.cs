using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// Diagnostics-only Root_Play.Update / outside-root stall census.
    /// This was removed from the RimMT production assembly in T35-A.
    /// </summary>
    internal static class RootFrameStallCensusT35A
    {
        private const long RootThresholdUs = 250000L;
        private const long GapThresholdUs = 1000000L;
        private const int RecentCapacity = 32;

        private static readonly object Sync = new object();
        private static readonly StallRecord[] Recent = new StallRecord[RecentCapacity];
        private static MethodBase target;
        private static bool installed;
        private static long frameSequence;
        private static long lastExitTimestamp;
        private static int lastExitTick = -1;
        private static int lastExitGc0;
        private static int lastExitGc1;
        private static int lastExitGc2;
        private static long updateCalls;
        private static long rootStalls;
        private static long outsideGaps;
        private static long over1s;
        private static long over5s;
        private static long over10s;
        private static long maxRootUs;
        private static long maxGapUs;
        private static long gcAssociated;
        private static long focusedStalls;
        private static long unfocusedStalls;
        private static long pausedStalls;
        private static int recentCount;
        private static int recentPos;
        private static long installFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null || installed) return;
            try
            {
                target = AccessTools.Method(typeof(Root_Play), "Update");
                if (target == null)
                {
                    installFailures++;
                    return;
                }

                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(RootFrameStallCensusT35A), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(RootFrameStallCensusT35A), nameof(Postfix)) { priority = Priority.Last });
                installed = true;
            }
            catch (Exception ex)
            {
                installFailures++;
                Log.Warning("[RimMT Diagnostics] T35-A Root frame stall census failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(ref FrameState __state)
        {
            long now = Stopwatch.GetTimestamp();
            int tick = ReadTick();
            bool focused = ReadFocused();
            bool paused = ReadPaused();
            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            long frame = ++frameSequence;

            long previousExit = lastExitTimestamp;
            if (previousExit != 0L)
            {
                long gapUs = ToMicroseconds(now - previousExit);
                if (gapUs >= GapThresholdUs)
                {
                    Record("OutsideRoot", gapUs, lastExitTick, tick, frame, focused, paused,
                        Math.Max(0, gc0 - lastExitGc0),
                        Math.Max(0, gc1 - lastExitGc1),
                        Math.Max(0, gc2 - lastExitGc2));
                    outsideGaps++;
                    if (gapUs > maxGapUs) maxGapUs = gapUs;
                }
            }

            __state = new FrameState(now, tick, frame, focused, paused, gc0, gc1, gc2);
        }

        public static void Postfix(FrameState __state)
        {
            long now = Stopwatch.GetTimestamp();
            updateCalls++;
            long elapsedUs = __state.Started == 0L ? 0L : ToMicroseconds(now - __state.Started);
            int tick = ReadTick();
            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);

            if (elapsedUs >= RootThresholdUs)
            {
                Record("RootUpdate", elapsedUs, __state.Tick, tick, __state.Frame,
                    __state.Focused, __state.Paused,
                    Math.Max(0, gc0 - __state.Gc0),
                    Math.Max(0, gc1 - __state.Gc1),
                    Math.Max(0, gc2 - __state.Gc2));
                rootStalls++;
                if (elapsedUs > maxRootUs) maxRootUs = elapsedUs;
            }

            lastExitTimestamp = now;
            lastExitTick = tick;
            lastExitGc0 = gc0;
            lastExitGc1 = gc1;
            lastExitGc2 = gc2;
        }

        private static void Record(string kind, long durationUs, int tickBefore, int tickAfter, long frame,
            bool focused, bool paused, int gc0, int gc1, int gc2)
        {
            if (durationUs >= 1000000L) over1s++;
            if (durationUs >= 5000000L) over5s++;
            if (durationUs >= 10000000L) over10s++;
            if (gc0 != 0 || gc1 != 0 || gc2 != 0) gcAssociated++;
            if (focused) focusedStalls++; else unfocusedStalls++;
            if (paused) pausedStalls++;

            long memory = 0L;
            try { memory = GC.GetTotalMemory(false); } catch { }

            StallRecord row = new StallRecord(kind, durationUs, tickBefore, tickAfter, frame,
                Current.ProgramState.ToString(), focused, paused, gc0, gc1, gc2, memory);
            lock (Sync)
            {
                Recent[recentPos] = row;
                recentPos = (recentPos + 1) % RecentCapacity;
                if (recentCount < RecentCapacity) recentCount++;
            }
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.Append("T35-A Diagnostics Root-frame stall census: installed=").Append(installed)
              .Append(", updateCalls=").Append(updateCalls)
              .Append(", stalls[root/outside]=").Append(rootStalls).Append('/').Append(outsideGaps)
              .Append(", >=1/5/10s=").Append(over1s).Append('/').Append(over5s).Append('/').Append(over10s)
              .Append(", max[root/gap]Ms=").Append((maxRootUs / 1000.0).ToString("F2")).Append('/')
              .Append((maxGapUs / 1000.0).ToString("F2"))
              .Append(", focus[yes/no]=").Append(focusedStalls).Append('/').Append(unfocusedStalls)
              .Append(", paused=").Append(pausedStalls)
              .Append(", gcAssociated=").Append(gcAssociated)
              .Append(", installFailures=").Append(installFailures)
              .Append(". Diagnostics-only; production RimMT no longer patches Root_Play.Update for this census.")
              .AppendLine();
            sb.Append("Recent root stalls: ").Append(RecentSummary()).AppendLine();
            return sb.ToString();
        }

        private static string RecentSummary()
        {
            List<string> rows = new List<string>();
            lock (Sync)
            {
                int start = (recentPos - recentCount + RecentCapacity) % RecentCapacity;
                for (int i = 0; i < recentCount; i++)
                {
                    StallRecord r = Recent[(start + i) % RecentCapacity];
                    rows.Add(r.Kind + "[ms=" + (r.DurationUs / 1000.0).ToString("F2") +
                        ",tick=" + r.TickBefore + "->" + r.TickAfter +
                        ",frame=" + r.Frame + ",state=" + r.ProgramState +
                        ",focused=" + r.Focused + ",paused=" + r.Paused +
                        ",gc=" + r.Gc0 + "/" + r.Gc1 + "/" + r.Gc2 +
                        ",managedMB=" + (r.ManagedBytes / 1048576.0).ToString("F1") + "]");
                }
            }
            return rows.Count == 0 ? "none" : string.Join("; ", rows.ToArray());
        }

        private static long ToMicroseconds(long ticks)
        {
            if (ticks <= 0L) return 0L;
            return ticks * 1000000L / Stopwatch.Frequency;
        }

        private static int ReadTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private static bool ReadPaused()
        {
            try { return Find.TickManager != null && Find.TickManager.Paused; }
            catch { return false; }
        }

        private static bool ReadFocused()
        {
            try { return Application.isFocused; }
            catch { return true; }
        }

        public struct FrameState
        {
            internal readonly long Started;
            internal readonly int Tick;
            internal readonly long Frame;
            internal readonly bool Focused;
            internal readonly bool Paused;
            internal readonly int Gc0;
            internal readonly int Gc1;
            internal readonly int Gc2;

            internal FrameState(long started, int tick, long frame, bool focused, bool paused, int gc0, int gc1, int gc2)
            {
                Started = started;
                Tick = tick;
                Frame = frame;
                Focused = focused;
                Paused = paused;
                Gc0 = gc0;
                Gc1 = gc1;
                Gc2 = gc2;
            }
        }

        private struct StallRecord
        {
            internal readonly string Kind;
            internal readonly long DurationUs;
            internal readonly int TickBefore;
            internal readonly int TickAfter;
            internal readonly long Frame;
            internal readonly string ProgramState;
            internal readonly bool Focused;
            internal readonly bool Paused;
            internal readonly int Gc0;
            internal readonly int Gc1;
            internal readonly int Gc2;
            internal readonly long ManagedBytes;

            internal StallRecord(string kind, long durationUs, int tickBefore, int tickAfter, long frame,
                string programState, bool focused, bool paused, int gc0, int gc1, int gc2, long managedBytes)
            {
                Kind = kind;
                DurationUs = durationUs;
                TickBefore = tickBefore;
                TickAfter = tickAfter;
                Frame = frame;
                ProgramState = programState;
                Focused = focused;
                Paused = paused;
                Gc0 = gc0;
                Gc1 = gc1;
                Gc2 = gc2;
                ManagedBytes = managedBytes;
            }
        }
    }
}
