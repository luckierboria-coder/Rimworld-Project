$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T25 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T25 Job Decision Root + Async Candidate Plan
#
# Goals:
# 1) retain the T24.1 generic-Def safety boundary and all T20-T24 production behavior;
# 2) add a small, permanently installed Job Decision root attribution layer which performs
#    Stopwatch work only while T2's existing bounded deep window is active;
# 3) activate the already-authored V0.4.18.2 async candidate-plan path for static large Thing
#    lists. Main thread captures Thing tokens + primitive x/z only; workers never dereference
#    Verse objects, never commit gameplay state, and the main thread never waits;
# 4) consume a worker plan only after exact main-thread membership + position revalidation;
# 5) do not parallelize Pawn.Tick, DetermineNextJob, Job creation, reservations or validators.

# Keep the first worker-authoritative surface deliberately bounded. Current runtime evidence puts
# useful JobGiver sources in the low hundreds; avoiding multi-thousand member validation protects
# the main-thread tail while we collect T25 hit/stale/capture evidence.
$asyncPath = 'RimMT/Source/RimMT/AI/AsyncJobCandidatePlan04182.cs'
$async = Get-Content $asyncPath -Raw
$async = Replace-OrThrow $async `
    'private const int MaxSourceCount = 8192;' `
    'private const int MaxSourceCount = 1024;' `
    'bound async candidate-plan source count'
Set-Content $asyncPath $async -Encoding UTF8

# -----------------------------------------------------------------------------
# T25-A: bounded Job Decision root attribution.
# No dynamic Harmony burst: six fixed decision boundaries only, and Stopwatch is entered only
# while the already-existing T2 DeepActive window is true.
# -----------------------------------------------------------------------------
$diagPath = 'RimMT/Source/RimMT/Diagnostics/JobDecisionRootAttribution093T25.cs'
$diag = @'
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal enum JobDecisionChild093T25
    {
        ThinkPriority = 0,
        ThinkConditional = 1,
        ThinkSubtree = 2,
        ThinkJobGiver = 3,
        JobGiverRoot = 4,
        WorkGiver = 5,
        Count = 6
    }

    /// <summary>
    /// T25 attributes the root of slow DetermineNextJob decisions without another T15-style
    /// profiler burst. Harmony hooks are fixed at install time; ordinary ticks pay only a guard.
    /// Child timings are inclusive and can overlap through nested ThinkNode calls.
    /// </summary>
    internal static class JobDecisionRootAttribution093T25
    {
        private const int Kinds = (int)JobDecisionChild093T25.Count;
        private const int RecentCapacity = 16;
        private static readonly string[] Names = new string[]
        {
            "ThinkPriority", "ThinkConditional", "ThinkSubtree", "ThinkJobGiver", "JobGiverRoot", "WorkGiver"
        };
        private static readonly long[] Calls = new long[Kinds];
        private static readonly long[] TotalTicks = new long[Kinds];
        private static readonly long[] MaxTicks = new long[Kinds];
        private static readonly long[] Over5 = new long[Kinds];
        private static readonly long[] Over20 = new long[Kinds];
        private static readonly object RecentLock = new object();
        private static readonly string[] Recent = new string[RecentCapacity];
        private static readonly System.Collections.Generic.Dictionary<MethodBase, JobDecisionChild093T25> ChildKinds =
            new System.Collections.Generic.Dictionary<MethodBase, JobDecisionChild093T25>();
        private static readonly System.Collections.Generic.HashSet<MethodBase> PatchedChildren =
            new System.Collections.Generic.HashSet<MethodBase>();

        [ThreadStatic] private static DecisionContext current;
        private static FieldInfo pawnField;
        private static int recentPos;
        private static int recentCount;
        private static int patched;
        private static int missing;
        private static int installFailures;
        private static long sampledDecisions;
        private static long tails20;
        private static long maxDecisionTicks;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            pawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

            MethodBase determine = AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob");
            if (determine == null)
            {
                missing++;
            }
            else
            {
                try
                {
                    harmony.Patch(determine,
                        prefix: new HarmonyMethod(typeof(JobDecisionRootAttribution093T25), nameof(DecisionPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(JobDecisionRootAttribution093T25), nameof(DecisionPostfix)) { priority = Priority.Last },
                        finalizer: new HarmonyMethod(typeof(JobDecisionRootAttribution093T25), nameof(DecisionFinalizer)) { priority = Priority.Last });
                    patched++;
                }
                catch { installFailures++; }
            }

            Type[] sig = new Type[] { typeof(Pawn), typeof(JobIssueParams) };
            PatchChild(harmony, AccessTools.TypeByName("Verse.AI.ThinkNode_Priority"), sig, JobDecisionChild093T25.ThinkPriority);
            PatchChild(harmony, AccessTools.TypeByName("Verse.AI.ThinkNode_Conditional"), sig, JobDecisionChild093T25.ThinkConditional);
            PatchChild(harmony, AccessTools.TypeByName("Verse.AI.ThinkNode_Subtree"), sig, JobDecisionChild093T25.ThinkSubtree);
            PatchChild(harmony, AccessTools.TypeByName("Verse.AI.ThinkNode_JobGiver"), sig, JobDecisionChild093T25.ThinkJobGiver);
            PatchChild(harmony, AccessTools.TypeByName("Verse.AI.JobGiver"), sig, JobDecisionChild093T25.JobGiverRoot);
            PatchChild(harmony, typeof(JobGiver_Work), sig, JobDecisionChild093T25.WorkGiver);

            Log.Message("[RimMT] T25 Job Decision root attribution installed: patched=" + patched +
                ", missing=" + missing + ", failures=" + installFailures +
                ". Fixed whitelist only; Stopwatch work is gated by T2 DeepActive; measurement-only.");
        }

        private static void PatchChild(Harmony harmony, Type type, Type[] sig, JobDecisionChild093T25 kind)
        {
            if (type == null) { missing++; return; }
            MethodBase method = AccessTools.Method(type, "TryIssueJobPackage", sig);
            if (method == null) { missing++; return; }
            if (!PatchedChildren.Add(method)) return;
            try
            {
                ChildKinds[method] = kind;
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(JobDecisionRootAttribution093T25), nameof(ChildPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(JobDecisionRootAttribution093T25), nameof(ChildPostfix)) { priority = Priority.Last });
                patched++;
            }
            catch { installFailures++; }
        }

        public static void DecisionPrefix(Pawn_JobTracker __instance, ref DecisionState __state)
        {
            __state = default(DecisionState);
            if (!TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread) return;

            DecisionContext context = new DecisionContext(__instance);
            __state.Entered = true;
            __state.Started = Stopwatch.GetTimestamp();
            __state.Previous = current;
            __state.Context = context;
            current = context;
            Interlocked.Increment(ref sampledDecisions);
        }

        public static void DecisionPostfix(DecisionState __state)
        {
            FinishDecision(__state);
        }

        public static Exception DecisionFinalizer(Exception __exception, DecisionState __state)
        {
            if (__exception != null) FinishDecision(__state);
            return __exception;
        }

        private static void FinishDecision(DecisionState state)
        {
            if (!state.Entered || state.Started == 0L) return;
            if (!ReferenceEquals(current, state.Context)) return;

            long elapsed = Stopwatch.GetTimestamp() - state.Started;
            current = state.Previous;
            UpdateMax(ref maxDecisionTicks, elapsed);
            if (elapsed < Stopwatch.Frequency / 50) return; // <20ms

            Interlocked.Increment(ref tails20);
            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }

            Pawn pawn = null;
            try
            {
                if (pawnField != null && state.Context != null && state.Context.Tracker != null)
                    pawn = pawnField.GetValue(state.Context.Tracker) as Pawn;
            }
            catch { }

            string pawnText = "<unknown>";
            if (pawn != null)
            {
                string job = "none";
                try { if (pawn.CurJobDef != null) job = pawn.CurJobDef.defName; }
                catch { }
                pawnText = (pawn.def == null ? "Pawn" : pawn.def.defName) + "#" + pawn.thingIDNumber + "[" + job + "]";
            }

            DecisionContext c = state.Context;
            string text = "frame=" + RimMTRuntime.MainThreadFrames + ",tick=" + gameTick +
                ",pawn=" + pawnText + ",total=" + Ms(elapsed) + "ms" +
                ",priority=" + Ms(c.ThinkPriority) +
                ",conditional=" + Ms(c.ThinkConditional) +
                ",subtree=" + Ms(c.ThinkSubtree) +
                ",thinkJobGiver=" + Ms(c.ThinkJobGiver) +
                ",jobGiver=" + Ms(c.JobGiverRoot) +
                ",workGiver=" + Ms(c.WorkGiver);

            lock (RecentLock)
            {
                Recent[recentPos] = text;
                recentPos = (recentPos + 1) % RecentCapacity;
                if (recentCount < RecentCapacity) recentCount++;
            }
        }

        public static void ChildPrefix(MethodBase __originalMethod, ref ChildState __state)
        {
            __state = default(ChildState);
            DecisionContext context = current;
            if (context == null || !TailPawnAttribution093T2.DeepActive) return;
            JobDecisionChild093T25 kind;
            if (__originalMethod == null || !ChildKinds.TryGetValue(__originalMethod, out kind)) return;
            __state.Started = Stopwatch.GetTimestamp();
            __state.Kind = kind;
            __state.Active = true;
        }

        public static void ChildPostfix(ChildState __state)
        {
            if (!__state.Active || __state.Started == 0L) return;
            DecisionContext context = current;
            if (context == null) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            int k = (int)__state.Kind;
            if (k < 0 || k >= Kinds) return;

            Interlocked.Increment(ref Calls[k]);
            Interlocked.Add(ref TotalTicks[k], elapsed);
            UpdateMax(ref MaxTicks[k], elapsed);
            if (elapsed >= Stopwatch.Frequency / 200) Interlocked.Increment(ref Over5[k]);
            if (elapsed >= Stopwatch.Frequency / 50) Interlocked.Increment(ref Over20[k]);
            context.Add(__state.Kind, elapsed);
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T25 Job Decision root: patched=").Append(patched)
              .Append(", missing=").Append(missing)
              .Append(", installFailures=").Append(installFailures)
              .Append(", sampledDecisions=").Append(Interlocked.Read(ref sampledDecisions))
              .Append(", tails>=20ms=").Append(Interlocked.Read(ref tails20))
              .Append(", maxDecisionMs=").Append(Ms(Interlocked.Read(ref maxDecisionTicks)))
              .Append(". Inclusive children: ");
            for (int i = 0; i < Kinds; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = Interlocked.Read(ref Calls[i]);
                long total = Interlocked.Read(ref TotalTicks[i]);
                sb.Append(Names[i]).Append("[calls=").Append(calls)
                  .Append(",avgUs=").Append(calls == 0 ? "0.0" : (total * 1000000.0 / Stopwatch.Frequency / calls).ToString("F1"))
                  .Append(",maxMs=").Append(Ms(Interlocked.Read(ref MaxTicks[i])))
                  .Append(",>5/20=").Append(Interlocked.Read(ref Over5[i])).Append('/').Append(Interlocked.Read(ref Over20[i]))
                  .Append(']');
            }
            sb.Append(". Child timings are inclusive/nested; no gameplay result is altered.");
            return sb.ToString();
        }

        internal static string RecentSummary()
        {
            lock (RecentLock)
            {
                if (recentCount == 0) return "T25 recent DetermineNextJob >=20ms: none.";
                StringBuilder sb = new StringBuilder(4096);
                sb.Append("T25 recent DetermineNextJob >=20ms (oldest->newest): ");
                int start = recentCount == RecentCapacity ? recentPos : 0;
                for (int i = 0; i < recentCount; i++)
                {
                    if (i != 0) sb.Append("; ");
                    sb.Append(Recent[(start + i) % RecentCapacity]);
                }
                return sb.ToString();
            }
        }

        private static string Ms(long ticks)
        {
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F2");
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
        }

        internal struct DecisionState
        {
            internal bool Entered;
            internal long Started;
            internal DecisionContext Previous;
            internal DecisionContext Context;
        }

        internal struct ChildState
        {
            internal bool Active;
            internal long Started;
            internal JobDecisionChild093T25 Kind;
        }

        internal sealed class DecisionContext
        {
            internal readonly Pawn_JobTracker Tracker;
            internal long ThinkPriority;
            internal long ThinkConditional;
            internal long ThinkSubtree;
            internal long ThinkJobGiver;
            internal long JobGiverRoot;
            internal long WorkGiver;

            internal DecisionContext(Pawn_JobTracker tracker) { Tracker = tracker; }

            internal void Add(JobDecisionChild093T25 kind, long ticks)
            {
                switch (kind)
                {
                    case JobDecisionChild093T25.ThinkPriority: ThinkPriority += ticks; break;
                    case JobDecisionChild093T25.ThinkConditional: ThinkConditional += ticks; break;
                    case JobDecisionChild093T25.ThinkSubtree: ThinkSubtree += ticks; break;
                    case JobDecisionChild093T25.ThinkJobGiver: ThinkJobGiver += ticks; break;
                    case JobDecisionChild093T25.JobGiverRoot: JobGiverRoot += ticks; break;
                    case JobDecisionChild093T25.WorkGiver: WorkGiver += ticks; break;
                }
            }
        }
    }
}
'@
Set-Content $diagPath $diag -Encoding UTF8

# -----------------------------------------------------------------------------
# T25-B: install the fixed root attribution and the existing safe async candidate plan.
# -----------------------------------------------------------------------------
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot `
    'internal const string Version = "0.9.3-t24.1-generic-def-safety";' `
    'internal const string Version = "0.9.3-t25-job-decision-root";' `
    'T25 bootstrap version'

