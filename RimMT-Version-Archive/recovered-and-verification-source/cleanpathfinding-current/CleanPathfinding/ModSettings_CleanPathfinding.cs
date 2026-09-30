using UnityEngine;
using Verse;

namespace CleanPathfinding;

public class ModSettings_CleanPathfinding : ModSettings
{
	public enum Tab
	{
		tuning,
		doorPathing,
		rules,
		misc
	}

	public static int bias = 8;

	public static int naturalBias;

	public static int roadBias = 9;

	public static int exitRange;

	public static int doorPathingSide = 250;

	public static int doorPathingEmergency = 500;

	public static int wanderDelay = 0;

	public static int regionModeThreshold = 1000;

	public static int heuristicAdjuster = 90;

	public static int darknessPenalty = 2;

	public static bool factorLight = true;

	public static bool factorCarryingPawn = true;

	public static bool factorBleeding = true;

	public static bool logging;

	public static bool doorPathing = true;

	public static bool optimizeCollider = true;

	public static bool exitTuning;

	public static bool wanderTuning;

	public static bool regionPathing = true;

	public static bool enableTuning = true;

	public static bool enableRegionDistanceFix = true;

	public static bool regionDistanceFixFailure = false;

	public static Vector2 scrollPos = Vector2.zero;

	public static Tab selectedTab = Tab.tuning;

	public override void ExposeData()
	{
		Scribe_Values.Look<int>(ref bias, "bias", 5, false);
		Scribe_Values.Look<int>(ref naturalBias, "naturalBias", 0, false);
		Scribe_Values.Look<int>(ref roadBias, "roadBias", 9, false);
		Scribe_Values.Look<int>(ref regionModeThreshold, "regionModeThreshold", 100000, false);
		Scribe_Values.Look<int>(ref heuristicAdjuster, "heuristicAdjuster", 90, false);
		Scribe_Values.Look<int>(ref darknessPenalty, "darknessPenalty", 2, false);
		Scribe_Values.Look<bool>(ref factorLight, "factorLight", true, false);
		Scribe_Values.Look<bool>(ref factorCarryingPawn, "factorCarryingPawn", true, false);
		Scribe_Values.Look<bool>(ref factorBleeding, "factorBleeding", true, false);
		Scribe_Values.Look<int>(ref exitRange, "exitRange", 0, false);
		Scribe_Values.Look<bool>(ref doorPathing, "doorPathing", true, false);
		Scribe_Values.Look<int>(ref doorPathingSide, "doorPathingSide", 250, false);
		Scribe_Values.Look<int>(ref doorPathingEmergency, "doorPathingEmergency", 500, false);
		Scribe_Values.Look<int>(ref wanderDelay, "wanderDelay", 0, false);
		Scribe_Values.Look<bool>(ref optimizeCollider, "optimizeCollider", true, false);
		Scribe_Values.Look<bool>(ref exitTuning, "exitTuning", false, false);
		Scribe_Values.Look<bool>(ref wanderTuning, "wanderTuning", false, false);
		Scribe_Values.Look<bool>(ref regionPathing, "regionPathing", true, false);
		Scribe_Values.Look<bool>(ref enableTuning, "enableTuning", true, false);
		Scribe_Values.Look<bool>(ref enableRegionDistanceFix, "enableRegionDistanceFix", true, false);
		((ModSettings)this).ExposeData();
	}
}
