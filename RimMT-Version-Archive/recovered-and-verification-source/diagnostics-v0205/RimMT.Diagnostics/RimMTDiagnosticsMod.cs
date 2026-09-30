using System;
using System.IO;
using UnityEngine;
using Verse;

namespace RimMT.Diagnostics;

public sealed class RimMTDiagnosticsMod : Mod
{
	private Vector2 scroll;

	public RimMTDiagnosticsMod(ModContentPack content)
		: base(content)
	{
		((Mod)this).GetSettings<RimMTDiagnosticsSettings>();
	}

	public override string SettingsCategory()
	{
		return "RimMT Diagnostics";
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Expected O, but got Unknown
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_026d: Unknown result type (might be due to invalid IL or missing references)
		Rect val = default(Rect);
		((Rect)(ref val))._002Ector(0f, 0f, ((Rect)(ref inRect)).width - 20f, 720f);
		Widgets.BeginScrollView(inRect, ref scroll, val, true);
		Listing_Standard val2 = new Listing_Standard();
		((Listing)val2).Begin(val);
		val2.Label("RimMT Diagnostics v0.20.5 passive mode. Runtime tick, pawn, path and WorkGiver probes are disabled.", -1f, (string)null);
		val2.Label("Reports still read RimMT production counters and Harmony ownership without adding hot-path instrumentation.", -1f, (string)null);
		((Listing)val2).GapLine(12f);
		val2.CheckboxLabeled("Track Wait/idle outcomes and live current-Wait census", ref RimMTDiagnosticsSettings.EnableWaitTrace, (string)null, 0f, 1f);
		val2.CheckboxLabeled("Time GenClosest and Reachability during deep sample windows", ref RimMTDiagnosticsSettings.EnableSearchTiming, (string)null, 0f, 1f);
		val2.Label("Deep sample cadence: every " + RimMTDiagnosticsSettings.SampleEveryTicks + " game ticks", -1f, (string)null);
		RimMTDiagnosticsSettings.SampleEveryTicks = (int)val2.Slider((float)RimMTDiagnosticsSettings.SampleEveryTicks, 1f, 256f);
		val2.Label("Tail threshold: " + RimMTDiagnosticsSettings.TailThresholdMs + " ms", -1f, (string)null);
		RimMTDiagnosticsSettings.TailThresholdMs = (int)val2.Slider((float)RimMTDiagnosticsSettings.TailThresholdMs, 5f, 100f);
		val2.Label("Post-spike deep burst: " + RimMTDiagnosticsSettings.PostSpikeBurstTicks + " ticks", -1f, (string)null);
		RimMTDiagnosticsSettings.PostSpikeBurstTicks = (int)val2.Slider((float)RimMTDiagnosticsSettings.PostSpikeBurstTicks, 0f, 16f);
		((Listing)val2).GapLine(12f);
		if (val2.ButtonText("Log full diagnostic report", (string)null, 1f))
		{
			Log.Message(DiagnosticReport.Build());
			Log.Message("[RimMT Diagnostics] report written to log (silent notification).");
		}
		if (val2.ButtonText("Write report to Config folder", (string)null, 1f))
		{
			try
			{
				string contents = DiagnosticReport.Build();
				string path = "RimMT-Diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
				string text = Path.Combine(GenFilePaths.ConfigFolderPath, path);
				File.WriteAllText(text, contents);
				Log.Message("[RimMT Diagnostics] report file written (silent notification): " + text);
			}
			catch (Exception ex)
			{
				Log.Warning("[RimMT Diagnostics] report write failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
		if (val2.ButtonText("Reset diagnostic counters", (string)null, 1f))
		{
			DiagnosticsHub.Reset();
			DiagnosticsV02.Reset();
			DiagnosticsV03.Reset();
			Log.Message("[RimMT Diagnostics] counters reset (silent notification).");
		}
		((Listing)val2).GapLine(12f);
		val2.Label("Legacy probe settings are retained for config compatibility but are ignored by this passive build.", -1f, (string)null);
		((Listing)val2).End();
		Widgets.EndScrollView();
	}
}
