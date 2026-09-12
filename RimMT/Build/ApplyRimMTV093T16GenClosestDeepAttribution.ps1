$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T16 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T16 GenClosest / RegionTraverser Deep Attribution
# Diagnostic child of T15. Production behavior/authority is unchanged.
# T16 reuses T15's one-shot 64-package temporary WorkGiver detail session and enriches
# the temporary infrastructure hooks with caller/source-shape attribution. No enumerable
# is consumed and no validator is wrapped/replaced: source count is read only from ICollection,
# so dynamic iterators remain count=unknown. This preserves Vanilla ordering and semantics.
# A separate TickManager Harmony census records SimplyMoreFPS coexistence and dispatcher gate
# state, but T16 does not enable/suppress any feature based on that census.

$deepPath = 'RimMT/Source/RimMT/Diagnostics/GenClosestDeepAttribution093T16.cs'
$deep = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T16 bounded search-infrastructure attribution. Active only while the T15 temporary
    /// WorkGiver detail session is active. It observes argument shape without enumerating
    /// dynamic sources or wrapping validators, so it is measurement-only.
    /// </summary>
    internal static class GenClosestDeepAttribution093T16
    {
        private const int MaxSlowCalls = 24;
        private const int MaxSlowPackages = 16;
        private static readonly long SlowCallTicks = Math.Max(1L, Stopwatch.Frequency * 8L / 1000L);
        private static readonly long SlowPackageTicks = Math.Max(1L, Stopwatch.Frequency * 20L / 1000L);
        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static readonly List<SlowCall> SlowCalls = new List<SlowCall>();
        private static readonly List<PackageTrace> SlowPackages = new List<PackageTrace>();

        private static long totalCalls;
        private static long genClosestCalls;
        private static long reachabilityCalls;
        private static long regionTraverserCalls;
        private static long sourceObserved;
        private static long knownSourceCountCalls;
        private static long unknownSourceCountCalls;
        private static long mobileSourceCalls;
        private static long dynamicSourceCalls;
        private static long sourceCountTotal;
        private static int maxSourceCount;
        private static long slowCalls8;
        private static long slowCalls20;

        [ThreadStatic] private static PackageState currentPackage;

        internal struct Scope
        {
            internal bool Active;
            internal long Started;
            internal string Phase;
            internal string Caller;
            internal string SourceType;
            internal int SourceCount;
            internal bool SourcePresent;
            internal bool MobileSource;
            internal bool DynamicSource;
        }

        internal static void Reset()
        {
            Stats.Clear();
            SlowCalls.Clear();
            SlowPackages.Clear();
            totalCalls = genClosestCalls = reachabilityCalls = regionTraverserCalls = 0L;
            sourceObserved = knownSourceCountCalls = unknownSourceCountCalls = 0L;
            mobileSourceCalls = dynamicSourceCalls = sourceCountTotal = 0L;
            maxSourceCount = 0;
            slowCalls8 = slowCalls20 = 0L;
            currentPackage = null;
        }

        internal static void BeginPackage(string pawn)
        {
            if (!RimMTThreadGuard.IsMainThread) return;
            currentPackage = new PackageState { Pawn = string.IsNullOrEmpty(pawn) ? "<unknown>" : pawn };
        }

        internal static void EndPackage(long elapsedTicks)
        {
            PackageState package = currentPackage;
            currentPackage = null;
            if (package == null || elapsedTicks < SlowPackageTicks) return;

            PackageTrace trace = new PackageTrace
            {
                ElapsedTicks = elapsedTicks,
                Pawn = package.Pawn,
                InfraCalls = package.InfraCalls,
                GenClosestCalls = package.GenClosestCalls,
                ReachCalls = package.ReachCalls,
                RegionCalls = package.RegionCalls,
                SourceCalls = package.SourceCalls,
                KnownSourceCalls = package.KnownSourceCalls,
                UnknownSourceCalls = package.UnknownSourceCalls,
                MobileSourceCalls = package.MobileSourceCalls,
                DynamicSourceCalls = package.DynamicSourceCalls,
                SourceCountSum = package.SourceCountSum,
                MaxSourceCount = package.MaxSourceCount,
                InfraTicks = package.InfraTicks
            };
            SlowPackages.Add(trace);
            SlowPackages.Sort(delegate(PackageTrace a, PackageTrace b) { return b.ElapsedTicks.CompareTo(a.ElapsedTicks); });
            if (SlowPackages.Count > MaxSlowPackages)
                SlowPackages.RemoveRange(MaxSlowPackages, SlowPackages.Count - MaxSlowPackages);
        }

        internal static Scope Begin(MethodBase method, object[] args)
        {
            Scope scope = default(Scope);
            if (!WorkGiverProfiler.DetailCaptureActive || !RimMTThreadGuard.IsMainThread || method == null)
                return scope;

            scope.Active = true;
            scope.Started = Stopwatch.GetTimestamp();
            scope.Phase = Classify(method);
            scope.Caller = WorkGiverProfiler.CurrentCaller;
            scope.SourceCount = -1;
            DescribeSource(args, ref scope);
            return scope;
        }

        internal static void End(Scope scope)
        {
            if (!scope.Active || scope.Started == 0L || !RimMTThreadGuard.IsMainThread)
                return;

            long elapsed = Stopwatch.GetTimestamp() - scope.Started;
            totalCalls++;
            if (scope.Phase.StartsWith("GenClosest.", StringComparison.Ordinal)) genClosestCalls++;
            else if (scope.Phase.StartsWith("Reachability.", StringComparison.Ordinal)) reachabilityCalls++;
            else if (scope.Phase.StartsWith("RegionTraverser.", StringComparison.Ordinal)) regionTraverserCalls++;

            if (scope.SourcePresent)
            {
                sourceObserved++;
                if (scope.SourceCount >= 0)
                {
                    knownSourceCountCalls++;
                    sourceCountTotal += scope.SourceCount;
                    if (scope.SourceCount > maxSourceCount) maxSourceCount = scope.SourceCount;
                }
                else unknownSourceCountCalls++;
                if (scope.MobileSource) mobileSourceCalls++;
                if (scope.DynamicSource) dynamicSourceCalls++;
            }

            string key = (scope.Caller ?? "<package>") + "|" + scope.Phase + "|" +
                (scope.SourcePresent ? scope.SourceType : "<no-source>") + "|" +
                (scope.MobileSource ? "mobile" : "static") + "|" +
                (scope.DynamicSource ? "dynamic" : "countable");
            Stat stat;
            if (!Stats.TryGetValue(key, out stat))
            {
                stat = new Stat
                {
                    Caller = scope.Caller ?? "<package>",
                    Phase = scope.Phase,
                    SourceType = scope.SourcePresent ? scope.SourceType : "<no-source>",
                    Mobile = scope.MobileSource,
                    Dynamic = scope.DynamicSource
                };
                Stats.Add(key, stat);
            }
            stat.Calls++;
            stat.TotalTicks += elapsed;
            if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
            if (scope.SourceCount >= 0)
            {
                stat.KnownSourceCalls++;
                stat.SourceCountTotal += scope.SourceCount;
                if (scope.SourceCount > stat.MaxSourceCount) stat.MaxSourceCount = scope.SourceCount;
            }
            else if (scope.SourcePresent) stat.UnknownSourceCalls++;

            if (elapsed >= SlowCallTicks)
            {
                slowCalls8++;
                SaveSlowCall(scope, elapsed);
            }
            if (elapsed >= SlowPackageTicks) slowCalls20++;

            PackageState package = currentPackage;
            if (package != null)
            {
                package.InfraCalls++;
                package.InfraTicks += elapsed;
                if (scope.Phase.StartsWith("GenClosest.", StringComparison.Ordinal)) package.GenClosestCalls++;
                else if (scope.Phase.StartsWith("Reachability.", StringComparison.Ordinal)) package.ReachCalls++;
                else if (scope.Phase.StartsWith("RegionTraverser.", StringComparison.Ordinal)) package.RegionCalls++;
                if (scope.SourcePresent)
                {
                    package.SourceCalls++;
                    if (scope.SourceCount >= 0)
                    {
                        package.KnownSourceCalls++;
                        package.SourceCountSum += scope.SourceCount;
                        if (scope.SourceCount > package.MaxSourceCount) package.MaxSourceCount = scope.SourceCount;
                    }
                    else package.UnknownSourceCalls++;
                    if (scope.MobileSource) package.MobileSourceCalls++;
                    if (scope.DynamicSource) package.DynamicSourceCalls++;
                }
            }
        }

        internal static string Summary(int topN)
        {
            List<Stat> entries = new List<Stat>(Stats.Values);
            entries.Sort(delegate(Stat a, Stat b)
            {
                int total = b.TotalTicks.CompareTo(a.TotalTicks);
                if (total != 0) return total;
                return b.MaxTicks.CompareTo(a.MaxTicks);
            });
            if (topN < 1) topN = 1;
            if (topN > entries.Count) topN = entries.Count;

            double avgSource = knownSourceCountCalls == 0 ? 0.0 : sourceCountTotal / (double)knownSourceCountCalls;
            StringBuilder sb = new StringBuilder(12288);
            sb.Append("T16 GenClosest/Region deep attribution: calls=").Append(totalCalls)
              .Append(", genClosest=").Append(genClosestCalls)
              .Append(", reachability=").Append(reachabilityCalls)
              .Append(", regionTraverser=").Append(regionTraverserCalls)
              .Append(", sourceObserved=").Append(sourceObserved)
              .Append(", sourceCountKnown/unknown=").Append(knownSourceCountCalls).Append('/').Append(unknownSourceCountCalls)
              .Append(", mobileSourceCalls=").Append(mobileSourceCalls)
              .Append(", dynamicSourceCalls=").Append(dynamicSourceCalls)
              .Append(", avgKnownSourceCount=").Append(avgSource.ToString("F1"))
              .Append(", maxSourceCount=").Append(maxSourceCount)
              .Append(", infra>=8/20ms=").Append(slowCalls8).Append('/').Append(slowCalls20)
              .Append(". Source counts are read only from ICollection; unknown/dynamic enumerables are never consumed; inclusive nested calls can overlap.");

            for (int i = 0; i < topN; i++)
            {
                Stat e = entries[i];
                double totalMs = e.TotalTicks * 1000.0 / Stopwatch.Frequency;
                double avgMs = e.Calls == 0 ? 0.0 : totalMs / e.Calls;
                double maxMs = e.MaxTicks * 1000.0 / Stopwatch.Frequency;
                double sourceAvg = e.KnownSourceCalls == 0 ? 0.0 : e.SourceCountTotal / (double)e.KnownSourceCalls;
                sb.Append("\n  #").Append(i + 1).Append(' ').Append(e.Caller).Append(" -> ").Append(e.Phase)
                  .Append(": calls=").Append(e.Calls)
                  .Append(", totalMs=").Append(totalMs.ToString("F1"))
                  .Append(", avgMs=").Append(avgMs.ToString("F3"))
                  .Append(", maxMs=").Append(maxMs.ToString("F3"))
                  .Append(", source=").Append(e.SourceType)
                  .Append(", mobile=").Append(e.Mobile)
                  .Append(", dynamic=").Append(e.Dynamic)
                  .Append(", countKnown/unknown=").Append(e.KnownSourceCalls).Append('/').Append(e.UnknownSourceCalls)
                  .Append(", avgCount=").Append(sourceAvg.ToString("F1"))
                  .Append(", maxCount=").Append(e.MaxSourceCount);
            }

            for (int i = 0; i < SlowCalls.Count; i++)
            {
                SlowCall s = SlowCalls[i];
                sb.Append("\n  CALL#").Append(i + 1)
                  .Append(": ms=").Append((s.ElapsedTicks * 1000.0 / Stopwatch.Frequency).ToString("F3"))
                  .Append(", caller=").Append(s.Caller)
                  .Append(", phase=").Append(s.Phase)
                  .Append(", source=").Append(s.SourceType)
                  .Append(", count=").Append(s.SourceCount < 0 ? "unknown" : s.SourceCount.ToString())
                  .Append(", mobile=").Append(s.Mobile)
                  .Append(", dynamic=").Append(s.Dynamic);
            }

            for (int i = 0; i < SlowPackages.Count; i++)
            {
                PackageTrace p = SlowPackages[i];
                double infraMs = p.InfraTicks * 1000.0 / Stopwatch.Frequency;
                double avgKnown = p.KnownSourceCalls == 0 ? 0.0 : p.SourceCountSum / (double)p.KnownSourceCalls;
                sb.Append("\n  PKG#").Append(i + 1)
                  .Append(": totalMs=").Append((p.ElapsedTicks * 1000.0 / Stopwatch.Frequency).ToString("F3"))
                  .Append(", pawn=").Append(p.Pawn)
                  .Append(", infraCalls=").Append(p.InfraCalls)
                  .Append(", infraInclusiveMs=").Append(infraMs.ToString("F3"))
                  .Append(", gen/reach/region=").Append(p.GenClosestCalls).Append('/').Append(p.ReachCalls).Append('/').Append(p.RegionCalls)
                  .Append(", sources=").Append(p.SourceCalls)
                  .Append(", known/unknown=").Append(p.KnownSourceCalls).Append('/').Append(p.UnknownSourceCalls)
                  .Append(", mobile/dynamic=").Append(p.MobileSourceCalls).Append('/').Append(p.DynamicSourceCalls)
                  .Append(", avgKnownCount=").Append(avgKnown.ToString("F1"))
                  .Append(", maxCount=").Append(p.MaxSourceCount);
            }
            return sb.ToString();
        }

        private static void DescribeSource(object[] args, ref Scope scope)
        {
            if (args == null) return;
            for (int i = 0; i < args.Length; i++)
            {
                object arg = args[i];
                if (arg == null) continue;
                Type type = arg.GetType();
                Type element = ThingElementType(type);
                if (element == null) continue;

                scope.SourcePresent = true;
                scope.SourceType = type.FullName ?? type.Name;
                ICollection collection = arg as ICollection;
                scope.SourceCount = collection == null ? -1 : collection.Count;
                scope.DynamicSource = collection == null;
                scope.MobileSource = typeof(Pawn).IsAssignableFrom(element);

                if (!scope.MobileSource && scope.SourceCount > 0)
                {
                    IList<Thing> things = arg as IList<Thing>;
                    if (things != null)
                    {
                        int sample = Math.Min(8, things.Count);
                        for (int n = 0; n < sample; n++)
                        {
                            if (things[n] is Pawn) { scope.MobileSource = true; break; }
                        }
                    }
                }
                return;
            }
        }

        private static Type ThingElementType(Type type)
        {
            if (type == null) return null;
            if (type.IsArray)
            {
                Type element = type.GetElementType();
                return element != null && typeof(Thing).IsAssignableFrom(element) ? element : null;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                Type direct = type.GetGenericArguments()[0];
                if (typeof(Thing).IsAssignableFrom(direct)) return direct;
            }
            Type[] interfaces;
            try { interfaces = type.GetInterfaces(); }
            catch { return null; }
            for (int i = 0; i < interfaces.Length; i++)
            {
                Type it = interfaces[i];
                if (!it.IsGenericType || it.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                Type element = it.GetGenericArguments()[0];
                if (typeof(Thing).IsAssignableFrom(element)) return element;
            }
            return null;
        }

        private static string Classify(MethodBase method)
        {
            Type type = method.DeclaringType;
            string typeName = type == null ? "<unknown>" : type.FullName;
            if (typeName == "Verse.GenClosest") return "GenClosest." + method.Name;
            if (typeName == "Verse.Reachability") return "Reachability." + method.Name;
            if (typeName == "Verse.RegionTraverser") return "RegionTraverser." + method.Name;
            if (typeName != null && typeName.IndexOf("WorkGiver", StringComparison.Ordinal) >= 0 &&
                (method.Name == "get_PotentialWorkThingsGlobal" || method.Name == "get_PotentialWorkCellsGlobal"))
                return "ScannerSource." + typeName + "." + method.Name;
            return typeName + "." + method.Name;
        }

        private static void SaveSlowCall(Scope scope, long elapsed)
        {
            SlowCalls.Add(new SlowCall
            {
                ElapsedTicks = elapsed,
                Caller = scope.Caller ?? "<package>",
                Phase = scope.Phase,
                SourceType = scope.SourcePresent ? scope.SourceType : "<no-source>",
                SourceCount = scope.SourcePresent ? scope.SourceCount : -1,
                Mobile = scope.MobileSource,
                Dynamic = scope.DynamicSource
            });
            SlowCalls.Sort(delegate(SlowCall a, SlowCall b) { return b.ElapsedTicks.CompareTo(a.ElapsedTicks); });
            if (SlowCalls.Count > MaxSlowCalls)
                SlowCalls.RemoveRange(MaxSlowCalls, SlowCalls.Count - MaxSlowCalls);
        }

        private sealed class Stat
        {
            internal string Caller;
            internal string Phase;
            internal string SourceType;
            internal bool Mobile;
            internal bool Dynamic;
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long KnownSourceCalls;
            internal long UnknownSourceCalls;
            internal long SourceCountTotal;
            internal int MaxSourceCount;
        }

        private sealed class PackageState
        {
            internal string Pawn;
            internal long InfraCalls;
            internal long InfraTicks;
            internal long GenClosestCalls;
            internal long ReachCalls;
            internal long RegionCalls;
            internal long SourceCalls;
            internal long KnownSourceCalls;
            internal long UnknownSourceCalls;
            internal long MobileSourceCalls;
            internal long DynamicSourceCalls;
            internal long SourceCountSum;
            internal int MaxSourceCount;
        }

        private sealed class SlowCall
        {
            internal long ElapsedTicks;
            internal string Caller;
            internal string Phase;
            internal string SourceType;
            internal int SourceCount;
            internal bool Mobile;
            internal bool Dynamic;
        }

        private sealed class PackageTrace
        {
            internal long ElapsedTicks;
            internal string Pawn;
            internal long InfraCalls;
            internal long InfraTicks;
            internal long GenClosestCalls;
            internal long ReachCalls;
            internal long RegionCalls;
            internal long SourceCalls;
            internal long KnownSourceCalls;
            internal long UnknownSourceCalls;
            internal long MobileSourceCalls;
            internal long DynamicSourceCalls;
            internal long SourceCountSum;
            internal int MaxSourceCount;
        }
    }
}
'@
Set-Content $deepPath $deep -Encoding UTF8

