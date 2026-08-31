using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTRC2T2
{
    /// <summary>
    /// Bounded classifier for the gap-dominant portion discovered by PreTail Structure V0.1.
    ///
    /// It deliberately targets only four previously observed hotspot families:
    /// PickUpAndHaul, WorkGiver_DoBill, ProcessorFramework FillProcessor, and Refuel.
    /// The Harmony surface is therefore small and fixed. Calls are timed only after the
    /// current JobGiver_Work package has already crossed 32ms; ordinary packages pay only
    /// the wrapper/family check and never touch global counters.
    ///
    /// Timings are inclusive: nested WorkGiver calls can overlap. This is diagnostic
    /// ranking telemetry, not an accounting profiler. Gameplay results are never changed.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class GapClassifier
    {
        private const string HarmonyId = "allen.rimmt";
        private const int FamilyCount = 4;
        private const int CategoryCount = 8;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

        private static readonly string[] FamilyNames =
        {
            "PickUpAndHaul",
            "DoBill",
            "ProcessorFill",
            "Refuel"
        };

        private static readonly string[] CategoryNames =
        {
            "HasThing",
            "JobThing",
            "ShouldSkip",
            "PotentialThings",
            "PotentialCells",
            "HasCell",
            "JobCell",
            "Other"
        };

        private static Type pickUpAndHaulType;
        private static Type doBillType;
        private static Type processorFillType;
        private static Type refuelType;

        private static readonly long[] Calls = new long[FamilyCount * CategoryCount];
        private static readonly long[] Ticks = new long[FamilyCount * CategoryCount];
        private static readonly long[] MaxTicks = new long[FamilyCount * CategoryCount];
        private static readonly long[] FamilyCalls = new long[FamilyCount];
        private static readonly long[] FamilyTicks = new long[FamilyCount];
        private static readonly long[] FamilyMaxTicks = new long[FamilyCount];

        private static bool installed;
        private static int patchedMethods;
        private static long failures;

        public struct TimingState
        {
            public long Started;
            public int Family;
            public int Category;
        }

        static GapClassifier()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;

            try
            {
                pickUpAndHaulType = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
                doBillType = typeof(WorkGiver_DoBill);
                processorFillType = AccessTools.TypeByName("ProcessorFramework.WorkGiver_FillProcessor");
                refuelType = typeof(WorkGiver_Refuel);

                HashSet<MethodBase> targets = new HashSet<MethodBase>();
                AddFamilyMethods(targets, pickUpAndHaulType);
                AddFamilyMethods(targets, doBillType);
                AddFamilyMethods(targets, processorFillType);
                AddFamilyMethods(targets, refuelType);

                HarmonyMethod prefix = new HarmonyMethod(typeof(GapClassifier), nameof(MethodPrefix)) { priority = Priority.First };
                HarmonyMethod finalizer = new HarmonyMethod(typeof(GapClassifier), nameof(MethodFinalizer)) { priority = Priority.Last };
                foreach (MethodBase method in targets)
                {
                    if (method == null) continue;
                    Harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    patchedMethods++;
                }

                Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
                if (report != null)
                    Harmony.Patch(report, postfix: new HarmonyMethod(typeof(GapClassifier), nameof(ReportPostfix)) { priority = Priority.Last });

                Log.Message("[RimMT] RC2-T2 Gap Classifier V0.1 installed: tail-only >=32ms telemetry for PickUpAndHaul/DoBill/ProcessorFill/Refuel. No gameplay authority or admission threshold is changed.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 Gap Classifier failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void AddFamilyMethods(HashSet<MethodBase> targets, Type type)
        {
            if (type == null) return;
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                Interlocked.Increment(ref failures);
                return;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.IsAbstract || method.ContainsGenericParameters) continue;
                if (ClassifyMethod(method.Name) < 0) continue;
                targets.Add(method);
            }
        }

        private static int ClassifyMethod(string name)
        {
            if (name == "HasJobOnThing") return 0;
            if (name == "JobOnThing") return 1;
            if (name == "ShouldSkip") return 2;
            if (name == "PotentialWorkThingsGlobal") return 3;
            if (name == "PotentialWorkCellsGlobal") return 4;
            if (name == "HasJobOnCell") return 5;
            if (name == "JobOnCell") return 6;
            if (name == "NonScanJob") return 7;
            return -1;
        }

        private static int ClassifyFamily(object instance)
        {
            if (instance == null) return -1;
            Type type = instance.GetType();
            if (pickUpAndHaulType != null && pickUpAndHaulType.IsAssignableFrom(type)) return 0;
            if (doBillType != null && doBillType.IsAssignableFrom(type)) return 1;
            if (processorFillType != null && processorFillType.IsAssignableFrom(type)) return 2;
            if (refuelType != null && refuelType.IsAssignableFrom(type)) return 3;
            return -1;
        }

        public static void MethodPrefix(object __instance, MethodBase __originalMethod, out TimingState __state)
        {
            __state = default(TimingState);
            if (__instance == null || __originalMethod == null) return;

            int family = ClassifyFamily(__instance);
            if (family < 0) return;

            int category = ClassifyMethod(__originalMethod.Name);
            if (category < 0) return;

            long now;
            if (!PreTailStructureProfiler.TryGetTailTimestamp(out now)) return;

            __state.Started = now;
            __state.Family = family;
            __state.Category = category;
        }

        public static Exception MethodFinalizer(Exception __exception, TimingState __state)
        {
            if (__state.Started == 0L) return __exception;

            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            if (elapsed <= 0L) return __exception;

            int family = __state.Family;
            int category = __state.Category;
            if ((uint)family >= FamilyCount || (uint)category >= CategoryCount) return __exception;

            int index = family * CategoryCount + category;
            Interlocked.Increment(ref Calls[index]);
            Interlocked.Add(ref Ticks[index], elapsed);
            UpdateMax(ref MaxTicks[index], elapsed);
            Interlocked.Increment(ref FamilyCalls[family]);
            Interlocked.Add(ref FamilyTicks[family], elapsed);
            UpdateMax(ref FamilyMaxTicks[family], elapsed);
            return __exception;
        }

        private static void UpdateMax(ref long target, long value)
        {
            long current = Interlocked.Read(ref target);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 Gap Classifier V0.1: patched=" + patchedMethods +
                ", available(PUAH/DoBill/ProcessorFill/Refuel)=" +
                (pickUpAndHaulType != null) + "/" + (doBillType != null) + "/" + (processorFillType != null) + "/" + (refuelType != null) +
                ", timings are inclusive and only begin after the live JobPackage crosses 32ms, failures=" + Interlocked.Read(ref failures) + ".");

            for (int family = 0; family < FamilyCount; family++)
            {
                long familyCalls = Interlocked.Read(ref FamilyCalls[family]);
                long familyTicks = Interlocked.Read(ref FamilyTicks[family]);
                long familyMax = Interlocked.Read(ref FamilyMaxTicks[family]);
                double totalMs = familyTicks * TimestampToMs;
                double avgUs = familyCalls == 0 ? 0.0 : totalMs * 1000.0 / familyCalls;
                double maxMs = familyMax * TimestampToMs;

                string cats = string.Empty;
                for (int category = 0; category < CategoryCount; category++)
                {
                    int index = family * CategoryCount + category;
                    long calls = Interlocked.Read(ref Calls[index]);
                    long ticks = Interlocked.Read(ref Ticks[index]);
                    if (category != 0) cats += ",";
                    cats += CategoryNames[category] + "=" + calls + ":" + (ticks * TimestampToMs).ToString("F2") + "ms";
                }

                Log.Message("[RimMT]   GapFamily " + FamilyNames[family] +
                    ": calls=" + familyCalls +
                    ", inclusiveTotalMs=" + totalMs.ToString("F2") +
                    ", avgUs=" + avgUs.ToString("F1") +
                    ", maxCallMs=" + maxMs.ToString("F2") +
                    ", cats(" + cats + ").");
            }
        }
    }
}
