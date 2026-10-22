using UnityEngine;
using Verse;

namespace RimMT2
{
    public sealed class RimMT2Settings : ModSettings
    {
        public bool enableWorkerFabric = true;
        public bool enableSpatialProof;
        public bool enableJobSearchCensus;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableWorkerFabric, "enableWorkerFabric", true);
            Scribe_Values.Look(ref enableSpatialProof, "enableSpatialProof", false);
            Scribe_Values.Look(ref enableJobSearchCensus, "enableJobSearchCensus", false);
        }
    }

    public sealed class RimMT2Mod : Mod
    {
        internal static RimMT2Settings Settings;

        public RimMT2Mod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimMT2Settings>();
            RuntimeController.RequestCensusConfiguration();
        }

        public override string SettingsCategory()
        {
            return "RimMT 2.0";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("Enable snapshot worker fabric", ref Settings.enableWorkerFabric,
                "Allows enabled snapshot modules to start a bounded worker pool. No module means no worker threads.");
            listing.CheckboxLabeled("Enable spatial snapshot proof module", ref Settings.enableSpatialProof,
                "Builds a per-map immutable cell index on workers. Disabled by default until a WorkGiver consumer is added.");
            listing.CheckboxLabeled("Sample job search cost (1 in 8)", ref Settings.enableJobSearchCensus,
                "Measures complete work searches, global/region candidate searches and reachability. Writes a summary every 64 samples; turn off after capture.");
            listing.Label("Unity job workers: " + Verse.UnityData.MaxJobWorkerCount);
            if (listing.ButtonText("Write RimMT 2.0 status to log"))
            {
                Diagnostics.StatusReporter.LogNow("settings");
                Diagnostics.JobSearchCensus.LogNow("settings");
            }
            listing.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            RuntimeController.ApplySettings();
            RuntimeController.RequestCensusConfiguration();
        }
    }
}
