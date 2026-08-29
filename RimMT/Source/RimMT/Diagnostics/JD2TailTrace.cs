using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Verse;

namespace RimMT
{
    // JD2 is diagnostic-only. It records search shape and inclusive infrastructure time only while
    // the temporary WorkGiver detail capture is active. No gameplay result is changed here.
    internal static class JD2TailTrace
    {
        [ThreadStatic]
        private static PackageState current;

        internal static bool Active
        {
            get { return current != null && WorkGiverProfiler.DetailCaptureActive && RimMTThreadGuard.IsMainThread; }
        }

        internal static void BeginPackage()
        {
            if (!RimMTThreadGuard.IsMainThread)
                return;
            current = new PackageState();
        }

        internal static void EndFastPackage()
        {
            current = null;
        }

        internal static string EndSlowPackage()
        {
            PackageState state = current;
            current = null;
            return state == null ? "tailShape=<none>" : state.Summary();
        }

        internal static void ResetThreadState()
        {
            current = null;
        }

        internal static void RecordInvocation(MethodBase method, object[] args)
        {
            PackageState state = current;
            if (state == null || !WorkGiverProfiler.DetailCaptureActive || !RimMTThreadGuard.IsMainThread || method == null)
                return;

            try
            {
                Type type = method.DeclaringType;
                string typeName = type == null ? string.Empty : type.FullName;
                string name = method.Name ?? string.Empty;

                if (typeName == "Verse.GenClosest")
                {
                    if (name == "ClosestThingReachable")
                    {
                        state.ClosestReachableCalls++;
                        bool custom = args != null && args.Length > 7 && args[7] != null;
                        int count;
                        if (custom)
                        {
                            state.ClosestReachableCustomSetCalls++;
                            count = TryCollectionCount(args[7]);
                            state.RecordCustomSource(count);
                        }
                        else
                        {
                            state.ClosestReachableThingRequestCalls++;
                            count = TryThingRequestSourceCount(args);
                            state.RecordThingRequestSource(count);
                        }
                        state.RecordClosestReachableSource(count);
                        return;
                    }

                    if (name == "ClosestThing_Global")
                    {
                        state.GlobalCalls++;
                        int count = args != null && args.Length > 1 ? TryCollectionCount(args[1]) : -1;
                        bool priority = args != null && args.Length > 4 && args[4] != null;
                        state.RecordGlobalSource(count, priority);
                        return;
                    }

                    if (name == "ClosestThing_Global_Reachable")
                    {
                        state.GlobalReachableCalls++;
                        int count = args != null && args.Length > 2 ? TryCollectionCount(args[2]) : -1;
                        bool priority = args != null && args.Length > 7 && args[7] != null;
                        state.RecordGlobalSource(count, priority);
                        return;
                    }

                    state.OtherGenClosestCalls++;
                    return;
                }

                if (name == "get_PotentialWorkThingsGlobal" || name == "get_PotentialWorkCellsGlobal")
                    state.ScannerSourceGetterCalls++;
            }
            catch
            {
                state.ShapeFailures++;
            }
        }

        internal static void RecordInfrastructure(string phase, long elapsedTicks)
        {
            PackageState state = current;
            if (state == null || !WorkGiverProfiler.DetailCaptureActive || !RimMTThreadGuard.IsMainThread ||
                string.IsNullOrEmpty(phase) || elapsedTicks <= 0L)
                return;

            if (phase.StartsWith("GenClosest.", StringComparison.Ordinal))
            {
                state.GenClosestCalls++;
                state.GenClosestTicks += elapsedTicks;
            }
            else if (phase.StartsWith("RegionTraverser.", StringComparison.Ordinal))
            {
                state.RegionTraverserCalls++;
                state.RegionTraverserTicks += elapsedTicks;
            }
            else if (phase.StartsWith("Reachability.", StringComparison.Ordinal))
            {
                state.ReachabilityCalls++;
                state.ReachabilityTicks += elapsedTicks;
            }
            else if (phase.IndexOf("PotentialWorkThingsGlobal", StringComparison.Ordinal) >= 0 ||
                     phase.IndexOf("PotentialWorkCellsGlobal", StringComparison.Ordinal) >= 0)
            {
                state.ScannerSourceTimedCalls++;
                state.ScannerSourceTicks += elapsedTicks;
            }
        }

        private static int TryCollectionCount(object source)
        {
            if (source == null)
                return -1;

            ICollection nonGeneric = source as ICollection;
            if (nonGeneric != null)
                return nonGeneric.Count;

            ICollection<Thing> things = source as ICollection<Thing>;
            if (things != null)
                return things.Count;

            return -1;
        }

        private static int TryThingRequestSourceCount(object[] args)
        {
            if (args == null || args.Length < 3)
                return -1;

            Map map = args[1] as Map;
            if (map == null || map.Disposed || !(args[2] is ThingRequest))
                return -1;

            try
            {
                List<Thing> source = map.listerThings.ThingsMatching((ThingRequest)args[2]);
                return source == null ? -1 : source.Count;
            }
            catch
            {
                return -1;
            }
        }

