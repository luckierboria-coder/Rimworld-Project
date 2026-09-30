using System;

namespace RimMT;

[Flags]
internal enum TailSignal093T0
{
	None = 0,
	ReachQuery = 1,
	ReachCapture = 2,
	ReachTopologySlice = 4,
	S4HeavyValidator = 8
}
