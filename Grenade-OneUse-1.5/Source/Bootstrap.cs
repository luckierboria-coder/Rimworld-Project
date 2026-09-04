using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GrenadeOneUse15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                bool combatExtended = LoadedModManager.RunningModsListForReading.Any(m =>
                    string.Equals(m.PackageIdPlayerFacing, "CETeam.CombatExtended", StringComparison.OrdinalIgnoreCase));

                if (combatExtended)
                {
                    Log.Message("[Grenade One-Use 1.5] Combat Extended detected; patch disabled because CE already supplies one-use grenade semantics.");
                    return;
                }

                new Harmony("allen.grenade.oneuse").PatchAll(Assembly.GetExecutingAssembly());
                Log.Message("[Grenade One-Use 1.5] loaded. Vanilla-style thrown projectile weapons now consume one physical item after a successful throw.");
            }
            catch (Exception e)
            {
                Log.Error("[Grenade One-Use 1.5] startup failed: " + e);
            }
        }
    }
}