$smfPath = 'RimMT/Source/RimMT/Diagnostics/SMFDispatcherCoexistence093T16.cs'
$smf = @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T16 read-only TickManager Harmony census. It records why runtime.dispatcher is or is not
    /// available with SimplyMoreFPS. It never changes FeatureGate or Harmony state.
    /// </summary>
    internal static class SMFDispatcherCoexistence093T16
    {
        internal static string Summary()
        {
            MethodBase target = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
            Dictionary<string, FeatureGate.FeatureState> gates = FeatureGate.Snapshot();
            FeatureGate.FeatureState dispatcher;
            gates.TryGetValue("runtime.dispatcher", out dispatcher);

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T16 SMF/dispatcher coexistence audit: dispatcherEnabled=").Append(FeatureGate.IsEnabled("runtime.dispatcher"));
            if (dispatcher != null)
                sb.Append(", suppressed=").Append(dispatcher.Suppressed).Append(", reason=").Append(string.IsNullOrEmpty(dispatcher.Reason) ? "<none>" : dispatcher.Reason);
            if (target == null)
                return sb.Append(", TickManagerUpdate target missing. Read-only audit; T16 changes no gate/Harmony state.").ToString();

            Patches info = Harmony.GetPatchInfo(target);
            int foreign = 0;
            int smf = 0;
            if (info != null)
            {
                Append(sb, "Prefix", info.Prefixes, ref foreign, ref smf);
                Append(sb, "Postfix", info.Postfixes, ref foreign, ref smf);
                Append(sb, "Transpiler", info.Transpilers, ref foreign, ref smf);
                Append(sb, "Finalizer", info.Finalizers, ref foreign, ref smf);
            }
            sb.Append(", foreignPatches=").Append(foreign).Append(", smfPatches=").Append(smf)
              .Append(". Read-only audit; T16 changes no gate/Harmony state.");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string kind, IEnumerable<Patch> patches, ref int foreign, ref int smf)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                string owner = patch.owner ?? "<null>";
                if (string.Equals(owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                foreign++;
                MethodInfo method = patch.PatchMethod;
                string methodName = method == null || method.DeclaringType == null ? "<null>" : method.DeclaringType.FullName + "." + method.Name;
                bool isSmf = owner.IndexOf("simplymorefps", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             owner.IndexOf("game-frame-budget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             methodName.IndexOf("SimplyMoreFPS", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSmf) smf++;
                sb.Append(" ").Append(kind).Append("[owner=").Append(owner).Append(",method=").Append(methodName)
                  .Append(isSmf ? ",SMF]" : "]");
            }
        }
    }
}
'@
Set-Content $smfPath $smf -Encoding UTF8

# T16 caller context and package correlation. The profiler already allocates only inside
# the one bounded T15 detail session, so this remains non-resident.
$profPath = 'RimMT/Source/RimMT/Diagnostics/WorkGiverProfiler.cs'
$prof = Get-Content $profPath -Raw
$prof = Replace-OrThrow $prof @'
        [ThreadStatic] private static Dictionary<string, long> currentInclusivePhases;
        [ThreadStatic] private static string currentPawn;
'@ @'
        [ThreadStatic] private static Dictionary<string, long> currentInclusivePhases;
        [ThreadStatic] private static string currentPawn;
        [ThreadStatic] private static List<string> callerStack;
'@ 'T16 caller stack field'
$prof = Replace-OrThrow $prof @'
            currentInclusivePhases = null;
            currentPawn = null;
            sessionActive = true;
'@ @'
            currentInclusivePhases = null;
            currentPawn = null;
            callerStack = null;
            GenClosestDeepAttribution093T16.Reset();
            sessionActive = true;
'@ 'T16 reset deep attribution with detail session'
$prof = Replace-OrThrow $prof @'
            currentInclusivePhases = null;
            currentPawn = null;
        }
