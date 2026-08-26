using System;
using System.Collections.Generic;
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
        private static readonly object Sync = new object();
        private static Harmony harmony;
        private static MethodBase jobPackageTarget;
        private static bool candidatesDiscovered;
        private static int active;
        private static int stopRequested;
        private static int patchFailures;

        internal static bool CaptureActive { get { return Volatile.Read(ref active) != 0; } }
        internal static int PackagesRemaining { get { return WorkGiverProfiler.PackagesRemaining; } }

        internal static void Initialize(Harmony owner)
        {
            harmony = owner;
            FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
            Log.Message("[RimMT] diagnostics.jobGiverDetail V0.4.5.2 is on-demand. No per-WorkGiver phase detours are resident during normal play; use Mod Settings to start a bounded capture.");
        }

        internal static bool StartCapture()
        {
            if (!RimMTThreadGuard.IsMainThread || harmony == null || CaptureActive)
                return false;

            lock (Sync)
            {
                if (CaptureActive)
                    return false;

                if (!EnsureCandidates())
                    return false;

                int patched = 0;
                patchFailures = 0;
                MethodInfo prefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Prefix));
                MethodInfo postfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Postfix));
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
                    Log.Warning("[RimMT] JobGiver detail capture could not patch the package scope. Capture was not started. " + ex.GetType().Name + ": " + ex.Message);
                    return false;
                }

                for (int i = 0; i < CandidateMethods.Count; i++)
                {
                    MethodBase method = CandidateMethods[i];
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
                        if (patchFailures <= 5)
                            Log.Warning("[RimMT] JobGiver detail capture skipped " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                Interlocked.Exchange(ref stopRequested, 0);
                Interlocked.Exchange(ref active, 1);
                FeatureGate.SetEnabled("diagnostics.jobGiverDetail", true);
                WorkGiverProfiler.StartSession(CaptureJobPackages, patched, patchFailures);
                Log.Message("[RimMT] JobGiver detail capture started for up to " + CaptureJobPackages + " outer TryIssueJobPackage calls; temporarily patched " + patched + "/" + CandidateMethods.Count + " WorkGiver phase methods. It will auto-unpatch when complete.");
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
            if (!RimMTThreadGuard.IsMainThread || Interlocked.Exchange(ref stopRequested, 0) == 0)
                return;
            StopCaptureNow();
        }

        private static void StopCaptureNow()
        {
            lock (Sync)
            {
                if (harmony == null)
                    return;

                MethodInfo prefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Prefix));
                MethodInfo postfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(Postfix));
                MethodInfo packagePrefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(JobPackagePrefix));
                MethodInfo packageFinalizerMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), nameof(JobPackageFinalizer));

                try
                {
                    if (jobPackageTarget != null)
                    {
                        harmony.Unpatch(jobPackageTarget, packagePrefixMethod);
                        harmony.Unpatch(jobPackageTarget, packageFinalizerMethod);
                    }
                    for (int i = 0; i < CandidateMethods.Count; i++)
                    {
                        harmony.Unpatch(CandidateMethods[i], prefixMethod);
                        harmony.Unpatch(CandidateMethods[i], postfixMethod);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimMT] JobGiver detail capture unpatch encountered " + ex.GetType().Name + ": " + ex.Message + ". Gameplay remains vanilla-authoritative.");
                }

                WorkGiverProfiler.StopSession();
                FeatureGate.SetEnabled("diagnostics.jobGiverDetail", false);
                Log.Message("[RimMT] JobGiver detail capture stopped and temporary WorkGiver detours were removed. " + WorkGiverProfiler.Summary(12));
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
                    Log.Warning("[RimMT] JobGiver detail capture unavailable: JobGiver_Work.TryIssueJobPackage was not found.");
                    return false;
                }

                HashSet<MethodBase> unique = new HashSet<MethodBase>();
                List<Type> allTypes = GenTypes.AllTypes;
                for (int i = 0; i < allTypes.Count; i++)
                {
                    Type type = allTypes[i];
                    if (type == null || !typeof(WorkGiver).IsAssignableFrom(type))
                        continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int m = 0; m < methods.Length; m++)
                    {
                        MethodInfo method = methods[m];
                        if (method == null || method.IsAbstract || !TargetNames.Contains(method.Name) || !IsUsefulSignature(method) || !unique.Add(method))
                            continue;
                        CandidateMethods.Add(method);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] JobGiver detail candidate discovery failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            return CandidateMethods.Count > 0;
        }

        public static void JobPackagePrefix(ref WorkGiverProfiler.JobPackageScope __state)
        {
            __state = WorkGiverProfiler.BeginJobPackage();
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
