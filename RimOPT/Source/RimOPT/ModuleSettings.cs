using System;
using Verse;

namespace RimOPT
{
    public sealed class RimOptSettings : ModSettings
    {
        // In 1.7 start all modules disabled pending in-game validation.
        // 1.6 keeps its original defaults and persistence keys.
#if RIMWORLD17
        public bool PulseDiagnosticsEnabled = false;
        public bool ReachabilityMemoEnabled = false;
        public bool DoBillIndexEnabled = false;
        public bool HaulMergeIndexEnabled = false;
        public bool WorkSearchTailPlansEnabled = false;
        public bool RoofPrunerEnabled = false;
        public bool HarvestPrunerEnabled = false;
        public bool SnowPrunerEnabled = false;
#else
        // Preserve the pre-settings behavior when no configuration has been saved.
        public bool ReachabilityMemoEnabled = true;
        public bool DoBillIndexEnabled = true;
        public bool HaulMergeIndexEnabled = true;
        public bool WorkSearchTailPlansEnabled = true;
        public bool RoofPrunerEnabled = true;
        public bool HarvestPrunerEnabled = true;
        public bool SnowPrunerEnabled = true;
#endif

        public override void ExposeData()
        {
#if RIMWORLD17
            Scribe_Values.Look(ref PulseDiagnosticsEnabled, "rw17_pulseDiagnostics", false);
            Scribe_Values.Look(ref ReachabilityMemoEnabled, "rw17_reachabilityMemo", false);
            Scribe_Values.Look(ref DoBillIndexEnabled, "rw17_doBillIndex", false);
            Scribe_Values.Look(ref HaulMergeIndexEnabled, "rw17_haulMergeIndex", false);
            Scribe_Values.Look(ref WorkSearchTailPlansEnabled, "rw17_workSearchTailPlans", false);
            Scribe_Values.Look(ref RoofPrunerEnabled, "rw17_roofPruner", false);
            Scribe_Values.Look(ref HarvestPrunerEnabled, "rw17_harvestPruner", false);
            Scribe_Values.Look(ref SnowPrunerEnabled, "rw17_snowPruner", false);
#else
            Scribe_Values.Look(ref ReachabilityMemoEnabled, "reachabilityMemo", true);
            Scribe_Values.Look(ref DoBillIndexEnabled, "doBillIndex", true);
            Scribe_Values.Look(ref HaulMergeIndexEnabled, "haulMergeIndex", true);
            Scribe_Values.Look(ref WorkSearchTailPlansEnabled, "workSearchTailPlans", true);
            Scribe_Values.Look(ref RoofPrunerEnabled, "roofPruner", true);
            Scribe_Values.Look(ref HarvestPrunerEnabled, "harvestPruner", true);
            Scribe_Values.Look(ref SnowPrunerEnabled, "snowPruner", true);
#endif
        }
    }

    // Freeze the startup configuration. Editing a checkbox cannot partly switch an
    // installed patch chain in a running game; changes take effect after restart.
    internal sealed class ModulePlan
    {
        internal readonly bool Reach, Bill, Merge, Tail, Roof, Harvest, Snow;
#if RIMWORLD17
        internal readonly bool Diagnostics;
#endif
        internal ModulePlan(RimOptSettings settings)
        {
#if RIMWORLD17
            Diagnostics = settings.PulseDiagnosticsEnabled;
#endif
            Reach = settings.ReachabilityMemoEnabled; Bill = settings.DoBillIndexEnabled;
            Merge = settings.HaulMergeIndexEnabled; Tail = settings.WorkSearchTailPlansEnabled;
            Roof = settings.RoofPrunerEnabled; Harvest = settings.HarvestPrunerEnabled; Snow = settings.SnowPrunerEnabled;
        }
        internal bool NeedsScope
        {
            get
            {
#if RIMWORLD17
                return Reach || Bill || Merge || Tail || Diagnostics;
#else
                return Reach || Bill || Merge || Tail;
#endif
            }
        }
        internal void Apply(Action scope, Action reach, Action bill, Action merge, Action tail, Action<bool, bool, bool> pruners)
        {
            if (NeedsScope) scope();
            if (Reach) reach();
            if (Bill) bill();
            if (Merge) merge();
            if (Tail) tail();
            if (Roof || Harvest || Snow) pruners(Roof, Harvest, Snow);
        }
        public override string ToString()
        {
#if RIMWORLD17
            string prefix = "pulse=" + Diagnostics + ", reach=";
#else
            string prefix = "reach=";
#endif
            return prefix + Reach + ", bill=" + Bill + ", merge=" + Merge + ", tail=" + Tail +
                ", roof=" + Roof + ", harvest=" + Harvest + ", snow=" + Snow + ", packageScope=" + NeedsScope;
        }
    }
}
