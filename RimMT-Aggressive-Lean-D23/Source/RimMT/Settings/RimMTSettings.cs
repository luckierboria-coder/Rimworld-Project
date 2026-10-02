using Verse;

namespace RimMT
{
    public sealed class RimMTSettings : ModSettings
    {
        public bool TextCache = true;
        public bool AdaptiveBurst = true;
        public bool WorkScanAcceleration = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref TextCache, "textCache", true);
            Scribe_Values.Look(ref AdaptiveBurst, "adaptiveBurst", true);
            Scribe_Values.Look(ref WorkScanAcceleration, "workScanAcceleration", true);
        }
    }
}
