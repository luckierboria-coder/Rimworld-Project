using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T30 measurement-only census for structural redundancy inside one synchronous
    /// JobGiver_Work.TryIssueJobPackage transaction.
    ///
    /// This module does not patch a new RimWorld method and does not change any gameplay result.
    /// Existing T20/T21/T22/S4/GlobalNearest hooks report identities that they already possess.
    /// Only every eighth package is sampled, and only while RimMT.Diagnostics is loaded.
    ///
    /// The census asks platform-level questions rather than naming WorkGivers:
    ///  - how often the same Thing is presented to the same validator pair again;
    ///  - how often one Thing is visited by multiple distinct scanners in one package;
    ///  - how often an exact Reachability query repeats, and how often one target is queried
    ///    through multiple distinct reachability shapes;
    ///  - how often one search source object is reused, across centers and search routes.
    ///
    /// No predicate result, Reachability result, source membership, Job, reservation, priority,
    /// or cross-package gameplay state is cached.
    /// </summary>
    internal static class JobSearchRedundancyCensus093T30
    {
        internal const string FeatureId = "diagnostics.jobSearchRedundancy";

        private const long SampleMask = 7L; // 1 / 8 outer packages.
        private const int MaxValidatorPairs = 8192;
        private const int MaxValidatorThings = 4096;
        private const int MaxReachQueries = 8192;
        private const int MaxReachTargets = 4096;
        private const int MaxSources = 512;

        [ThreadStatic] private static SampleContext current;

        private static int diagnosticsPresence = -1;
        private static long packagesSeen;
        private static long sampledPackages;
        private static long sampledSlow20;
        private static long sampledSlow50;
        private static long sampleCapacityBypass;
        private static readonly AggregateBucket All = new AggregateBucket();
        private static readonly AggregateBucket Slow20 = new AggregateBucket();

        internal static bool Sampling
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return current != null; }
        }

        internal static void BeginPackage(Pawn pawn, long generation)
        {
            current = null;
            packagesSeen++;

            if (!DiagnosticsPresent())
                return;
            if ((generation & SampleMask) != 0L)
                return;

            current = new SampleContext(pawn, generation, Stopwatch.GetTimestamp());
            sampledPackages++;
        }

        internal static void EndPackage()
        {
            SampleContext context = current;
            current = null;
            if (context == null) return;

            long elapsedTicks = Stopwatch.GetTimestamp() - context.StartedTimestamp;
            if (elapsedTicks < 0L) elapsedTicks = 0L;
            double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

            PackageSnapshot snapshot = context.Snapshot(elapsedMs);
            All.Add(snapshot);

            if (elapsedMs >= 20.0)
            {
                sampledSlow20++;
                Slow20.Add(snapshot);
            }
            if (elapsedMs >= 50.0)
                sampledSlow50++;

            sampleCapacityBypass += context.CapacityBypass;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RecordValidator(JobSearchTransaction093T20.ValidatorKey key)
        {
            SampleContext context = current;
            if (context == null || key.Thing == null || key.Scanner == null) return;
            context.RecordValidator(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RecordReach(JobSearchTransaction093T20.ReachKey key)
        {
            SampleContext context = current;
            if (context == null) return;
            context.RecordReach(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void RecordSource(object source, IntVec3 center, SourceRoute route, int knownCount)
        {
            SampleContext context = current;
            if (context == null || source == null || !center.IsValid) return;
            context.RecordSource(source, center, route, knownCount);
        }

        internal static string Summary()
        {
            return "T30 Job Search redundancy census: diagnosticsPresent=" + DiagnosticsPresent() +
                ", sample=1/8, packagesSeen=" + packagesSeen +
                ", sampledPackages=" + sampledPackages +
                ", sampledSlow>=20/50ms=" + sampledSlow20 + "/" + sampledSlow50 +
                ", capacityBypass=" + sampleCapacityBypass +
                ". ALL{" + All.Format() + "} SLOW20{" + Slow20.Format() + "}. " +
                "Measurement-only: no WorkGiver whitelist/name attribution, no new RimWorld Harmony target, " +
                "no predicate/reach/source-membership/Job/reservation/priority result cache.";
        }

        private static bool DiagnosticsPresent()
        {
            int cached = diagnosticsPresence;
            if (cached >= 0) return cached == 1;

            bool found = false;
            try
            {
                System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    System.Reflection.Assembly assembly = assemblies[i];
                    if (assembly == null) continue;
                    string name = assembly.GetName().Name;
                    if (string.Equals(name, "RimMT.Diagnostics", StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
            }
            catch { }

            diagnosticsPresence = found ? 1 : 0;
            return found;
        }

        internal enum SourceRoute
        {
            Global = 1,
            GlobalReachable = 2,
            GlobalNewTemp = 4,
            ClosestReachableLister = 8,
            ClosestReachableCustom = 16
        }

        private sealed class SampleContext
        {
            internal readonly Pawn Pawn;
            internal readonly long Generation;
            internal readonly long StartedTimestamp;

            internal long ValidatorCalls;
            internal long ReachCalls;
            internal long SourceCalls;
            internal long KnownSourceCountObservations;
            internal long KnownSourceCountTotal;
            internal int MaxKnownSourceCount;
            internal long CapacityBypass;

            internal readonly HashSet<JobSearchTransaction093T20.ValidatorKey> ValidatorPairs =
                new HashSet<JobSearchTransaction093T20.ValidatorKey>();
            internal readonly Dictionary<Thing, ValidatorThingStats> ValidatorThings =
                new Dictionary<Thing, ValidatorThingStats>(ReferenceComparer<Thing>.Instance);

            internal readonly HashSet<JobSearchTransaction093T20.ReachKey> ReachQueries =
                new HashSet<JobSearchTransaction093T20.ReachKey>();
            internal readonly Dictionary<TargetIdentity, ReachTargetStats> ReachTargets =
                new Dictionary<TargetIdentity, ReachTargetStats>();

            internal readonly Dictionary<object, SourceStats> Sources =
                new Dictionary<object, SourceStats>(ReferenceComparer<object>.Instance);

            internal SampleContext(Pawn pawn, long generation, long startedTimestamp)
            {
                Pawn = pawn;
                Generation = generation;
                StartedTimestamp = startedTimestamp;
            }

            internal void RecordValidator(JobSearchTransaction093T20.ValidatorKey key)
            {
                ValidatorCalls++;

                if (ValidatorPairs.Count < MaxValidatorPairs || ValidatorPairs.Contains(key))
                {
                    ValidatorPairs.Add(key);
                }
                else
                {
                    CapacityBypass++;
                }

                ValidatorThingStats stats;
                if (!ValidatorThings.TryGetValue(key.Thing, out stats))
                {
                    if (ValidatorThings.Count >= MaxValidatorThings)
                    {
                        CapacityBypass++;
                        return;
                    }
                    stats = new ValidatorThingStats(key.Scanner);
                    ValidatorThings.Add(key.Thing, stats);
                    return;
                }
                stats.Record(key.Scanner);
            }

            internal void RecordReach(JobSearchTransaction093T20.ReachKey key)
            {
                ReachCalls++;

                if (ReachQueries.Count < MaxReachQueries || ReachQueries.Contains(key))
                {
                    ReachQueries.Add(key);
                }
                else
                {
                    CapacityBypass++;
                }

                TargetIdentity target = new TargetIdentity(key);
                ReachTargetStats stats;
                if (!ReachTargets.TryGetValue(target, out stats))
                {
                    if (ReachTargets.Count >= MaxReachTargets)
                    {
                        CapacityBypass++;
                        return;
                    }
                    stats = new ReachTargetStats(key);
                    ReachTargets.Add(target, stats);
                    return;
                }
                stats.Record(key);
            }

            internal void RecordSource(object source, IntVec3 center, SourceRoute route, int knownCount)
            {
                SourceCalls++;
                if (knownCount >= 0)
                {
                    KnownSourceCountObservations++;
                    KnownSourceCountTotal += knownCount;
                    if (knownCount > MaxKnownSourceCount) MaxKnownSourceCount = knownCount;
                }

                SourceStats stats;
                if (!Sources.TryGetValue(source, out stats))
                {
                    if (Sources.Count >= MaxSources)
                    {
                        CapacityBypass++;
                        return;
                    }
                    stats = new SourceStats(center, route);
                    Sources.Add(source, stats);
                    return;
                }

                stats.Record(center, route);
            }

            internal PackageSnapshot Snapshot(double elapsedMs)
            {
                PackageSnapshot snapshot = new PackageSnapshot();
                snapshot.ElapsedMs = elapsedMs;

                snapshot.ValidatorCalls = ValidatorCalls;
                snapshot.ValidatorDistinctPairs = ValidatorPairs.Count;
                snapshot.ValidatorUniqueThings = ValidatorThings.Count;
                foreach (KeyValuePair<Thing, ValidatorThingStats> pair in ValidatorThings)
                {
                    ValidatorThingStats stats = pair.Value;
                    if (stats == null) continue;
                    int scanners = stats.DistinctScanners;
                    if (scanners > 1)
                    {
                        snapshot.ValidatorMultiScannerThings++;
                        snapshot.ValidatorCrossScannerExtraPairs += scanners - 1;
                    }
                    if (scanners > snapshot.ValidatorMaxScannersPerThing)
                        snapshot.ValidatorMaxScannersPerThing = scanners;
                    if (stats.Calls > snapshot.ValidatorMaxCallsPerThing)
                        snapshot.ValidatorMaxCallsPerThing = stats.Calls;
                }

                snapshot.ReachCalls = ReachCalls;
                snapshot.ReachDistinctQueries = ReachQueries.Count;
                snapshot.ReachUniqueTargets = ReachTargets.Count;
                foreach (KeyValuePair<TargetIdentity, ReachTargetStats> pair in ReachTargets)
                {
                    ReachTargetStats stats = pair.Value;
                    if (stats == null) continue;
                    int shapes = stats.DistinctShapes;
                    if (shapes > 1)
                    {
                        snapshot.ReachMultiShapeTargets++;
                        snapshot.ReachCrossShapeExtraQueries += shapes - 1;
                    }
                    if (shapes > snapshot.ReachMaxShapesPerTarget)
                        snapshot.ReachMaxShapesPerTarget = shapes;
                }

                snapshot.SourceCalls = SourceCalls;
                snapshot.SourceUnique = Sources.Count;
                snapshot.KnownSourceCountObservations = KnownSourceCountObservations;
                snapshot.KnownSourceCountTotal = KnownSourceCountTotal;
                snapshot.MaxKnownSourceCount = MaxKnownSourceCount;

                foreach (KeyValuePair<object, SourceStats> pair in Sources)
                {
                    SourceStats stats = pair.Value;
                    if (stats == null) continue;
                    int centers = stats.DistinctCenters;
                    snapshot.SourceDistinctCenterPairs += centers;
                    if (centers > 1) snapshot.SourceMultiCenter++;
                    if (stats.RouteCount > 1) snapshot.SourceCrossRoute++;
                    if (centers > snapshot.SourceMaxCenters)
                        snapshot.SourceMaxCenters = centers;
                    if (stats.Calls > snapshot.SourceMaxCalls)
                        snapshot.SourceMaxCalls = stats.Calls;
                }

                snapshot.CapacityBypass = CapacityBypass;
                return snapshot;
            }
        }

        private sealed class ValidatorThingStats
        {
            internal int Calls;
            private readonly WorkGiver_Scanner firstScanner;
            private HashSet<WorkGiver_Scanner> extraScanners;

            internal ValidatorThingStats(WorkGiver_Scanner scanner)
            {
                firstScanner = scanner;
                Calls = 1;
            }

            internal void Record(WorkGiver_Scanner scanner)
            {
                Calls++;
                if (ReferenceEquals(scanner, firstScanner)) return;

                if (extraScanners == null)
                    extraScanners = new HashSet<WorkGiver_Scanner>(ReferenceComparer<WorkGiver_Scanner>.Instance);
                extraScanners.Add(scanner);
            }

            internal int DistinctScanners
            {
                get { return 1 + (extraScanners == null ? 0 : extraScanners.Count); }
            }
        }

        private sealed class ReachTargetStats
        {
            internal int Calls;
            private readonly JobSearchTransaction093T20.ReachKey first;
            private HashSet<JobSearchTransaction093T20.ReachKey> extraShapes;

            internal ReachTargetStats(JobSearchTransaction093T20.ReachKey key)
            {
                first = key;
                Calls = 1;
            }

            internal void Record(JobSearchTransaction093T20.ReachKey key)
            {
                Calls++;
                if (first.Equals(key)) return;

                if (extraShapes == null)
                    extraShapes = new HashSet<JobSearchTransaction093T20.ReachKey>();
                extraShapes.Add(key);
            }

            internal int DistinctShapes
            {
                get { return 1 + (extraShapes == null ? 0 : extraShapes.Count); }
            }
        }

        private sealed class SourceStats
        {
            internal int Calls;
            private readonly IntVec3 firstCenter;
            private HashSet<IntVec3> extraCenters;
            private int routeMask;

            internal SourceStats(IntVec3 center, SourceRoute route)
            {
                Calls = 1;
                firstCenter = center;
                routeMask = (int)route;
            }

            internal void Record(IntVec3 center, SourceRoute route)
            {
                Calls++;
                routeMask |= (int)route;
                if (center == firstCenter) return;

                if (extraCenters == null)
                    extraCenters = new HashSet<IntVec3>();
                extraCenters.Add(center);
            }

            internal int DistinctCenters
            {
                get { return 1 + (extraCenters == null ? 0 : extraCenters.Count); }
            }

            internal int RouteCount
            {
                get
                {
                    int value = routeMask;
                    int count = 0;
                    while (value != 0)
                    {
                        value &= value - 1;
                        count++;
                    }
                    return count;
                }
            }
        }

        private struct TargetIdentity : IEquatable<TargetIdentity>
        {
            private readonly object reachability;
            private readonly bool hasThing;
            private readonly Thing thing;
            private readonly IntVec3 cell;

            internal TargetIdentity(JobSearchTransaction093T20.ReachKey key)
            {
                reachability = key.Reachability;
                hasThing = key.HasThing;
                thing = key.Thing;
                cell = key.Cell;
            }

            public bool Equals(TargetIdentity other)
            {
                return ReferenceEquals(reachability, other.reachability) &&
                    hasThing == other.hasThing &&
                    (hasThing ? ReferenceEquals(thing, other.thing) : cell == other.cell);
            }

            public override bool Equals(object obj)
            {
                return obj is TargetIdentity && Equals((TargetIdentity)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = reachability == null ? 0 : RuntimeHelpers.GetHashCode(reachability);
                    h = h * 397 ^ (hasThing ? (thing == null ? 0 : RuntimeHelpers.GetHashCode(thing)) : cell.GetHashCode());
                    return h;
                }
            }
        }

        private struct PackageSnapshot
        {
            internal double ElapsedMs;
            internal long ValidatorCalls;
            internal long ValidatorDistinctPairs;
            internal long ValidatorUniqueThings;
            internal long ValidatorMultiScannerThings;
            internal long ValidatorCrossScannerExtraPairs;
            internal int ValidatorMaxScannersPerThing;
            internal int ValidatorMaxCallsPerThing;

            internal long ReachCalls;
            internal long ReachDistinctQueries;
            internal long ReachUniqueTargets;
            internal long ReachMultiShapeTargets;
            internal long ReachCrossShapeExtraQueries;
            internal int ReachMaxShapesPerTarget;

            internal long SourceCalls;
            internal long SourceUnique;
            internal long SourceDistinctCenterPairs;
            internal long SourceMultiCenter;
            internal long SourceCrossRoute;
            internal int SourceMaxCenters;
            internal int SourceMaxCalls;
            internal long KnownSourceCountObservations;
            internal long KnownSourceCountTotal;
            internal int MaxKnownSourceCount;

            internal long CapacityBypass;
        }

        private sealed class AggregateBucket
        {
            private long packages;
            private double elapsedMs;

            private long validatorCalls;
            private long validatorDistinctPairs;
            private long validatorUniqueThings;
            private long validatorMultiScannerThings;
            private long validatorCrossScannerExtraPairs;
            private int validatorMaxScannersPerThing;
            private int validatorMaxCallsPerThing;

            private long reachCalls;
            private long reachDistinctQueries;
            private long reachUniqueTargets;
            private long reachMultiShapeTargets;
            private long reachCrossShapeExtraQueries;
            private int reachMaxShapesPerTarget;

            private long sourceCalls;
            private long sourceUnique;
            private long sourceDistinctCenterPairs;
            private long sourceMultiCenter;
            private long sourceCrossRoute;
            private int sourceMaxCenters;
            private int sourceMaxCalls;
            private long knownSourceCountObservations;
            private long knownSourceCountTotal;
            private int maxKnownSourceCount;

            private long capacityBypass;

            internal void Add(PackageSnapshot s)
            {
                packages++;
                elapsedMs += s.ElapsedMs;

                validatorCalls += s.ValidatorCalls;
                validatorDistinctPairs += s.ValidatorDistinctPairs;
                validatorUniqueThings += s.ValidatorUniqueThings;
                validatorMultiScannerThings += s.ValidatorMultiScannerThings;
                validatorCrossScannerExtraPairs += s.ValidatorCrossScannerExtraPairs;
                if (s.ValidatorMaxScannersPerThing > validatorMaxScannersPerThing)
                    validatorMaxScannersPerThing = s.ValidatorMaxScannersPerThing;
                if (s.ValidatorMaxCallsPerThing > validatorMaxCallsPerThing)
                    validatorMaxCallsPerThing = s.ValidatorMaxCallsPerThing;

                reachCalls += s.ReachCalls;
                reachDistinctQueries += s.ReachDistinctQueries;
                reachUniqueTargets += s.ReachUniqueTargets;
                reachMultiShapeTargets += s.ReachMultiShapeTargets;
                reachCrossShapeExtraQueries += s.ReachCrossShapeExtraQueries;
                if (s.ReachMaxShapesPerTarget > reachMaxShapesPerTarget)
                    reachMaxShapesPerTarget = s.ReachMaxShapesPerTarget;

                sourceCalls += s.SourceCalls;
                sourceUnique += s.SourceUnique;
                sourceDistinctCenterPairs += s.SourceDistinctCenterPairs;
                sourceMultiCenter += s.SourceMultiCenter;
                sourceCrossRoute += s.SourceCrossRoute;
                if (s.SourceMaxCenters > sourceMaxCenters) sourceMaxCenters = s.SourceMaxCenters;
                if (s.SourceMaxCalls > sourceMaxCalls) sourceMaxCalls = s.SourceMaxCalls;
                knownSourceCountObservations += s.KnownSourceCountObservations;
                knownSourceCountTotal += s.KnownSourceCountTotal;
                if (s.MaxKnownSourceCount > maxKnownSourceCount) maxKnownSourceCount = s.MaxKnownSourceCount;

                capacityBypass += s.CapacityBypass;
            }

            internal string Format()
            {
                long validatorRevisits = Math.Max(0L, validatorCalls - validatorUniqueThings);
                long validatorSamePairRepeats = Math.Max(0L, validatorCalls - validatorDistinctPairs);
                long reachExactRepeats = Math.Max(0L, reachCalls - reachDistinctQueries);
                long reachTargetRevisits = Math.Max(0L, reachCalls - reachUniqueTargets);
                long sourceRepeats = Math.Max(0L, sourceCalls - sourceUnique);
                long sourceSameCenterRepeats = Math.Max(0L, sourceCalls - sourceDistinctCenterPairs);

                double avgPackageMs = packages == 0 ? 0.0 : elapsedMs / packages;
                double avgKnownSourceCount = knownSourceCountObservations == 0
                    ? 0.0
                    : knownSourceCountTotal * 1.0 / knownSourceCountObservations;

                return "packages=" + packages +
                    ", avgPackageMs=" + avgPackageMs.ToString("F2") +
                    ", validator[calls=" + validatorCalls +
                    ", uniqueThings=" + validatorUniqueThings +
                    ", revisits=" + validatorRevisits +
                    "(" + Percent(validatorRevisits, validatorCalls) + ")" +
                    ", distinctScannerThingPairs=" + validatorDistinctPairs +
                    ", samePairRepeats=" + validatorSamePairRepeats +
                    "(" + Percent(validatorSamePairRepeats, validatorCalls) + ")" +
                    ", multiScannerThings=" + validatorMultiScannerThings +
                    ", crossScannerExtraPairs=" + validatorCrossScannerExtraPairs +
                    ", maxScannersPerThing=" + validatorMaxScannersPerThing +
                    ", maxCallsPerThing=" + validatorMaxCallsPerThing + "]" +
                    ", reach[calls=" + reachCalls +
                    ", uniqueQueries=" + reachDistinctQueries +
                    ", exactRepeats=" + reachExactRepeats +
                    "(" + Percent(reachExactRepeats, reachCalls) + ")" +
                    ", uniqueTargets=" + reachUniqueTargets +
                    ", targetRevisits=" + reachTargetRevisits +
                    "(" + Percent(reachTargetRevisits, reachCalls) + ")" +
                    ", multiShapeTargets=" + reachMultiShapeTargets +
                    ", crossShapeExtraQueries=" + reachCrossShapeExtraQueries +
                    ", maxShapesPerTarget=" + reachMaxShapesPerTarget + "]" +
                    ", source[calls=" + sourceCalls +
                    ", unique=" + sourceUnique +
                    ", repeats=" + sourceRepeats +
                    "(" + Percent(sourceRepeats, sourceCalls) + ")" +
                    ", sameCenterRepeats=" + sourceSameCenterRepeats +
                    "(" + Percent(sourceSameCenterRepeats, sourceCalls) + ")" +
                    ", multiCenterSources=" + sourceMultiCenter +
                    ", crossRouteSources=" + sourceCrossRoute +
                    ", maxCenters=" + sourceMaxCenters +
                    ", maxCalls=" + sourceMaxCalls +
                    ", knownCountAvg=" + avgKnownSourceCount.ToString("F1") +
                    ", knownCountMax=" + maxKnownSourceCount + "]" +
                    ", capacityBypass=" + capacityBypass;
            }

            private static string Percent(long numerator, long denominator)
            {
                if (denominator <= 0L) return "0.00%";
                return (numerator * 100.0 / denominator).ToString("F2") + "%";
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