        private sealed class PackageState
        {
            internal int ClosestReachableCalls;
            internal int ClosestReachableThingRequestCalls;
            internal int ClosestReachableCustomSetCalls;
            internal int OtherGenClosestCalls;
            internal int GlobalCalls;
            internal int GlobalReachableCalls;
            internal int GlobalPriorityCalls;
            internal int ScannerSourceGetterCalls;
            internal int ShapeFailures;

            internal long ClosestReachableSourceTotal;
            internal int ClosestReachableKnownSources;
            internal int ClosestReachableMaxSource;
            internal long ThingRequestSourceTotal;
            internal int ThingRequestKnownSources;
            internal int ThingRequestMaxSource;
            internal long CustomSourceTotal;
            internal int CustomKnownSources;
            internal int CustomMaxSource;
            internal long GlobalSourceTotal;
            internal int GlobalKnownSources;
            internal int GlobalMaxSource;

            internal int Bucket0To127;
            internal int Bucket128To255;
            internal int Bucket256To383;
            internal int Bucket384To511;
            internal int Bucket512To767;
            internal int Bucket768Plus;

            internal long GenClosestTicks;
            internal int GenClosestCalls;
            internal long RegionTraverserTicks;
            internal int RegionTraverserCalls;
            internal long ReachabilityTicks;
            internal int ReachabilityCalls;
            internal long ScannerSourceTicks;
            internal int ScannerSourceTimedCalls;

            internal void RecordClosestReachableSource(int count)
            {
                if (count < 0)
                    return;
                ClosestReachableKnownSources++;
                ClosestReachableSourceTotal += count;
                if (count > ClosestReachableMaxSource) ClosestReachableMaxSource = count;
                RecordBucket(count);
            }

            internal void RecordThingRequestSource(int count)
            {
                if (count < 0)
                    return;
                ThingRequestKnownSources++;
                ThingRequestSourceTotal += count;
                if (count > ThingRequestMaxSource) ThingRequestMaxSource = count;
            }

            internal void RecordCustomSource(int count)
            {
                if (count < 0)
                    return;
                CustomKnownSources++;
                CustomSourceTotal += count;
                if (count > CustomMaxSource) CustomMaxSource = count;
            }

            internal void RecordGlobalSource(int count, bool priority)
            {
                if (priority) GlobalPriorityCalls++;
                if (count < 0)
                    return;
                GlobalKnownSources++;
                GlobalSourceTotal += count;
                if (count > GlobalMaxSource) GlobalMaxSource = count;
            }

            private void RecordBucket(int count)
            {
                if (count < 128) Bucket0To127++;
                else if (count < 256) Bucket128To255++;
                else if (count < 384) Bucket256To383++;
                else if (count < 512) Bucket384To511++;
                else if (count < 768) Bucket512To767++;
                else Bucket768Plus++;
            }

            internal string Summary()
            {
                double ctrAvg = ClosestReachableKnownSources == 0 ? 0.0 : ClosestReachableSourceTotal / (double)ClosestReachableKnownSources;
                double normalAvg = ThingRequestKnownSources == 0 ? 0.0 : ThingRequestSourceTotal / (double)ThingRequestKnownSources;
                double customAvg = CustomKnownSources == 0 ? 0.0 : CustomSourceTotal / (double)CustomKnownSources;
                double globalAvg = GlobalKnownSources == 0 ? 0.0 : GlobalSourceTotal / (double)GlobalKnownSources;

                return "tailShape: CTR=" + ClosestReachableCalls +
                    "(thingRequest=" + ClosestReachableThingRequestCalls +
                    ",customSet=" + ClosestReachableCustomSetCalls +
                    ",known=" + ClosestReachableKnownSources +
                    ",avgSource=" + ctrAvg.ToString("F1") +
                    ",maxSource=" + ClosestReachableMaxSource +
                    ",normalAvg/max=" + normalAvg.ToString("F1") + "/" + ThingRequestMaxSource +
                    ",customAvg/max=" + customAvg.ToString("F1") + "/" + CustomMaxSource +
                    ",buckets0-127/128-255/256-383/384-511/512-767/768+=" +
                    Bucket0To127 + "/" + Bucket128To255 + "/" + Bucket256To383 + "/" + Bucket384To511 + "/" + Bucket512To767 + "/" + Bucket768Plus +
                    "), Global=" + GlobalCalls + ", GlobalReachable=" + GlobalReachableCalls +
                    "(known=" + GlobalKnownSources + ",avg/max=" + globalAvg.ToString("F1") + "/" + GlobalMaxSource +
                    ",priority=" + GlobalPriorityCalls + "), otherGenClosest=" + OtherGenClosestCalls +
                    ", scannerSourceGetters=" + ScannerSourceGetterCalls +
                    ", infraMs[GenClosest=" + ToMs(GenClosestTicks).ToString("F3") + "/" + GenClosestCalls +
                    ",RegionTraverser=" + ToMs(RegionTraverserTicks).ToString("F3") + "/" + RegionTraverserCalls +
                    ",Reachability=" + ToMs(ReachabilityTicks).ToString("F3") + "/" + ReachabilityCalls +
                    ",ScannerSource=" + ToMs(ScannerSourceTicks).ToString("F3") + "/" + ScannerSourceTimedCalls +
                    "], shapeFailures=" + ShapeFailures;
            }

            private static double ToMs(long ticks)
            {
                return ticks * 1000.0 / Stopwatch.Frequency;
            }
        }
    }
}