'@ @'
            currentInclusivePhases = null;
            currentPawn = null;
            callerStack = null;
        }
'@ 'T16 stop clears caller stack'
$prof = Replace-OrThrow $prof @'
            currentInclusivePhases = new Dictionary<string, long>(StringComparer.Ordinal);
            currentPawn = pawn == null ? "<null>" : pawn.ToString();
            return state;
'@ @'
            currentInclusivePhases = new Dictionary<string, long>(StringComparer.Ordinal);
            currentPawn = pawn == null ? "<null>" : pawn.ToString();
            callerStack = new List<string>(8);
            GenClosestDeepAttribution093T16.BeginPackage(currentPawn);
            return state;
'@ 'T16 begin package correlation'
$prof = Replace-OrThrow $prof @'
                if (elapsed >= Threshold20Ticks)
                {
                    slowJobPackages++;
                    SaveSlowTrace(elapsed);
                }
'@ @'
                GenClosestDeepAttribution093T16.EndPackage(elapsed);
                if (elapsed >= Threshold20Ticks)
                {
                    slowJobPackages++;
                    SaveSlowTrace(elapsed);
                }
'@ 'T16 end package correlation'
$prof = Replace-OrThrow $prof @'
                currentInclusivePhases = null;
                currentPawn = null;
                if (sessionActive && totalJobPackages >= targetJobPackages)
