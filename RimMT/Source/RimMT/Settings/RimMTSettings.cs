using Verse;

namespace RimMT
{
    public sealed class RimMTSettings : ModSettings
    {
        // Production controls exposed by V0.9.2 Unified Lean.
        public bool TextCache = true;
        public bool WorkScanAcceleration = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref TextCache, "textCache", true);
            Scribe_Values.Look(ref WorkScanAcceleration, "workScanAcceleration", true);
        }
    }
}
