using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Records only catastrophic Storyteller phases already timed by T1. No extra hot-path timer.
    /// Harmony census is generated on demand only.
    /// </summary>
    internal static class StorytellerCatastrophic093T15
    {
        private const long ThresholdUs = 100000L;
        private const int RecentCapacity = 12;
        private static readonly Entry[] Recent = new Entry[RecentCapacity];
        private static long events;
        private static long maxUs;
        private static int recentPos;
        private static int recentCount;

        internal static void Observe(long us)
        {
            if (us < ThresholdUs || !RimMTThreadGuard.IsMainThread) return;
            events++;
            if (us > maxUs) maxUs = us;
            StorytellerDeepAttribution093T18.ObserveCatastrophic(us);
            int tick = -1;
            try { if (Find.TickManager != null) tick = Find.TickManager.TicksGame; }
            catch { }
            Recent[recentPos] = new Entry(RimMTRuntime.MainThreadFrames, tick, us);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T15 Storyteller catastrophic tails >=100ms: events=").Append(events)
              .Append(", maxMs=").Append((maxUs / 1000.0).ToString("F2"));
            if (recentCount == 0) return sb.Append(", recent=none. T15 reuses T1 elapsed time; no extra Storyteller Stopwatch.").ToString();

            sb.Append(", recent(oldest->newest)=");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append(';');
                Entry e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.Tick)
                  .Append(",ms=").Append((e.Us / 1000.0).ToString("F2"));
            }
            sb.Append(". T15 reuses T1 elapsed time; no extra Storyteller Stopwatch.");
            return sb.ToString();
        }

        internal static string HarmonyCensus()
        {
            MethodBase target = AccessTools.Method(typeof(Storyteller), "StorytellerTick");
            if (target == null) return "T15 Storyteller Harmony census: target missing.";
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return "T15 Storyteller Harmony census: no patches.";

            StringBuilder sb = new StringBuilder(3072);
            int foreign = 0;
            Append(sb, "Prefix", info.Prefixes, ref foreign);
            Append(sb, "Postfix", info.Postfixes, ref foreign);
            Append(sb, "Transpiler", info.Transpilers, ref foreign);
            Append(sb, "Finalizer", info.Finalizers, ref foreign);
            sb.Insert(0, "T15 Storyteller Harmony census: foreignPatches=" + foreign + ". ");
            sb.Append(" Census is on-demand only; T15 does not wrap foreign Storyteller patches.");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string kind, IEnumerable<Patch> patches, ref int foreign)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                bool ours = patch.owner != null && patch.owner.StartsWith("allen.rimmt", StringComparison.Ordinal);
                if (!ours) foreign++;
                MethodInfo method = patch.PatchMethod;
                sb.Append(kind).Append("[owner=").Append(patch.owner ?? "<null>")
                  .Append(",priority=").Append(patch.priority)
                  .Append(",method=").Append(method == null ? "<null>" : method.DeclaringType.FullName + "." + method.Name)
                  .Append(ours ? ",RimMT] " : ",FOREIGN] ");
            }
        }

        private struct Entry
        {
            internal readonly long Frame;
            internal readonly int Tick;
            internal readonly long Us;
            internal Entry(long frame, int tick, long us) { Frame = frame; Tick = tick; Us = us; }
        }
    }
}

