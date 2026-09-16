using System;
using System.IO;
using UnityEngine;
using Verse;

namespace RimMT.Diagnostics
{
    public sealed class RimMTDiagnosticsSettings : ModSettings
    {
        public static bool EnableWaitTrace = true;
        public static bool EnableSearchTiming = false;
        public static int SampleEveryTicks = 64;
        public static int TailThresholdMs = 20;
        public static int PostSpikeBurstTicks = 4;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EnableWaitTrace, "enableWaitTrace", true);
            Scribe_Values.Look(ref EnableSearchTiming, "enableSearchTiming", false);
            Scribe_Values.Look(ref SampleEveryTicks, "sampleEveryTicks", 64);
            Scribe_Values.Look(ref TailThresholdMs, "tailThresholdMs", 20);
            Scribe_Values.Look(ref PostSpikeBurstTicks, "postSpikeBurstTicks", 4);
            if (SampleEveryTicks < 1) SampleEveryTicks = 1;
            if (TailThresholdMs < 1) TailThresholdMs = 1;
            if (PostSpikeBurstTicks < 0) PostSpikeBurstTicks = 0;
        }
    }

    public sealed class RimMTDiagnosticsMod : Mod
    {
        private Vector2 scroll;

        public RimMTDiagnosticsMod(ModContentPack content) : base(content)
        {
            GetSettings<RimMTDiagnosticsSettings>();
        }

        public override string SettingsCategory() { return "RimMT Diagnostics"; }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 20f, 620f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            Listing_Standard list = new Listing_Standard();
            list.Begin(view);
            list.Label("Standalone diagnostics companion. Disable the entire mod for normal gameplay when profiling is not needed.");
            list.GapLine();
            list.CheckboxLabeled("Track Wait/idle outcomes on every DetermineNextJob", ref RimMTDiagnosticsSettings.EnableWaitTrace);
            list.CheckboxLabeled("Time GenClosest and Reachability during deep sample windows", ref RimMTDiagnosticsSettings.EnableSearchTiming);
            list.Label("Deep sample cadence: every " + RimMTDiagnosticsSettings.SampleEveryTicks + " game ticks");
            RimMTDiagnosticsSettings.SampleEveryTicks = (int)list.Slider(RimMTDiagnosticsSettings.SampleEveryTicks, 1f, 256f);
            list.Label("Tail threshold: " + RimMTDiagnosticsSettings.TailThresholdMs + " ms");
            RimMTDiagnosticsSettings.TailThresholdMs = (int)list.Slider(RimMTDiagnosticsSettings.TailThresholdMs, 5f, 100f);
            list.Label("Post-spike deep burst: " + RimMTDiagnosticsSettings.PostSpikeBurstTicks + " ticks");
            RimMTDiagnosticsSettings.PostSpikeBurstTicks = (int)list.Slider(RimMTDiagnosticsSettings.PostSpikeBurstTicks, 0f, 16f);
            list.GapLine();

            if (list.ButtonText("Log full diagnostic report"))
            {
                string report = DiagnosticReport.Build();
                Log.Message(report);
                Messages.Message("RimMT Diagnostics report written to log.", MessageTypeDefOf.NeutralEvent, false);
            }
            if (list.ButtonText("Write report to Config folder"))
            {
                try
                {
                    string report = DiagnosticReport.Build();
                    string name = "RimMT-Diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                    string path = Path.Combine(GenFilePaths.ConfigFolderPath, name);
                    File.WriteAllText(path, report);
                    Messages.Message("RimMT Diagnostics report: " + path, MessageTypeDefOf.NeutralEvent, false);
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimMT Diagnostics] report write failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (list.ButtonText("Reset diagnostic counters"))
            {
                DiagnosticsHub.Reset();
                Messages.Message("RimMT Diagnostics counters reset.", MessageTypeDefOf.NeutralEvent, false);
            }
            list.GapLine();
            list.Label("Search timing adds Harmony dispatch to hot GenClosest/Reachability methods. Keep it OFF unless diagnosing search tails.");
            list.End();
            Widgets.EndScrollView();
            WriteSettings();
        }
    }
}
