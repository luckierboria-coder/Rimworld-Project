using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class GenerateNewPathForeignPatches093T11
    {
        private const string VfeOwner = "OskarPotocki.VFECore";
        private const string PfOwner = "pathfinding.framework";

        internal static void Apply(Harmony harmony)
        {
            bool vfeFound = false, vfeTimed = false, pfFound = false, pfTimed = false;
            if (harmony == null)
            {
                GenerateNewPathAttribution093T11.SetInstallState(false, false, false, false);
                return;
            }

            try
            {
                MethodBase generate = AccessTools.Method(typeof(Pawn_PathFollower), "GenerateNewPath");
                Patches info = generate == null ? null : Harmony.GetPatchInfo(generate);
                Patch vfePatch = null;
                Patch pfPatch = null;

                if (info != null)
                {
                    if (info.Prefixes != null)
                    {
                        foreach (Patch patch in info.Prefixes)
                        {
                            if (patch == null || patch.PatchMethod == null) continue;
                            MethodInfo pm = patch.PatchMethod;
                            string typeName = pm.DeclaringType == null ? string.Empty : pm.DeclaringType.FullName ?? string.Empty;
                            if (vfePatch == null && string.Equals(patch.owner, VfeOwner, StringComparison.Ordinal) &&
                                string.Equals(pm.Name, "GenerateNewPath_Prefix", StringComparison.Ordinal) &&
                                typeName.IndexOf("PhasingPatches", StringComparison.OrdinalIgnoreCase) >= 0)
                                vfePatch = patch;
                        }
                    }

                    if (info.Postfixes != null)
                    {
                        foreach (Patch patch in info.Postfixes)
                        {
                            if (patch == null || patch.PatchMethod == null) continue;
                            MethodInfo pm = patch.PatchMethod;
                            string typeName = pm.DeclaringType == null ? string.Empty : pm.DeclaringType.FullName ?? string.Empty;
                            if (pfPatch == null && string.Equals(patch.owner, PfOwner, StringComparison.Ordinal) &&
                                string.Equals(pm.Name, "Postfix", StringComparison.Ordinal) &&
                                typeName.IndexOf("PathDebugging", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                typeName.IndexOf("GenerateNewPath", StringComparison.OrdinalIgnoreCase) >= 0)
                                pfPatch = patch;
                        }
                    }
                }

                vfeFound = vfePatch != null && vfePatch.PatchMethod != null;
                pfFound = pfPatch != null && pfPatch.PatchMethod != null;
                if (vfeFound) vfeTimed = PatchDirect(harmony, vfePatch.PatchMethod, nameof(VfePrefix), nameof(VfePostfix));
                if (pfFound) pfTimed = PatchDirect(harmony, pfPatch.PatchMethod, nameof(PfPrefix), nameof(PfPostfix));
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T11 GenerateNewPath foreign attribution setup failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }

            GenerateNewPathAttribution093T11.SetInstallState(vfeFound, vfeTimed, pfFound, pfTimed);
            Log.Message("[RimMT] T11 GenerateNewPath attribution installed: VFE=" + vfeFound + "/" + vfeTimed +
                ", PF=" + pfFound + "/" + pfTimed + ". Diagnostic-only; existing owner order/results unchanged.");
        }

        private static bool PatchDirect(Harmony harmony, MethodBase target, string prefixName, string postfixName)
        {
            if (target == null) return false;
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(GenerateNewPathForeignPatches093T11), prefixName) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(GenerateNewPathForeignPatches093T11), postfixName) { priority = Priority.Last });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T11 direct foreign timer failed closed for " + target.DeclaringType + "." + target.Name + ": " + ex.GetType().Name);
                return false;
            }
        }

        public static void VfePrefix(ref long __state)
        {
            __state = GenerateNewPathAttribution093T11.BeginForeignCall();
        }

        public static void VfePostfix(long __state, bool __result)
        {
            GenerateNewPathAttribution093T11.EndVfe(__state, __result);
        }

        public static void PfPrefix(ref long __state)
        {
            __state = GenerateNewPathAttribution093T11.BeginForeignCall();
        }

        public static void PfPostfix(long __state)
        {
            GenerateNewPathAttribution093T11.EndPf(__state);
        }
    }
}
