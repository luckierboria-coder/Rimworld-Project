using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// v0.3 diagnostics-only slow DetermineNextJob correlation.
    /// Every DetermineNextJob gets one timestamp pair while the diagnostics mod is enabled.
    /// WorkGiver method timings are accumulated only while a DetermineNextJob call is active.
    /// Calls >=20ms are copied into a bounded ring with top contributing WorkGiver methods.
    /// No gameplay result/state is changed.
    /// </summary>
    internal static class DiagnosticsV03
    {
        private const long SlowDetermineUs = 20000L;
        private const int RecentCapacity = 32;
        private const int MaxMethodsPerDetermine = 96;
        private const int TopMethodsPerBurst = 12;

        private static readonly FieldInfo JobTrackerPawnField = AccessToolsCompat.Field(typeof(Pawn_JobTracker), "pawn");
        private static readonly SlowDetermineRecord[] Recent = new SlowDetermineRecord[RecentCapacity];

        [ThreadStatic] private static DetermineContext current;
        private static int recentPos;
        private static int recentCount;
        private static long determines;
        private static long slowDetermines;
        private static long workGiverCallsInsideDetermine;
        private static long workGiverUsInsideDetermine;
        private static long contextReentry;
        private static long failures;

        internal static bool InDetermine { get { return current != null; } }

        internal static void BeginDetermine(Pawn_JobTracker tracker)
        {
            if (current != null)
            {
                contextReentry++;
                current = null;
            }

            Pawn pawn = null;
            try
            {
                if (tracker != null && JobTrackerPawnField != null)
                    pawn = JobTrackerPawnField.GetValue(tracker) as Pawn;
            }
            catch { failures++; }

            DetermineContext ctx = new DetermineContext();
            ctx.Started = Stopwatch.GetTimestamp();
            ctx.Pawn = pawn;
            ctx.Methods = new Dictionary<string, MethodAccum>(StringComparer.Ordinal);
            current = ctx;
            determines++;
        }

        internal static void RecordWorkGiver(object instance, MethodBase method, long started)
        {
            DetermineContext ctx = current;
            if (ctx == null || started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = elapsed * 1000000L / Stopwatch.Frequency;
            string type = instance == null
                ? (method == null || method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName)
                : instance.GetType().FullName;
            if (string.IsNullOrEmpty(type)) type = "<unknown>";
            string name = type + "." + (method == null ? "<method>" : method.Name);

            MethodAccum a;
            if (!ctx.Methods.TryGetValue(name, out a))
            {
                if (ctx.Methods.Count >= MaxMethodsPerDetermine) name = "<other>";
                if (!ctx.Methods.TryGetValue(name, out a)) a = new MethodAccum();
            }
            a.Calls++;
            a.TotalUs += us;
            if (us > a.MaxUs) a.MaxUs = us;
            ctx.Methods[name] = a;
            workGiverCallsInsideDetermine++;
            workGiverUsInsideDetermine += us;
        }

        internal static void EndDetermine(Pawn_JobTracker tracker, ThinkResult result)
        {
            DetermineContext ctx = current;
            current = null;
            if (ctx == null || ctx.Started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - ctx.Started;
            if (elapsed <= 0L) return;
            long totalUs = elapsed * 1000000L / Stopwatch.Frequency;
            if (totalUs < SlowDetermineUs) return;
            slowDetermines++;

            int tick = -1;
            try { tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame; } catch { }
            string pawn = PawnText(ctx.Pawn);
            string resultJob = "<none>";
            string source = "<none>";
            try
            {
                if (result.Job != null && result.Job.def != null) resultJob = result.Job.def.defName;
                if (result.SourceNode != null) source = result.SourceNode.GetType().FullName;
            }
            catch { failures++; }

            string top = string.Join(" | ", ctx.Methods
                .OrderByDescending(kv => kv.Value.TotalUs)
                .Take(TopMethodsPerBurst)
                .Select(kv => kv.Key + "[calls=" + kv.Value.Calls + ",totalMs=" + (kv.Value.TotalUs / 1000.0).ToString("F2") + ",maxMs=" + (kv.Value.MaxUs / 1000.0).ToString("F2") + "]")
                .ToArray());
            Recent[recentPos] = new SlowDetermineRecord(tick, pawn, totalUs, resultJob, source, top);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.Append("SlowDNJCorrelation: determines=").Append(determines)
              .Append(", slow>=20ms=").Append(slowDetermines)
              .Append(", workGiverCalls=").Append(workGiverCallsInsideDetermine)
              .Append(", workGiverTimeMs=").Append((workGiverUsInsideDetermine / 1000.0).ToString("F1"))
              .Append(", contextReentry=").Append(contextReentry)
              .Append(", failures=").Append(failures).AppendLine();
            sb.Append("RecentSlowDNJ=");
            if (recentCount == 0)
            {
                sb.AppendLine("none");
                return sb.ToString();
            }
            sb.AppendLine();
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                SlowDetermineRecord e = Recent[(start + i) % RecentCapacity];
                sb.Append(" - tick=").Append(e.Tick)
                  .Append(", pawn=").Append(e.Pawn)
                  .Append(", totalMs=").Append((e.TotalUs / 1000.0).ToString("F2"))
                  .Append(", result=").Append(e.ResultJob)
                  .Append(", source=").Append(e.Source)
                  .Append(", top=").Append(string.IsNullOrEmpty(e.TopMethods) ? "none" : e.TopMethods)
                  .AppendLine();
            }
            return sb.ToString();
        }

        internal static void Reset()
        {
            Array.Clear(Recent, 0, Recent.Length);
            recentPos = recentCount = 0;
            determines = slowDetermines = workGiverCallsInsideDetermine = workGiverUsInsideDetermine = contextReentry = failures = 0L;
            current = null;
        }

        private static string PawnText(Pawn pawn)
        {
            if (pawn == null) return "<null>";
            string label = null;
            try { label = pawn.LabelShortCap; } catch { }
            if (string.IsNullOrEmpty(label)) label = pawn.def == null ? "Pawn" : pawn.def.defName;
            return label + "#" + pawn.thingIDNumber;
        }

        private sealed class DetermineContext
        {
            internal long Started;
            internal Pawn Pawn;
            internal Dictionary<string, MethodAccum> Methods;
        }

        private struct MethodAccum
        {
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
        }

        private struct SlowDetermineRecord
        {
            internal readonly int Tick;
            internal readonly string Pawn;
            internal readonly long TotalUs;
            internal readonly string ResultJob;
            internal readonly string Source;
            internal readonly string TopMethods;

            internal SlowDetermineRecord(int tick, string pawn, long totalUs, string resultJob, string source, string topMethods)
            {
                Tick = tick;
                Pawn = pawn;
                TotalUs = totalUs;
                ResultJob = resultJob;
                Source = source;
                TopMethods = topMethods;
            }
        }
    }
}
