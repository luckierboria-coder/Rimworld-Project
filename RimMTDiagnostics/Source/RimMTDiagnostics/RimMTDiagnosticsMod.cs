using System;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMT.Diagnostics
{
    public sealed class RimMTDiagnosticsSettings : ModSettings
    {
        public static bool EnableWaitTrace = true;
        public static bool EnableSearchTiming = false;
        public static bool EnablePatherBreakdown = true;
        public static bool EnableWorkGiverBurst = true;
        public static bool EnableReachCaptureTrace = true;
        public static bool EnablePauseGapTrace = true;
        public static int SampleEveryTicks = 64;
        public static int TailThresholdMs = 20;
        public static int PostSpikeBurstTicks = 4;
        public static int WorkGiverTriggerMs = 20;
        public static int WorkGiverBurstPackages = 16;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EnableWaitTrace, "enableWaitTrace", true);
            Scribe_Values.Look(ref EnableSearchTiming, "enableSearchTiming", false);
            Scribe_Values.Look(ref EnablePatherBreakdown, "enablePatherBreakdown", true);
            Scribe_Values.Look(ref EnableWorkGiverBurst, "enableWorkGiverBurst", true);
            Scribe_Values.Look(ref EnableReachCaptureTrace, "enableReachCaptureTrace", true);
            Scribe_Values.Look(ref EnablePauseGapTrace, "enablePauseGapTrace", true);
            Scribe_Values.Look(ref SampleEveryTicks, "sampleEveryTicks", 64);
            Scribe_Values.Look(ref TailThresholdMs, "tailThresholdMs", 20);
            Scribe_Values.Look(ref PostSpikeBurstTicks, "postSpikeBurstTicks", 4);
            Scribe_Values.Look(ref WorkGiverTriggerMs, "workGiverTriggerMs", 20);
            Scribe_Values.Look(ref WorkGiverBurstPackages, "workGiverBurstPackages", 16);
            if (SampleEveryTicks < 1) SampleEveryTicks = 1;
            if (TailThresholdMs < 1) TailThresholdMs = 1;
            if (PostSpikeBurstTicks < 0) PostSpikeBurstTicks = 0;
            if (WorkGiverTriggerMs < 5) WorkGiverTriggerMs = 5;
            if (WorkGiverBurstPackages < 4) WorkGiverBurstPackages = 4;
            if (WorkGiverBurstPackages > 64) WorkGiverBurstPackages = 64;
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
            Rect view = new Rect(0f, 0f, inRect.width - 20f, 820f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            Listing_Standard list = new Listing_Standard();
            list.Begin(view);
            list.Label("Standalone diagnostics companion. Disable the entire mod for normal gameplay when profiling is not needed.");
            list.GapLine();
            list.CheckboxLabeled("Track Wait/idle outcomes and sampled current-job dwell", ref RimMTDiagnosticsSettings.EnableWaitTrace);
            list.CheckboxLabeled("Break down Pawn_PathFollower internals during deep samples", ref RimMTDiagnosticsSettings.EnablePatherBreakdown);
            list.CheckboxLabeled("Auto-capture a short WorkGiver burst after sampled slow DetermineNextJob", ref RimMTDiagnosticsSettings.EnableWorkGiverBurst);
            list.CheckboxLabeled("Trace RimMT ReachProfile capture and Region.Allows tails", ref RimMTDiagnosticsSettings.EnableReachCaptureTrace);
            list.CheckboxLabeled("Tag long inter-tick/pause-resume gaps", ref RimMTDiagnosticsSettings.EnablePauseGapTrace);
            list.CheckboxLabeled("Time all GenClosest and Reachability during deep sample windows (heavier)", ref RimMTDiagnosticsSettings.EnableSearchTiming);
            list.GapLine();
            list.Label("Deep sample cadence: every " + RimMTDiagnosticsSettings.SampleEveryTicks + " game ticks");
            RimMTDiagnosticsSettings.SampleEveryTicks = (int)list.Slider(RimMTDiagnosticsSettings.SampleEveryTicks, 1f, 256f);
            list.Label("Tick tail threshold: " + RimMTDiagnosticsSettings.TailThresholdMs + " ms");
            RimMTDiagnosticsSettings.TailThresholdMs = (int)list.Slider(RimMTDiagnosticsSettings.TailThresholdMs, 5f, 100f);
            list.Label("Post-spike deep burst: " + RimMTDiagnosticsSettings.PostSpikeBurstTicks + " ticks");
            RimMTDiagnosticsSettings.PostSpikeBurstTicks = (int)list.Slider(RimMTDiagnosticsSettings.PostSpikeBurstTicks, 0f, 16f);
            list.Label("WorkGiver burst trigger: " + RimMTDiagnosticsSettings.WorkGiverTriggerMs + " ms DetermineNextJob");
            RimMTDiagnosticsSettings.WorkGiverTriggerMs = (int)list.Slider(RimMTDiagnosticsSettings.WorkGiverTriggerMs, 5f, 100f);
            list.Label("WorkGiver burst length: " + RimMTDiagnosticsSettings.WorkGiverBurstPackages + " JobGiver_Work packages");
            RimMTDiagnosticsSettings.WorkGiverBurstPackages = (int)list.Slider(RimMTDiagnosticsSettings.WorkGiverBurstPackages, 4f, 64f);
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
                DiagnosticsV02.Reset();
                Messages.Message("RimMT Diagnostics counters reset.", MessageTypeDefOf.NeutralEvent, false);
            }
            list.GapLine();
            list.Label("Search Timing is deliberately OFF by default because it adds Harmony dispatch to extremely hot GenClosest/Reachability methods. The v0.2 targeted profilers remain bounded/sampled.");
            list.End();
            Widgets.EndScrollView();
        }
    }
}
