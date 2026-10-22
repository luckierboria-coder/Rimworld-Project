using System;
using HarmonyLib;
using Verse;

namespace RimOPT
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string HarmonyId = "allen.rimopt";
        internal static string AppliedConfiguration = "尚未读取";
        internal static string InstallationState = "尚未初始化";

        static Bootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

#if RIMWORLD17
        private static bool TryInstall(string module, Action action)
        {
            try { action(); return true; }
            catch (Exception exception)
            {
                Log.Error("[RimOPT/1.7] " + module + " did not install; other modules remain independently configurable: " + exception);
                return false;
            }
        }
#endif

        private static void Install()
        {
            try
            {
                var plan = new ModulePlan(RimOptMod.Settings);
                AppliedConfiguration = plan.ToString();
#if !RIMWORLD17
                // Legacy 1.6 guard only. 1.7 has independently gated Harmony 3
                // modules; a historical RimMT diagnostic mod must not disable
                // all RimOPT optimizations and the optional sampler.
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    string id = mod == null ? null : mod.PackageIdPlayerFacing;
                    if (!string.IsNullOrEmpty(id) &&
                        id.StartsWith("allen.rimmt", StringComparison.OrdinalIgnoreCase) &&
                        !id.Equals("allen.rimmt2", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("[RimOPT] RimMT is active. RimOPT stayed inactive to avoid duplicate work-search patches.");
                        InstallationState = "原有 RimMT 兼容检查阻止安装；模块开关未绕过该检查。";
                        return;
                    }
                }

#endif
                Harmony harmony = new Harmony(HarmonyId);
#if RIMWORLD17
                // Independent guarded installs: scope-dependent modules need a valid work-package hook.
                bool scoped = !plan.NeedsScope || TryInstall("PackageScope", () => PackageScope.Install(harmony));
                if (plan.Diagnostics) TryInstall("PulseProfiler", () => PulseProfiler.Install(harmony));
                if (scoped)
                {
                    if (plan.Reach) TryInstall("ReachabilityMemo", () => ReachabilityMemo.Install(harmony));
                    if (plan.Bill) TryInstall("DoBillIndex", () => DoBillIndex.Install(harmony));
                    if (plan.Merge) TryInstall("HaulMergeIndex", () => HaulMergeIndex.Install(harmony));
                    if (plan.Tail) TryInstall("WorkSearchTailPlans", () => WorkSearchTailPlans.Install(harmony));
                }
                else Log.Warning("[RimOPT/1.7] Work-package scope unavailable; scope-dependent modules were not installed.");
                if (plan.Roof || plan.Harvest || plan.Snow)
                    TryInstall("WorkSourcePruners", () => WorkSourcePruners.Install(harmony, plan.Roof, plan.Harvest, plan.Snow));
#else
                plan.Apply(
                    () => PackageScope.Install(harmony),
                    () => ReachabilityMemo.Install(harmony),
                    () => DoBillIndex.Install(harmony),
                    () => HaulMergeIndex.Install(harmony),
                    () => WorkSearchTailPlans.Install(harmony),
                    (roof, harvest, snow) => WorkSourcePruners.Install(harmony, roof, harvest, snow));
#endif
                InstallationState = ReachabilityMemo.Status + "; " + DoBillIndex.Status + "; " + HaulMergeIndex.Status + "; " + WorkSourcePruners.Status + "; " + WorkSearchTailPlans.Status;
#if RIMWORLD17
                if (plan.Diagnostics) InstallationState += "; pulse[" + PulseProfiler.Status + "]";
#endif
                Log.Message("[RimOPT] configured: " + AppliedConfiguration);
                Log.Message("[RimOPT] installation state. " + ReachabilityMemo.Status + "; " +
                    DoBillIndex.Status + "; " +
                    HaulMergeIndex.Status + "; " + WorkSourcePruners.Status +
                    "; " + WorkSearchTailPlans.Status +
                    ". Tail search and distance plans stay inside one synchronous Work package.");
            }
            catch (Exception ex)
            {
                InstallationState = "初始化异常：" + ex.GetType().Name + "；检查日志，前面已安装的模块可能仍在。";
                Log.Error("[RimOPT] initialization failed; no further patches were attempted: " + ex);
            }
        }
    }
}