$boot = Replace-OrThrow $boot @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                AsyncJobCandidatePlan04182.Apply(harmony);
                JobDecisionRootAttribution093T25.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ 'T25 async candidate plan + decision root installs'

$boot = $boot.Replace('[RimMT] V0.9.3-T24.1 Generic Def Safety initialized.',
    '[RimMT] V0.9.3-T25 Job Decision Root + Async Candidate Plan initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T24.1 Generic Def Safety', 'V0.9.3-T25 Job Decision Root + Async Candidate Plan')
$report = Replace-OrThrow $report @'
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
'@ @'
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(AsyncJobCandidatePlan04182.Summary());
            sb.AppendLine(JobDecisionRootAttribution093T25.Summary());
            sb.AppendLine(JobDecisionRootAttribution093T25.RecentSummary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
'@ 'T25 report summaries'
$report = $report.Replace(
    'T24 behavior retained; T24.1 disables all closed-generic DefDatabase<TraitDef>/TechLevelDatabase<TraitDef> Harmony hooks after Mono generic-sharing corruption was observed; non-generic WorldRoot timing remains;',
    'T24.1 generic-Def safety retained; T25 adds bounded Job Decision root attribution and activates a no-wait async static-Thing candidate plan: main-thread primitive capture, worker distance/range ordering, exact main-thread membership/position revalidation before use;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T24.1 Generic Def Safety', 'V0.9.3-T25 Job Decision Root + Async Candidate Plan')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T25 Job Decision Root + Async Candidate Plan: fixed low-duty decision attribution plus revalidated no-wait worker candidate ordering; T24.1 generic safety retained.'