'@ @'
                currentInclusivePhases = null;
                currentPawn = null;
                callerStack = null;
                if (sessionActive && totalJobPackages >= targetJobPackages)
'@ 'T16 package completion clears caller stack'
$prof = Replace-OrThrow $prof @'
        internal static long Begin()
        {
            if (!captureDetail || !RimMTThreadGuard.IsMainThread)
                return 0L;
            return Stopwatch.GetTimestamp();
        }
'@ @'
        internal static string CurrentCaller
        {
            get
            {
                if (callerStack == null || callerStack.Count == 0) return "<package>";
                return callerStack[callerStack.Count - 1];
            }
        }

        internal static void EnterCaller(WorkGiver giver, MethodBase method)
        {
            if (!captureDetail || !RimMTThreadGuard.IsMainThread) return;
            if (callerStack == null) callerStack = new List<string>(8);
            string def = giver == null || giver.def == null ? "<no-def>" : giver.def.defName;
            string type = giver == null ? "<null>" : giver.GetType().FullName;
            string phase = method == null ? "?" : method.Name;
            callerStack.Add(def + "/" + type + "." + phase);
        }

        internal static void ExitCaller()
        {
            if (callerStack == null || callerStack.Count == 0) return;
            callerStack.RemoveAt(callerStack.Count - 1);
        }

        internal static long Begin()
        {
            if (!captureDetail || !RimMTThreadGuard.IsMainThread)
                return 0L;
            return Stopwatch.GetTimestamp();
        }
