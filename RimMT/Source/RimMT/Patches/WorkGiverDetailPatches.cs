using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class WorkGiverDetailPatches
    {
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

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            PatchJobPackageScope(harmony);

            HashSet<MethodBase> patched = new HashSet<MethodBase>();
            int candidates = 0;
            int failures = 0;

            List<Type> allTypes;
            try
            {
                allTypes = GenTypes.AllTypes;
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("diagnostics.jobGiverDetail", "type enumeration failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] diagnostics.jobGiverDetail disabled: could not enumerate loaded types. " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

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
                    if (method == null || method.IsAbstract || !TargetNames.Contains(method.Name) || !IsUsefulSignature(method))
                        continue;
                    if (!patched.Add(method))
                        continue;

                    candidates++;
                    try
                    {
                        HarmonyMethod prefix = new HarmonyMethod(typeof(WorkGiverDetailPatches), nameof(Prefix));
                        prefix.priority = Priority.First;
                        HarmonyMethod postfix = new HarmonyMethod(typeof(WorkGiverDetailPatches), nameof(Postfix));
                        postfix.priority = Priority.Last;
                        harmony.Patch(method, prefix: prefix, postfix: postfix);
                        WorkGiverProfiler.NotePatchedMethod();
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        WorkGiverProfiler.NotePatchFailure();
                        if (failures <= 5)
                            Log.Warning("[RimMT] diagnostics.jobGiverDetail skipped " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            if (candidates == 0)
            {
                FeatureGate.Suppress("diagnostics.jobGiverDetail", "no WorkGiver methods were found");
                Log.Warning("[RimMT] diagnostics.jobGiverDetail found no compatible WorkGiver methods.");
            }
            else
            {
                Log.Message("[RimMT] diagnostics.jobGiverDetail patched " + (candidates - failures) + "/" + candidates + " WorkGiver phase methods. V0.4.5.1 samples 1/32 job-package scopes and bursts after >=64ms calls to reduce profiler-induced microstutter; gameplay remains vanilla-authoritative.");
            }
        }

        private static void PatchJobPackageScope(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (target == null)
                {
                    FeatureGate.Suppress("diagnostics.jobGiverDetail", "JobGiver_Work.TryIssueJobPackage was not found");
                    Log.Warning("[RimMT] diagnostics.jobGiverDetail disabled: JobGiver_Work.TryIssueJobPackage was not found.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(WorkGiverDetailPatches), nameof(JobPackagePrefix));
                prefix.priority = Priority.First;
                HarmonyMethod finalizer = new HarmonyMethod(typeof(WorkGiverDetailPatches), nameof(JobPackageFinalizer));
                finalizer.priority = Priority.Last;
                harmony.Patch(target, prefix: prefix, finalizer: finalizer);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("diagnostics.jobGiverDetail", "job-package sampling scope patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] diagnostics.jobGiverDetail disabled: job-package sampling scope patch failed. " + ex.GetType().Name + ": " + ex.Message);
            }
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
