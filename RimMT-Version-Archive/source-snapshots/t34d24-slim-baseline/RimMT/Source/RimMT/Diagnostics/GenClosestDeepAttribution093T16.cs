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
