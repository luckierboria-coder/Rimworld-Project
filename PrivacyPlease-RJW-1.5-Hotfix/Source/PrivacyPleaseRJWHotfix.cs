using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Allen.PrivacyPleaseRJW15Hotfix
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        private const string HarmonyId = "Allen.PrivacyPleaseRJW15Hotfix";
        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);
                int patched = 0;
                patched += PatchPrivacyHandler(harmony, "Privacy_Please.HarmonyPatch_JobDriver_Sex_setup_ticks", "Postfix", "setup_ticks");
                patched += PatchPrivacyHandler(harmony, "Privacy_Please.HarmonyPatch_JobDriver_Sex_SexTick", "Postfix", "SexTick");
                if (patched > 0)
                    Log.Message("[Privacy Please - RJW 1.5 Hotfix] Active. Guarded " + patched + " Privacy Please handler(s). Only NullReferenceException thrown inside those handlers is suppressed; RJW remains authoritative.");
                else
                    Log.Warning("[Privacy Please - RJW 1.5 Hotfix] Privacy Please target handlers were not found. No patch was applied. Check Privacy Please version/load order.");
            }
            catch (Exception ex)
            {
                Log.Error("[Privacy Please - RJW 1.5 Hotfix] Failed to install: " + ex);
            }
        }

        private static int PatchPrivacyHandler(Harmony harmony, string typeName, string methodName, string label)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null) { Log.Warning("[Privacy Please - RJW 1.5 Hotfix] Type not found: " + typeName); return 0; }
            MethodInfo target = AccessTools.Method(type, methodName);
            if (target == null) { Log.Warning("[Privacy Please - RJW 1.5 Hotfix] Method not found: " + typeName + "." + methodName); return 0; }
            var finalizer = new HarmonyMethod(typeof(PrivacyHandlerGuard), "Finalizer");
            finalizer.priority = Priority.Last;
            harmony.Patch(target, finalizer: finalizer);
            PrivacyHandlerGuard.RegisterLabel(target, label);
            return 1;
        }
    }

    public static class PrivacyHandlerGuard
    {
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static readonly Dictionary<MethodBase, string> Labels = new Dictionary<MethodBase, string>();
        public static void RegisterLabel(MethodBase method, string label) { if (method != null) Labels[method] = label; }
        public static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!(__exception is NullReferenceException)) return __exception;
            string label = "Privacy Please handler";
            string knownLabel;
            if (__originalMethod != null && Labels.TryGetValue(__originalMethod, out knownLabel)) label = knownLabel;
            string key = __originalMethod != null ? (__originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.FullName : "<unknown>") + "." + __originalMethod.Name : label;
            if (Logged.Add(key))
                Log.Warning("[Privacy Please - RJW 1.5 Hotfix] Suppressed one NullReferenceException from " + label + ". The current RJW job is allowed to continue. This warning is shown once per guarded handler. Original exception: " + __exception.Message);
            return null;
        }
    }
}
