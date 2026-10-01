using UnityEngine;
using Verse;

namespace RimMT
{
    public sealed class RimMTMod : Mod
    {
        public static RimMTSettings Settings;

        public RimMTMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimMTSettings>();
            RimMTRuntime.ApplySettings(Settings);
        }

        public override string SettingsCategory() { return "RimMT"; }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("RimMT V0.9.3-T34D.2.3 Lean Live V2.2");
            listing.Label("Only runtime-verified synchronous modules are present in this DLL.");
            listing.GapLine();

            listing.CheckboxLabeled("RimMT_TextCache".Translate(), ref Settings.TextCache, "RimMT_TextCacheDesc".Translate());
            listing.GapLine();
            if (listing.ButtonText("RimMT_OpenMonitor".Translate()))
                Find.WindowStack.Add(new RimMTMonitorWindow());
            if (listing.ButtonText("RimMT_LogReport".Translate()))
                RimMTDiagnostics.LogRuntimeReport();
            listing.End();
            RimMTRuntime.ApplySettings(Settings);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            RimMTRuntime.ApplySettings(Settings);
        }
    }
}