'@ 'T16 current WorkGiver caller context'
Set-Content $profPath $prof -Encoding UTF8

$detailPath = 'RimMT/Source/RimMT/Patches/WorkGiverDetailPatches.cs'
$detail = Get-Content $detailPath -Raw
$detail = Replace-OrThrow $detail @'
        public static void Prefix(ref long __state)
        {
            __state = WorkGiverProfiler.Begin();
        }

        public static void Postfix(WorkGiver __instance, MethodBase __originalMethod, long __state)
        {
            WorkGiverProfiler.Record(__instance, __originalMethod, __state);
        }

        public static void InfrastructurePrefix(ref long __state)
        {
            __state = JobGiverInfrastructureProfiler.Begin();
        }

        public static void InfrastructurePostfix(MethodBase __originalMethod, long __state)
        {
            JobGiverInfrastructureProfiler.Record(__originalMethod, __state);
        }
'@ @'
        public struct InfrastructureStateT16
        {
            public long LegacyStarted;
            public GenClosestDeepAttribution093T16.Scope Deep;
        }

        public static void Prefix(WorkGiver __instance, MethodBase __originalMethod, ref long __state)
        {
            WorkGiverProfiler.EnterCaller(__instance, __originalMethod);
            __state = WorkGiverProfiler.Begin();
        }

        public static void Postfix(WorkGiver __instance, MethodBase __originalMethod, long __state)
        {
            try { WorkGiverProfiler.Record(__instance, __originalMethod, __state); }
            finally { WorkGiverProfiler.ExitCaller(); }
        }

        public static void InfrastructurePrefix(MethodBase __originalMethod, object[] __args, ref InfrastructureStateT16 __state)
        {
            __state.LegacyStarted = JobGiverInfrastructureProfiler.Begin();
            __state.Deep = GenClosestDeepAttribution093T16.Begin(__originalMethod, __args);
        }

        public static void InfrastructurePostfix(MethodBase __originalMethod, InfrastructureStateT16 __state)
        {
            JobGiverInfrastructureProfiler.Record(__originalMethod, __state.LegacyStarted);
            GenClosestDeepAttribution093T16.End(__state.Deep);
        }
