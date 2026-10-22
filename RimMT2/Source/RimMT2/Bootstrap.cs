using HarmonyLib;
using Verse;

namespace RimMT2
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            Harmony harmony = new Harmony("allen.rimmt2");
            harmony.PatchAll(typeof(Bootstrap).Assembly);
            Compatibility.MissileGirlCompatibility.Audit();
            Log.Message("[RimMT 2.0 M1.1.5] Unity ManagedJob/JobHandle fabric loaded; WorkGiver census is opt-in.");
        }
    }
}
