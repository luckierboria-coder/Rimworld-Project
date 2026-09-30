using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Measurement-only census for stalls outside DoSingleTick. It separates time spent
    /// inside Root_Play.Update from the wall-clock gap between consecutive updates.
    /// No gameplay result is changed and no worker work is scheduled or awaited.
    /// </summary>
    internal static class RootFrameStallCensus093T34C8
    {
        internal const string FeatureId = "diagnostics.rootFrameStalls";
        private const long RootThresholdUs = 250000L;
        private const long GapThresholdUs = 1000000L;
        private const int RecentCapacity = 32;

        private static readonly object Sync = new object();
        private static readonly StallRecord[] Recent = new StallRecord[RecentCapacity];
        private static MethodBase target;
        private static bool installed;
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
                    prefix: new HarmonyMethod(typeof(RootFrameStallCensus093T34C8), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(RootFrameStallCensus093T34C8), nameof(Postfix)) { priority = Priority.Last });
                installed = true;
            }
            catch (Exception ex)
            {
                installFailures++;
                Log.Warning("[RimMT] T34-C.8 Root frame stall census failed closed: " + ex.GetType().Name + ": " + ex.Message);
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

            long previousExit = lastExitTimestamp;
            if (previousExit != 0L)
            {
                long gapUs = ToMicroseconds(now - previousExit);
                if (gapUs >= GapThresholdUs)
                {
                    int d0 = Math.Max(0, gc0 - lastExitGc0);
                    int d1 = Math.Max(0, gc1 - lastExitGc1);
                    int d2 = Math.Max(0, gc2 - lastExitGc2);
                    Record("OutsideRoot", gapUs, lastExitTick, tick, focused, paused, d0, d1, d2);
                    outsideGaps++;
                    if (gapUs > maxGapUs) maxGapUs = gapUs;
                }
            }

            __state = new FrameState(now, tick, focused, paused, gc0, gc1, gc2);
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
                Record("RootUpdate", elapsedUs, __state.Tick, tick, __state.Focused, __state.Paused,
                    Math.Max(0, gc0 - __state.Gc0), Math.Max(0, gc1 - __state.Gc1), Math.Max(0, gc2 - __state.Gc2));
                rootStalls++;
                if (elapsedUs > maxRootUs) maxRootUs = elapsedUs;
            }

            lastExitTimestamp = now;
            lastExitTick = tick;
            lastExitGc0 = gc0;
            lastExitGc1 = gc1;
            lastExitGc2 = gc2;
        }

        private static void Record(string kind, long durationUs, int tickBefore, int tickAfter,
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
            StallRecord row = new StallRecord(kind, durationUs, tickBefore, tickAfter,
                RimMTRuntime.MainThreadFrames, Current.ProgramState.ToString(), focused, paused,
                gc0, gc1, gc2, memory, DateTime.UtcNow.Ticks);
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
            sb.Append("T34-C.8 Root-frame stall census: installed=").Append(installed)
              .Append(", updateCalls=").Append(updateCalls)
              .Append(", stalls[root/outside]=").Append(rootStalls).Append('/').Append(outsideGaps)
              .Append(", >=1/5/10s=").Append(over1s).Append('/').Append(over5s).Append('/').Append(over10s)
              .Append(", max[root/gap]Ms=").Append((maxRootUs / 1000.0).ToString("F2")).Append('/').Append((maxGapUs / 1000.0).ToString("F2"))
              .Append(", focus[yes/no]=").Append(focusedStalls).Append('/').Append(unfocusedStalls)
              .Append(", paused=").Append(pausedStalls)
              .Append(", gcAssociated=").Append(gcAssociated)
              .Append(", installFailures=").Append(installFailures)
              .Append(". RootUpdate includes Root_Play.Update plus Harmony work inside this first/last envelope; OutsideRoot is wall time between consecutive updates and may include Unity rendering, resource unload, native plugins, OS scheduling, focus loss or an intentional pause.");
            sb.AppendLine();
            sb.Append("T34-C.8 Root_Play.Update patches: ").Append(PatchSummary()).AppendLine();
            sb.Append("T34-C.8 recent stalls: ").Append(RecentSummary());
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

        private static string PatchSummary()
        {
            if (target == null) return "target-missing";
            try
            {
                Patches patches = Harmony.GetPatchInfo(target);
                if (patches == null) return "none";
                List<string> rows = new List<string>();
                AddPatches(rows, "P", patches.Prefixes);
                AddPatches(rows, "Q", patches.Postfixes);
                AddPatches(rows, "T", patches.Transpilers);
                AddPatches(rows, "F", patches.Finalizers);
                return rows.Count == 0 ? "none" : string.Join(" ", rows.ToArray());
            }
            catch (Exception ex) { return "audit-failed:" + ex.GetType().Name; }
        }

        private static void AddPatches(List<string> rows, string kind, IList<Patch> patches)
        {
            if (patches == null) return;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                string owner = p == null || string.IsNullOrEmpty(p.owner) ? "<unknown>" : p.owner;
                string method = p == null || p.PatchMethod == null ? "<null>" :
                    (p.PatchMethod.DeclaringType == null ? "<type>" : p.PatchMethod.DeclaringType.FullName) + "." + p.PatchMethod.Name;
                rows.Add(kind + "[" + owner + ",pri=" + (p == null ? 0 : p.priority) + "," + method + "]");
            }
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
            internal readonly bool Focused;
            internal readonly bool Paused;
            internal readonly int Gc0;
            internal readonly int Gc1;
            internal readonly int Gc2;

            internal FrameState(long started, int tick, bool focused, bool paused, int gc0, int gc1, int gc2)
            {
                Started = started;
                Tick = tick;
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
            internal readonly long UtcTicks;

            internal StallRecord(string kind, long durationUs, int tickBefore, int tickAfter, long frame,
                string programState, bool focused, bool paused, int gc0, int gc1, int gc2, long managedBytes, long utcTicks)
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
                UtcTicks = utcTicks;
            }
        }
    }
}
