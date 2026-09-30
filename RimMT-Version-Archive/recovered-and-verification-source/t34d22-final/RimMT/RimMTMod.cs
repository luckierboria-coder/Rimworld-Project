using UnityEngine;
using Verse;

namespace RimMT;

public sealed class RimMTMod : Mod
{
	public static RimMTSettings Settings;

	public RimMTMod(ModContentPack content)
		: base(content)
	{
		Settings = ((Mod)this).GetSettings<RimMTSettings>();
		RimMTRuntime.ApplySettings(Settings);
	}

	public override string SettingsCategory()
	{
		return "RimMT";
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_011a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		Listing_Standard val = new Listing_Standard();
		((Listing)val).Begin(inRect);
		val.Label("RimMT V0.9.3 Consolidated Stable — single DLL production build", -1f, (string)null);
		val.Label("Production counters are lightweight aggregates. The realtime monitor is optional, closed by default, and refreshes its text every 30 rendered frames.", -1f, (string)null);
		((Listing)val).GapLine(12f);
		val.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("RimMT_AdaptiveBurst")), ref Settings.AdaptiveBurst, TaggedString.op_Implicit(Translator.Translate("RimMT_AdaptiveBurstDesc")), 0f, 1f);
		val.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("RimMT_TextCache")), ref Settings.TextCache, TaggedString.op_Implicit(Translator.Translate("RimMT_TextCacheDesc")), 0f, 1f);
		val.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("RimMT_WorkScanAcceleration")), ref Settings.WorkScanAcceleration, TaggedString.op_Implicit(Translator.Translate("RimMT_WorkScanAccelerationDesc")), 0f, 1f);
		((Listing)val).GapLine(12f);
		if (val.ButtonText(TaggedString.op_Implicit(Translator.Translate("RimMT_OpenMonitor")), (string)null, 1f))
		{
			Find.WindowStack.Add((Window)(object)new RimMTMonitorWindow());
		}
		if (val.ButtonText(TaggedString.op_Implicit(Translator.Translate("RimMT_LogReport")), (string)null, 1f))
		{
			RimMTDiagnostics.LogRuntimeReport();
		}
		if (val.ButtonText(TaggedString.op_Implicit(Translator.Translate("RimMT_RunSelfTest")), (string)null, 1f))
		{
			RimMTDiagnostics.RunWorkerSelfTest();
		}
		((Listing)val).End();
		RimMTRuntime.ApplySettings(Settings);
	}

	public override void WriteSettings()
	{
		((Mod)this).WriteSettings();
		RimMTRuntime.ApplySettings(Settings);
	}
}
