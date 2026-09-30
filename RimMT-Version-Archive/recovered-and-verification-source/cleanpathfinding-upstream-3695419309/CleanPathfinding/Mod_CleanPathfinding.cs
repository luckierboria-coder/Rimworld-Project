using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CleanPathfinding;

public class Mod_CleanPathfinding : Mod
{
	public static Dictionary<string, bool> patchLedger = new Dictionary<string, bool>();

	public Mod_CleanPathfinding(ModContentPack content)
		: base(content)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		((Mod)this).GetSettings<ModSettings_CleanPathfinding>();
		new Harmony(((Mod)this).Content.PackageIdPlayerFacing).PatchAll();
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Expected O, but got Unknown
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Expected O, but got Unknown
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Expected O, but got Unknown
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0118: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Expected O, but got Unknown
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0174: Unknown result type (might be due to invalid IL or missing references)
		GUI.BeginGroup(inRect);
		List<TabRecord> list = new List<TabRecord>();
		list.Add(new TabRecord(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.Header.Tuning")), (Action)delegate
		{
			ModSettings_CleanPathfinding.selectedTab = ModSettings_CleanPathfinding.Tab.tuning;
		}, ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.tuning));
		list.Add(new TabRecord(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.Header.Doorpathing")), (Action)delegate
		{
			ModSettings_CleanPathfinding.selectedTab = ModSettings_CleanPathfinding.Tab.doorPathing;
		}, ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.doorPathing));
		list.Add(new TabRecord(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.Header.Rules")), (Action)delegate
		{
			ModSettings_CleanPathfinding.selectedTab = ModSettings_CleanPathfinding.Tab.rules;
		}, ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.rules));
		list.Add(new TabRecord(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.Header.Misc")), (Action)delegate
		{
			ModSettings_CleanPathfinding.selectedTab = ModSettings_CleanPathfinding.Tab.misc;
		}, ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.misc));
		Rect val = default(Rect);
		((Rect)(ref val))._002Ector(0f, 32f, ((Rect)(ref inRect)).width, ((Rect)(ref inRect)).height - 32f);
		Widgets.DrawMenuSection(val);
		TabDrawer.DrawTabs<TabRecord>(new Rect(0f, 32f, ((Rect)(ref inRect)).width, Text.LineHeight), list, 200f);
		if (ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.tuning)
		{
			DrawTuning();
		}
		else if (ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.doorPathing)
		{
			DrawDoorpathing();
		}
		else if (ModSettings_CleanPathfinding.selectedTab == ModSettings_CleanPathfinding.Tab.rules)
		{
			DrawRules();
		}
		else
		{
			DrawMisc();
		}
		GUI.EndGroup();
		void DrawDoorpathing()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0024: Invalid comparison between Unknown and I4
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0077: Unknown result type (might be due to invalid IL or missing references)
			//IL_0034: Unknown result type (might be due to invalid IL or missing references)
			//IL_0048: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
			//IL_0103: Unknown result type (might be due to invalid IL or missing references)
			//IL_010d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0112: Unknown result type (might be due to invalid IL or missing references)
			//IL_0135: Unknown result type (might be due to invalid IL or missing references)
			//IL_013f: Unknown result type (might be due to invalid IL or missing references)
			//IL_016c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0176: Unknown result type (might be due to invalid IL or missing references)
			//IL_0180: Unknown result type (might be due to invalid IL or missing references)
			//IL_0185: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
			Listing_Standard val2 = new Listing_Standard();
			((Listing)val2).Begin(GenUI.ContractedBy(inRect, 15f));
			if ((int)Current.ProgramState != 2)
			{
				val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.DoorPathing")), ref ModSettings_CleanPathfinding.doorPathing, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.DoorPathing.Desc")), 0f, 1f);
			}
			else
			{
				val2.Label(Translator.Translate("CleanPathfinding.Settings.DoorPathing.Notice"), -1f, (string)null);
			}
			((Listing)val2).GapLine(12f);
			((Listing)val2).End();
			((Listing)val2).Begin(new Rect(((Rect)(ref inRect)).x + 15f, ((Rect)(ref inRect)).y + 55f, ((Rect)(ref inRect)).width - 30f, ((Rect)(ref inRect)).height - 30f));
			if (ModSettings_CleanPathfinding.doorPathing)
			{
				val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.DoorPathingSide", NamedArgument.op_Implicit("250"), NamedArgument.op_Implicit("50"), NamedArgument.op_Implicit("500"))) + ModSettings_CleanPathfinding.doorPathingSide, -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.DoorPathingSide.Desc")));
				ModSettings_CleanPathfinding.doorPathingSide = (int)val2.Slider((float)ModSettings_CleanPathfinding.doorPathingSide, 50f, 500f);
				val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.DoorPathingEmergency", NamedArgument.op_Implicit("500"), NamedArgument.op_Implicit("500"), NamedArgument.op_Implicit("1000"))) + ModSettings_CleanPathfinding.doorPathingEmergency, -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.DoorPathingEmergency.Desc")));
				ModSettings_CleanPathfinding.doorPathingEmergency = (int)val2.Slider((float)ModSettings_CleanPathfinding.doorPathingEmergency, 500f, 1000f);
			}
			((Listing)val2).End();
		}
		void DrawMisc()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			//IL_004c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0076: Unknown result type (might be due to invalid IL or missing references)
			//IL_007b: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
			//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
			//IL_0126: Unknown result type (might be due to invalid IL or missing references)
			//IL_0130: Unknown result type (might be due to invalid IL or missing references)
			//IL_013a: Unknown result type (might be due to invalid IL or missing references)
			//IL_015e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0163: Unknown result type (might be due to invalid IL or missing references)
			//IL_014b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0168: Unknown result type (might be due to invalid IL or missing references)
			//IL_016d: Unknown result type (might be due to invalid IL or missing references)
			//IL_017c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0186: Unknown result type (might be due to invalid IL or missing references)
			//IL_01d0: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
			Listing_Standard val2 = new Listing_Standard();
			((Listing)val2).Begin(new Rect(((Rect)(ref inRect)).x + 15f, ((Rect)(ref inRect)).y + 55f, ((Rect)(ref inRect)).width - 30f, ((Rect)(ref inRect)).height - 30f));
			val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.ExitRange", NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("200"))) + (((float)ModSettings_CleanPathfinding.exitRange == 0f) ? ((object)Translator.Translate("Off")) : ((object)ModSettings_CleanPathfinding.exitRange)), -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.ExitRange.Desc")));
			ModSettings_CleanPathfinding.exitRange = (int)val2.Slider((float)ModSettings_CleanPathfinding.exitRange, 0f, 200f);
			ModSettings_CleanPathfinding.exitTuning = (float)ModSettings_CleanPathfinding.exitRange > 0f;
			float num = (float)Math.Round((float)ModSettings_CleanPathfinding.wanderDelay / 60f, 1);
			val2.Label(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.WanderDelay", NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("-2"), NamedArgument.op_Implicit("10"), NamedArgument.op_Implicit(ModSettings_CleanPathfinding.wanderTuning ? (num.ToString() + Translator.Translate("Seconds")) : Translator.Translate("Off"))), -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.WanderDelay.Desc")));
			ModSettings_CleanPathfinding.wanderDelay = (int)val2.Slider((float)ModSettings_CleanPathfinding.wanderDelay, -118f, 600f);
			ModSettings_CleanPathfinding.wanderTuning = (float)ModSettings_CleanPathfinding.wanderDelay < -20f || (float)ModSettings_CleanPathfinding.wanderDelay > 20f;
			val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.OptimizeCollider")), ref ModSettings_CleanPathfinding.optimizeCollider, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.OptimizeCollider.Desc")), 0f, 1f);
			if (Prefs.DevMode)
			{
				val2.CheckboxLabeled("DevMode: Enable logging", ref ModSettings_CleanPathfinding.logging, (string)null, 0f, 1f);
			}
			((Listing)val2).End();
		}
		void DrawRules()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			//IL_004c: Unknown result type (might be due to invalid IL or missing references)
			//IL_005d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0071: Unknown result type (might be due to invalid IL or missing references)
			//IL_0091: Unknown result type (might be due to invalid IL or missing references)
			//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00de: Unknown result type (might be due to invalid IL or missing references)
			//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
			//IL_010b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0130: Unknown result type (might be due to invalid IL or missing references)
			//IL_013a: Unknown result type (might be due to invalid IL or missing references)
			Listing_Standard val2 = new Listing_Standard();
			((Listing)val2).Begin(new Rect(((Rect)(ref inRect)).x + 15f, ((Rect)(ref inRect)).y + 55f, ((Rect)(ref inRect)).width - 30f, ((Rect)(ref inRect)).height - 30f));
			val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.FactorCarryingPawn")), ref ModSettings_CleanPathfinding.factorCarryingPawn, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.FactorCarryingPawn.Desc")), 0f, 1f);
			val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.FactorBleeding")), ref ModSettings_CleanPathfinding.factorBleeding, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.FactorBleeding.Desc")), 0f, 1f);
			val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.DarknessPenalty", NamedArgument.op_Implicit("2"), NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("6"))) + (((float)ModSettings_CleanPathfinding.darknessPenalty == 0f) ? ((object)Translator.Translate("Off")) : ((object)ModSettings_CleanPathfinding.darknessPenalty)), -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.DarknessPenalty.Desc")));
			ModSettings_CleanPathfinding.darknessPenalty = (int)val2.Slider((float)ModSettings_CleanPathfinding.darknessPenalty, 0f, 6f);
			ModSettings_CleanPathfinding.factorLight = (float)ModSettings_CleanPathfinding.darknessPenalty != 0f;
			((Listing)val2).End();
		}
		void DrawTuning()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Expected O, but got Unknown
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
			//IL_010e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0118: Unknown result type (might be due to invalid IL or missing references)
			//IL_0122: Unknown result type (might be due to invalid IL or missing references)
			//IL_0127: Unknown result type (might be due to invalid IL or missing references)
			//IL_014a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0154: Unknown result type (might be due to invalid IL or missing references)
			//IL_0181: Unknown result type (might be due to invalid IL or missing references)
			//IL_018b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0195: Unknown result type (might be due to invalid IL or missing references)
			//IL_019a: Unknown result type (might be due to invalid IL or missing references)
			//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
			//IL_01f4: Unknown result type (might be due to invalid IL or missing references)
			//IL_01fe: Unknown result type (might be due to invalid IL or missing references)
			//IL_0208: Unknown result type (might be due to invalid IL or missing references)
			//IL_020d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0230: Unknown result type (might be due to invalid IL or missing references)
			//IL_023a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0267: Unknown result type (might be due to invalid IL or missing references)
			//IL_0271: Unknown result type (might be due to invalid IL or missing references)
			//IL_027b: Unknown result type (might be due to invalid IL or missing references)
			//IL_029d: Unknown result type (might be due to invalid IL or missing references)
			//IL_02a2: Unknown result type (might be due to invalid IL or missing references)
			//IL_0291: Unknown result type (might be due to invalid IL or missing references)
			//IL_047c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0490: Unknown result type (might be due to invalid IL or missing references)
			//IL_040c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0411: Unknown result type (might be due to invalid IL or missing references)
			//IL_0413: Unknown result type (might be due to invalid IL or missing references)
			//IL_0424: Unknown result type (might be due to invalid IL or missing references)
			//IL_0438: Unknown result type (might be due to invalid IL or missing references)
			//IL_0442: Unknown result type (might be due to invalid IL or missing references)
			//IL_044c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0451: Unknown result type (might be due to invalid IL or missing references)
			//IL_046b: Unknown result type (might be due to invalid IL or missing references)
			//IL_02a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_02b6: Unknown result type (might be due to invalid IL or missing references)
			//IL_02c0: Unknown result type (might be due to invalid IL or missing references)
			//IL_02e8: Unknown result type (might be due to invalid IL or missing references)
			//IL_02fc: Unknown result type (might be due to invalid IL or missing references)
			//IL_0306: Unknown result type (might be due to invalid IL or missing references)
			//IL_030b: Unknown result type (might be due to invalid IL or missing references)
			//IL_035b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0365: Unknown result type (might be due to invalid IL or missing references)
			//IL_036f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0374: Unknown result type (might be due to invalid IL or missing references)
			//IL_0397: Unknown result type (might be due to invalid IL or missing references)
			//IL_039c: Unknown result type (might be due to invalid IL or missing references)
			//IL_03a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_03aa: Unknown result type (might be due to invalid IL or missing references)
			Listing_Standard val2 = new Listing_Standard();
			((Listing)val2).Begin(GenUI.ContractedBy(inRect, 15f));
			bool enableTuning = ModSettings_CleanPathfinding.enableTuning;
			val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.EnableTuning")), ref ModSettings_CleanPathfinding.enableTuning, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.EnableTuning.Desc")), 0f, 1f);
			if (enableTuning != ModSettings_CleanPathfinding.enableTuning)
			{
				ModSettings_CleanPathfinding.bias = 5;
				ModSettings_CleanPathfinding.naturalBias = 0;
				ModSettings_CleanPathfinding.roadBias = 9;
				ModSettings_CleanPathfinding.heuristicAdjuster = 90;
				ModSettings_CleanPathfinding.regionPathing = true;
				ModSettings_CleanPathfinding.regionModeThreshold = 1000;
			}
			((Listing)val2).GapLine(12f);
			((Listing)val2).End();
			((Listing)val2).Begin(new Rect(((Rect)(ref inRect)).x + 15f, ((Rect)(ref inRect)).y + 55f, ((Rect)(ref inRect)).width - 30f, ((Rect)(ref inRect)).height - 30f));
			if (ModSettings_CleanPathfinding.enableTuning)
			{
				val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.Bias", NamedArgument.op_Implicit("5"), NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("12"))) + ModSettings_CleanPathfinding.bias, -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.Bias.Desc")));
				ModSettings_CleanPathfinding.bias = (int)val2.Slider((float)ModSettings_CleanPathfinding.bias, 0f, 12f);
				val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.NaturalBias", NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("12"))) + ModSettings_CleanPathfinding.naturalBias, -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.NaturalBias.Desc")));
				ModSettings_CleanPathfinding.naturalBias = (int)val2.Slider((float)ModSettings_CleanPathfinding.naturalBias, 0f, 12f);
				val2.Label(TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.RoadBias", NamedArgument.op_Implicit("9"), NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("12"))) + ModSettings_CleanPathfinding.roadBias, -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.RoadBias.Desc")));
				ModSettings_CleanPathfinding.roadBias = (int)val2.Slider((float)ModSettings_CleanPathfinding.roadBias, 0f, 12f);
				val2.Label(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.HeuristicAdjuster", NamedArgument.op_Implicit("90"), NamedArgument.op_Implicit("0"), NamedArgument.op_Implicit("200"), (ModSettings_CleanPathfinding.heuristicAdjuster == 200) ? NamedArgument.op_Implicit(Translator.Translate("Max")) : NamedArgument.op_Implicit(ModSettings_CleanPathfinding.heuristicAdjuster)), -1f, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.HeuristicAdjuster.Desc")));
				ModSettings_CleanPathfinding.heuristicAdjuster = (int)val2.Slider((float)ModSettings_CleanPathfinding.heuristicAdjuster, 0f, 200f);
				val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.EnableRegionPathing")), ref ModSettings_CleanPathfinding.regionPathing, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.EnableRegionPathing.Desc") + Translator.Translate("CleanPathfinding.Settings.RegionModeThreshold.Desc")), 0f, 1f);
				if (ModSettings_CleanPathfinding.regionPathing)
				{
					if (ModSettings_CleanPathfinding.regionModeThreshold == 100000)
					{
						ModSettings_CleanPathfinding.regionModeThreshold = 1000;
					}
					string text = TaggedString.op_Implicit(TranslatorFormattedStringExtensions.Translate("CleanPathfinding.Settings.RegionModeThreshold", NamedArgument.op_Implicit("1000"), NamedArgument.op_Implicit("500"), NamedArgument.op_Implicit("2000"))) + ModSettings_CleanPathfinding.regionModeThreshold;
					TaggedString val3 = Translator.Translate("CleanPathfinding.Settings.RegionModeThreshold.Desc");
					val2.Label(text, -1f, TaggedString.op_Implicit(((TaggedString)(ref val3)).CapitalizeFirst()));
					ModSettings_CleanPathfinding.regionModeThreshold = (int)val2.Slider((float)ModSettings_CleanPathfinding.regionModeThreshold, 500f, 2000f);
				}
				else
				{
					ModSettings_CleanPathfinding.regionModeThreshold = 100000;
				}
			}
			else
			{
				ModSettings_CleanPathfinding.bias = (ModSettings_CleanPathfinding.naturalBias = (ModSettings_CleanPathfinding.roadBias = 0));
				ModSettings_CleanPathfinding.regionModeThreshold = 100000;
				ModSettings_CleanPathfinding.regionPathing = false;
			}
			if (ModSettings_CleanPathfinding.regionDistanceFixFailure)
			{
				Color color = GUI.color;
				GUI.color = Color.red;
				val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathFinding.Settings.RegionCostCalculatorFix")), ref ModSettings_CleanPathfinding.enableRegionDistanceFix, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.PatchFailure") + "\n" + Translator.Translate("CleanPathfinding.Settings.RegionCostCalculatorFix.Desc")), 0f, 1f);
				GUI.color = color;
			}
			else
			{
				val2.CheckboxLabeled(TaggedString.op_Implicit(Translator.Translate("CleanPathFinding.Settings.RegionCostCalculatorFix")), ref ModSettings_CleanPathfinding.enableRegionDistanceFix, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Settings.RegionCostCalculatorFix.Desc")), 0f, 1f);
			}
			((Listing)val2).End();
		}
	}

	public override string SettingsCategory()
	{
		return "Clean Pathfinding";
	}

	public override void WriteSettings()
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Expected O, but got Unknown
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Expected O, but got Unknown
		//IL_0224: Unknown result type (might be due to invalid IL or missing references)
		//IL_0230: Expected O, but got Unknown
		//IL_0259: Unknown result type (might be due to invalid IL or missing references)
		//IL_0265: Expected O, but got Unknown
		//IL_028e: Unknown result type (might be due to invalid IL or missing references)
		//IL_029a: Expected O, but got Unknown
		((Mod)this).WriteSettings();
		Harmony val = new Harmony(((Mod)this).Content.PackageIdPlayerFacing);
		try
		{
			CleanPathfindingUtility.UpdatePathCosts();
			DoorPathingUtility.UpdateAllDoorsOnAllMaps();
		}
		catch (Exception ex)
		{
			Log.Message("[Clean Pathfinding] Error processing settings: " + ex);
		}
		try
		{
			if (!ModSettings_CleanPathfinding.wanderTuning && patchLedger["Patch_JobGiver_Wander"])
			{
				patchLedger["Patch_JobGiver_Wander"] = false;
				val.Unpatch((MethodBase)AccessTools.Method(typeof(JobGiver_Wander), "TryGiveJob", (Type[])null, (Type[])null), (HarmonyPatchType)2, ((Mod)this).Content.PackageIdPlayerFacing);
			}
			else if (ModSettings_CleanPathfinding.wanderTuning && !patchLedger["Patch_JobGiver_Wander"])
			{
				patchLedger["Patch_JobGiver_Wander"] = true;
				val.Patch((MethodBase)AccessTools.Method(typeof(JobGiver_Wander), "TryGiveJob", (Type[])null, (Type[])null), (HarmonyMethod)null, new HarmonyMethod(typeof(Patch_JobGiver_Wander), "Postfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
			}
			if (!ModSettings_CleanPathfinding.doorPathing && patchLedger["Patch_Building_Door_GetGizmos"])
			{
				patchLedger["Patch_Building_Door_GetGizmos"] = false;
				val.Unpatch((MethodBase)AccessTools.Method(typeof(Building_Door), "GetGizmos", (Type[])null, (Type[])null), (HarmonyPatchType)2, ((Mod)this).Content.PackageIdPlayerFacing);
				val.Unpatch((MethodBase)AccessTools.Method(typeof(Building_Door), "DeSpawn", (Type[])null, (Type[])null), (HarmonyPatchType)1, ((Mod)this).Content.PackageIdPlayerFacing);
				val.Unpatch((MethodBase)AccessTools.Method(typeof(Room), "Notify_RoomShapeChanged", (Type[])null, (Type[])null), (HarmonyPatchType)2, ((Mod)this).Content.PackageIdPlayerFacing);
			}
			else if (ModSettings_CleanPathfinding.doorPathing && !patchLedger["Patch_Building_Door_GetGizmos"])
			{
				patchLedger["Patch_Building_Door_GetGizmos"] = true;
				val.Patch((MethodBase)AccessTools.Method(typeof(Building_Door), "GetGizmos", (Type[])null, (Type[])null), (HarmonyMethod)null, new HarmonyMethod(typeof(Patch_Building_Door_GetGizmos), "Postfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
				val.Patch((MethodBase)AccessTools.Method(typeof(Building_Door), "DeSpawn", (Type[])null, (Type[])null), (HarmonyMethod)null, new HarmonyMethod(typeof(Patch_Building_DoorDeSpawn), "Prefix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
				val.Patch((MethodBase)AccessTools.Method(typeof(Room), "Notify_RoomShapeChanged", (Type[])null, (Type[])null), (HarmonyMethod)null, new HarmonyMethod(typeof(Patch_Notify_RoomShapeChanged), "Postfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
			}
		}
		catch (Exception ex2)
		{
			Log.Error("[Clean Pathfinding] Error processing patching or unpatching, skipping...\n" + ex2);
		}
	}
}
