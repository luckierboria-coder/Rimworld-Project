using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    [StaticConstructorOnStartup]
    internal static class ReachabilityPatchCensus
    {
        private static bool _done;

        static ReachabilityPatchCensus()
        {
            try
            {
                MethodBase update = AccessTools.Method(typeof(Root_Play), "Update");
                if (update == null)
                {
                    Log.Warning("[RimMT] Reachability patch census could not attach: Root_Play.Update not found.");
                    return;
                }

                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                HarmonyMethod postfix = new HarmonyMethod(typeof(ReachabilityPatchCensus), nameof(UpdatePostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(update, postfix: postfix);
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Reachability patch census attach failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void UpdatePostfix()
        {
            if (_done || Current.ProgramState != ProgramState.Playing)
                return;

            _done = true;
            try
            {
                MethodBase target = AccessTools.Method(typeof(Reachability), nameof(Reachability.CanReach), new Type[]
                {
                    typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms)
                });

                if (target == null)
                {
                    Log.Warning("[RimMT] Reachability patch census target overload not found.");
                    return;
                }

                Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                {
                    Log.Message("[RimMT] Reachability patch census: no Harmony patches found on target.");
                    return;
                }

                Log.Message("[RimMT] Reachability patch census BEGIN target=" + DescribeMethod(target));
                Dump("PREFIX", info.Prefixes);
                Dump("POSTFIX", info.Postfixes);
                Dump("TRANSPILER", info.Transpilers);
                Dump("FINALIZER", info.Finalizers);
                Log.Message("[RimMT] Reachability patch census END");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Reachability patch census failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Dump(string kind, IList<Patch> patches)
        {
            if (patches == null || patches.Count == 0)
            {
                Log.Message("[RimMT] Reachability patch census " + kind + ": <none>");
                return;
            }

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null) continue;

                string owner = string.IsNullOrEmpty(patch.owner) ? "<unknown>" : patch.owner;
                string method = DescribeMethod(patch.PatchMethod);
                string before = Join(patch.before);
                string after = Join(patch.after);
                Log.Message("[RimMT] Reachability patch census " + kind + "[" + i + "] owner='" + owner + "' priority=" + patch.priority + " method=" + method + " before=[" + before + "] after=[" + after + "]");
            }
        }

        private static string DescribeMethod(MethodBase method)
        {
            if (method == null) return "<null>";
            string typeName = method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName;
            return typeName + "." + method.Name;
        }

        private static string Join(string[] values)
        {
            if (values == null || values.Length == 0) return string.Empty;
            return string.Join(",", values);
        }
    }
}
