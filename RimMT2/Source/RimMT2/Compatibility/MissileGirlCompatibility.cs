using System;
using System.Linq;
using Verse;

namespace RimMT2.Compatibility
{
    internal static class MissileGirlCompatibility
    {
        internal static bool Active { get; private set; }

        internal static void Audit()
        {
            try
            {
                Active = LoadedModManager.RunningModsListForReading.Any(mod =>
                    string.Equals(mod.PackageIdPlayerFacing, "vr.missilegirl", StringComparison.OrdinalIgnoreCase));
                if (Active)
                {
                    Log.Message("[RimMT 2.0] Missile Girl detected. Worker policy: no StatWorker, Pawn needs, Notify_Dirty, RocketStates, Verse objects or Missile Girl caches on workers. Load order keeps Missile Girl last.");
                }
                else
                {
                    Log.Message("[RimMT 2.0] Missile Girl not active.");
                }
            }
            catch (Exception exception)
            {
                Log.Warning("[RimMT 2.0] Missile Girl compatibility audit failed closed: " + exception.Message);
                Active = true;
            }
        }
    }
}
