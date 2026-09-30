using Verse;

namespace RimMT.Diagnostics;

public sealed class RimMTDiagnosticsSettings : ModSettings
{
	public static bool EnableWaitTrace = true;

	public static bool EnableSearchTiming = false;

	public static int SampleEveryTicks = 64;

	public static int TailThresholdMs = 20;

	public static int PostSpikeBurstTicks = 4;

	public override void ExposeData()
	{
		Scribe_Values.Look<bool>(ref EnableWaitTrace, "enableWaitTrace", true, false);
		Scribe_Values.Look<bool>(ref EnableSearchTiming, "enableSearchTiming", false, false);
		Scribe_Values.Look<int>(ref SampleEveryTicks, "sampleEveryTicks", 64, false);
		Scribe_Values.Look<int>(ref TailThresholdMs, "tailThresholdMs", 20, false);
		Scribe_Values.Look<int>(ref PostSpikeBurstTicks, "postSpikeBurstTicks", 4, false);
		if (SampleEveryTicks < 1)
		{
			SampleEveryTicks = 1;
		}
		if (TailThresholdMs < 1)
		{
			TailThresholdMs = 1;
		}
		if (PostSpikeBurstTicks < 0)
		{
			PostSpikeBurstTicks = 0;
		}
	}
}
