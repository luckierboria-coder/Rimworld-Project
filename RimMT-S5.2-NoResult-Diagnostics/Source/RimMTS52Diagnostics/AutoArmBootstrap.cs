using System;
using Verse;

namespace RimMTS52Diagnostics
{
    [StaticConstructorOnStartup]
    internal static class AutoArmBootstrap
    {
        static AutoArmBootstrap()
        {
            Log.Message("[RimMT-S5.2] Sidecar assembly loaded; scheduling automatic profiler arm after long events finish.");
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                try
                {
                    NoResultOriginProfilerS52.RuntimeReportPrefix();
                    Log.Message("[RimMT-S5.2] Auto-arm request executed. If WorkGiver patch discovery succeeded, an ARMED/report line will appear on the next RimMT runtime report.");
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimMT-S5.2] Auto-arm bootstrap failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
        }
    }
}
