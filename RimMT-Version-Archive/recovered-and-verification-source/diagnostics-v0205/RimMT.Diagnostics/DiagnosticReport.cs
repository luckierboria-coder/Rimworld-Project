using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimMT.Diagnostics;

internal static class DiagnosticReport
{
	internal static string Build()
	{
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		StringBuilder stringBuilder = new StringBuilder(65536);
		stringBuilder.AppendLine("============================================================");
		stringBuilder.AppendLine("RimMT Diagnostics v0.20.5 report");
		stringBuilder.AppendLine("ProgramState=" + ((object)Current.ProgramState/*cast due to .constrained prefix*/).ToString() + ", RimWorld=" + VersionControl.CurrentVersionStringWithRev);
		try
		{
			stringBuilder.AppendLine("LoadedMods=" + LoadedModManager.RunningModsListForReading.Count());
		}
		catch
		{
		}
		try
		{
			stringBuilder.AppendLine("GameTick=" + ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame));
		}
		catch
		{
		}
		stringBuilder.AppendLine("Settings: sampleEveryTicks=" + RimMTDiagnosticsSettings.SampleEveryTicks + ", tailThresholdMs=" + RimMTDiagnosticsSettings.TailThresholdMs + ", postSpikeBurstTicks=" + RimMTDiagnosticsSettings.PostSpikeBurstTicks + ", waitTrace=" + RimMTDiagnosticsSettings.EnableWaitTrace + ", searchTiming=" + RimMTDiagnosticsSettings.EnableSearchTiming);
		stringBuilder.AppendLine("------------------------------------------------------------");
		stringBuilder.Append(DiagnosticsHub.BuildSummary());
		stringBuilder.Append(DiagnosticsV02.BuildSummary());
		stringBuilder.Append(DiagnosticsV03.BuildSummary());
		stringBuilder.Append(MapPostTickComponentCensus.Summary());
		stringBuilder.AppendLine("------------------------------------------------------------");
		stringBuilder.AppendLine("[RimMT production summaries via reflection]");
		stringBuilder.Append(RimMTBridge.BuildSummary());
		stringBuilder.AppendLine("------------------------------------------------------------");
		stringBuilder.AppendLine("[Harmony audit]");
		stringBuilder.Append(HarmonyAudit.BuildSummary());
		stringBuilder.AppendLine("============================================================");
		return stringBuilder.ToString();
	}
}
