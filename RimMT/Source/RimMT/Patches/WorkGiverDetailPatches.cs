using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class WorkGiverDetailPatches
    {
        private const int CaptureJobPackages = 32;
        private const long AutoTriggerMinMainThreadFrames = 600;
        private static readonly long AutoTriggerThresholdTicks = Math.Max(1L, Stopwatch.Frequency * 64L / 1000L);

        private static readonly HashSet<string> TargetNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ShouldSkip",
            "NonScanJob",
            "HasJobOnThing",
            "HasJobOnCell",
            "JobOnThing",
            "JobOnCell",
            "GetPriority"
        };

        private static readonly List<MethodBase> CandidateMethods = new List<MethodBase>();
        private static readonly List<MethodBase> InfrastructureMethods = new List<MethodBase>();
        private static readonly object Sync = new object();
        private static Harmony harmony;
        private static MethodBase jobPackageTarget;
        private static bool candidatesDiscovered;
        private static int active;
        private static int stopRequested;
        private static int autoStartRequested;
        private static int autoTriggered;
        private static long autoTriggerElapsedTicks;
        private static int patchFailures;

        internal static bool CaptureActive { get { return Volatile.Read(ref active) != 0; } }
        internal static bool AutoTraceArmed { get { return Volatile.Read(ref autoTriggered) == 0 && !CaptureActive; } }
        internal static int PackagesRemaining { get { return WorkGiverProfiler.PackagesRemaining; } }

        internal static void Initialize(Harmony owner)
        {
            harmony = owner;
            FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
            Log.Message("[RimMT] JD1 AutoTrace armed on JS1.2.1 Hybrid. After " + AutoTriggerMinMainThreadFrames +
                " main-thread frames, the first JobGiver package >=64ms arms a 32-package detail capture on the next frame. " +
                "The slow trigger package itself is not instrumented. Capture includes bounded WorkGiver, GenClosest, Reachability, RegionTraverser and scanner-source timings, then auto-unpatches.");
        }

        internal static void ObserveJobGiver(long started)
        {
            if (started == 0L || !AutoTraceArmed || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;
            if (RimMTRuntime.MainThreadFrames < AutoTriggerMinMainThreadFrames)
                return;

            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed < AutoTriggerThresholdTicks)
                return;

            if (Interlocked.CompareExchange(ref autoTriggered, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref autoTriggerElapsedTicks, elapsed);
            Interlocked.Exchange(ref autoStartRequested, 1);
            Log.Message("[RimMT] JD1 AutoTrace observed slow JobGiver package " +
                (elapsed * 1000.0 / Stopwatch.Frequency).ToString("F3") +
                "ms at mainThreadFrame=" + RimMTRuntime.MainThreadFrames +
                ". Detail capture will start on the next safe main-thread frame for the following " + CaptureJobPackages + " outer packages.");
        }

        internal static bool StartCapture()
        {
            if (!RimMTThreadGuard.IsMainThread || harmony == null || CaptureActive)
                return false;

            lock (Sync)
            {
                if (CaptureActive)
                    return false;

                // A manual capture also consumes the one-shot auto trigger so the diagnostic build
                // never installs a second detail session later in the same process.
                Interlocked.Exchange(ref autoTriggered, 1);
                Interlocked.Exchange(ref autoStartRequested, 0);

                if (!EnsureCandidates())
                    return false;

                int patched = 0;
                patchFailures = 0;
                MethodInfo prefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Prefix));
                MethodInfo postfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Postfix));
                MethodInfo infraPrefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(InfrastructurePrefix));
                MethodInfo infraPostfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(InfrastructurePostfix));
                MethodInfo packagePrefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(JobPackagePrefix));
                MethodInfo packageFinalizerMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(JobPackageFinalizer));

                try
                {
                    HarmonyMethod packagePrefix = new HarmonyMethod(packagePrefixMethod) { priority = Priority.First };
                    HarmonyMethod packageFinalizer = new HarmonyMethod(packageFinalizerMethod) { priority = Priority.Last };
                    harmony.Patch(jobPackageTarget, prefix: packagePrefix, finalizer: packageFinalizer);
                }
                catch (Exception ex)
                {
                    FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
                    Log.Warning("[RimMT] JD1 detail capture could not patch the package scope. Capture was not started. " + ex.GetType().Name + ": " + ex.Message);
                    return false;
                }

                patched += PatchMethods(CandidateMethods, prefixMethod, postfixMethod, "WorkGiver phase");
                patched += PatchMethods(InfrastructureMethods, infraPrefixMethod, infraPostfixMethod, "infrastructure");

                Interlocked.Exchange(ref stopRequested, 0);
                Interlocked.Exchange(ref active, 1);
                FeatureGate.SetEnabled("diagnostics.jobGiverDetail", true);
                WorkGiverProfiler.StartSession(CaptureJobPackages, patched, patchFailures);

                long triggerTicks = Interlocked.Read(ref autoTriggerElapsedTicks);
                string triggerText = triggerTicks <= 0L ? "manual" :
                    (triggerTicks * 1000.0 / Stopwatch.Frequency).ToString("F3") + "ms slow-package trigger";
                Log.Message("[RimMT] JD1 detail capture started (" + triggerText + ") for up to " + CaptureJobPackages +
                    " outer TryIssueJobPackage calls; temporarily patched " + CandidateMethods.Count +
                    " WorkGiver phase candidates and " + InfrastructureMethods.Count +
                    " infrastructure candidates. It will auto-unpatch when complete.");
                return true;
            }
        }

        internal static void RequestStopCapture()
        {
            if (!CaptureActive)
                return;
            Interlocked.Exchange(ref active, 0);
            FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
            Interlocked.Exchange(ref stopRequested, 1);
        }

        internal static void OnMainThreadFrame()
        {
            if (!RimMTThreadGuard.IsMainThread)
                return;

            if (Interlocked.Exchange(ref stopRequested, 0) != 0)
                StopCaptureNow();

            if (Interlocked.Exchange(ref autoStartRequested, 0) != 0 && !CaptureActive)
            {
                if (!StartCapture())
                    Log.Warning("[RimMT] JD1 AutoTrace was triggered but detail capture could not start. Gameplay remains unchanged.");
            }
        }

        private static int PatchMethods(List<MethodBase> methods, MethodInfo prefixMethod, MethodInfo postfixMethod, string kind)
        {
            int patched = 0;
            for (int i = 0; i < methods.Count; i++)
            {
                MethodBase method = methods[i];
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First };
                    HarmonyMethod postfix = new HarmonyMethod(postfixMethod) { priority = Priority.Last };
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    patched++;
                }
                catch (Exception ex)
                {
                    patchFailures++;
                    if (patchFailures <= 8)
                        Log.Warning("[RimMT] JD1 detail capture skipped " + kind + " method " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            return patched;
        }

        private static void StopCaptureNow()
        {
            lock (Sync)
            {
                if (harmony == null)
                    return;

                MethodInfo prefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Prefix));
                MethodInfo postfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Postfix));
                MethodInfo infraPrefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(InfrastructurePrefix));
                MethodInfo infraPostfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(InfrastructurePostfix));
                MethodInfo packagePrefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(JobPackagePrefix));
                MethodInfo packageFinalizerMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(JobPackageFinalizer));

                try
                {
                    if (jobPackageTarget != null)
                    {
                        harmony.Unpatch(jobPackageTarget, packagePrefixMethod);
                        harmony.Unpatch(jobPackageTarget, packageFinalizerMethod);
                    }
                    UnpatchMethods(CandidateMethods, prefixMethod, postfixMethod);
                    UnpatchMethods(InfrastructureMethods, infraPrefixMethod, infraPostfixMethod);
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimMT] JD1 detail capture unpatch encountered " + ex.GetType().Name + ": " + ex.Message + ". Gameplay remains vanilla-authoritative.");
                }

                WorkGiverProfiler.StopSession();
                FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
                Log.Message("[RimMT] JD1 DETAIL CAPTURE COMPLETE; temporary detours removed. " +
                    WorkGiverProfiler.Summary(12) + "\n" + JobGiverInfrastructureProfiler.Summary(12));
            }
        }

        private static void UnpatchMethods(List<MethodBase> methods, MethodInfo prefix, MethodInfo postfix)
        {
            for (int i = 0; i < methods.Count; i++)
            {
                harmony.Unpatch(methods[i], prefix);
                harmony.Unpatch(methods[i], postfix);
            }
        }

        private static bool EnsureCandidates()
        {
            if (candidatesDiscovered)
                return jobPackageTarget != null && CandidateMethods.Count > 0;

            candidatesDiscovered = true;
            try
            {
                jobPackageTarget = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (jobPackageTarget == null)
                {
                    Log.Warning("[RimMT] JD1 detail capture unavailable: JobGiver_Work.TryIssueJobPackage was not found.");
                    return false;
                }

                DiscoverWorkGiverMethods();
                DiscoverInfrastructureMethods();
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] JD1 detail candidate discovery failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            return CandidateMethods.Count > 0;
        }

        private static void DiscoverWorkGiverMethods()
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            List<Type> allTypes = GenTypes.AllTypes;
            for (int i = 0; i < allTypes.Count; i++)
            {
                Type type = allTypes[i];
                if (type == null || !typeof(WorkGiver).IsAssignableFrom(type))
                    continue;

                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }

                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (method == null || method.IsAbstract || method.ContainsGenericParameters || !TargetNames.Contains(method.Name) || !IsUsefulSignature(method) || !unique.Add(method))
                        continue;
                    CandidateMethods.Add(method);
                }
            }
        }

        private static void DiscoverInfrastructureMethods()
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            AddNamedMethods(typeof(GenClosest), unique, delegate(MethodInfo m)
            {
                return m.Name.StartsWith("ClosestThing", StringComparison.Ordinal);
            });
            AddNamedMethods(typeof(Reachability), unique, delegate(MethodInfo m)
            {
                return m.Name == "CanReach";
            });

            Type regionTraverser = AccessTools.TypeByName("Verse.RegionTraverser");
            if (regionTraverser != null)
            {
                AddNamedMethods(regionTraverser, unique, delegate(MethodInfo m)
                {
                    return m.Name == "BreadthFirstTraverse";
                });
            }

            List<Type> allTypes = GenTypes.AllTypes;
            for (int i = 0; i < allTypes.Count; i++)
            {
                Type type = allTypes[i];
                if (type == null || !typeof(WorkGiver_Scanner).IsAssignableFrom(type))
                    continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                        continue;
                    if (method.Name != "get_PotentialWorkThingsGlobal" && method.Name != "get_PotentialWorkCellsGlobal")
                        continue;
                    if (unique.Add(method)) InfrastructureMethods.Add(method);
                }
            }
        }

        private static void AddNamedMethods(Type type, HashSet<MethodBase> unique, Predicate<MethodInfo> predicate)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.IsAbstract || method.ContainsGenericParameters || !predicate(method))
                    continue;
                if (unique.Add(method)) InfrastructureMethods.Add(method);
            }
        }

        public static void JobPackagePrefix(Pawn __0, ref WorkGiverProfiler.JobPackageScope __state)
        {
            __state = WorkGiverProfiler.BeginJobPackage(__0);
        }

        public static Exception JobPackageFinalizer(Exception __exception, WorkGiverProfiler.JobPackageScope __state)
        {
            WorkGiverProfiler.EndJobPackage(__state);
            return __exception;
        }

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

        private static bool IsUsefulSignature(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Pawn))
                return false;
            if (method.Name == "GetPriority")
                return parameters.Length == 2 && parameters[1].ParameterType == typeof(TargetInfo);
            return true;
        }
    }
}