'@ 'T16 infrastructure argument/caller attribution state'
Set-Content $detailPath $detail -Encoding UTF8

# Version/report only. T15 coordinator remains responsible for the bounded one-shot lifecycle.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t15-workgiver-deep-attribution";' 'internal const string Version = "0.9.3-t16-genclosest-deep-attribution";' 'T16 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T15 WorkGiver Deep Attribution initialized. T14/T13/T8 production and diagnostic behavior retained; a sampled DetermineNextJob >=20ms requests one deferred 64-package WorkGiver detail burst that auto-unpatches; Storyteller >=100ms events reuse T1 timing.' '[RimMT] V0.9.3-T16 GenClosest Deep Attribution initialized. T15 bounded lifecycle retained; temporary infrastructure hooks now attribute GenClosest/Reachability/RegionTraverser calls to WorkGiver caller and non-enumerated source shape; SMF dispatcher census is read-only.' 'T16 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
            sb.AppendLine(StorytellerCatastrophic093T15.Summary());
'@ @'
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
            sb.AppendLine(GenClosestDeepAttribution093T16.Summary(24));
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
            sb.AppendLine(StorytellerCatastrophic093T15.Summary());
'@ 'T16 report deep search and SMF census lines'
$report = Replace-OrThrow $report 'V0.9.3-T15 WorkGiver Deep Attribution' 'V0.9.3-T16 GenClosest Deep Attribution' 'T16 report title'
$report = Replace-OrThrow $report 'T8 production behavior retained + T13/T14 attribution retained + T15 one-shot WorkGiver deep burst; T15 trigger reuses T2 sampled DetermineNextJob >=20ms timing, installs temporary detail detours only at the next frame boundary for 64 outer JobGiver_Work packages, then auto-unpatches; Storyteller >=100ms recorder reuses T1 elapsed time; no resident WorkGiver profiler; no T9/T10/T11/T12 probe chain;' 'T8 production behavior retained + T13/T14 attribution retained + T15 one-shot bounded lifecycle + T16 caller/source-shape search attribution; T16 reuses the same temporary 64-package detours and adds no resident profiler; dynamic source enumerables are never consumed and validators are never wrapped; SMF dispatcher coexistence is census-only; Storyteller >=100ms recorder reuses T1 elapsed time; no T9/T10/T11/T12 probe chain;' 'T16 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T15 WorkGiver Deep Attribution', 'V0.9.3-T16 GenClosest Deep Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

$projectPath = 'RimMT/Source/RimMT/RimMT.csproj'
$project = Get-Content $projectPath -Raw
$project = $project.Replace('V0.9.3-T15 keeps superseded experiments and resident deep profilers out of the assembly.', 'V0.9.3-T16 keeps superseded experiments and resident deep profilers out of the assembly.')
$project = $project.Replace('The three WorkGiver diagnostic components are intentionally compiled for T15 because they', 'The bounded WorkGiver/T16 search diagnostic components are compiled because they')
Set-Content $projectPath $project -Encoding UTF8

Write-Host 'Applied RimMT V0.9.3-T16: T15 bounded 64-package burst retained; GenClosest/Reachability/RegionTraverser calls attributed to WorkGiver caller and non-enumerated source shape; SMF dispatcher census read-only; production authority unchanged.'