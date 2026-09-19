using HarmonyLib;
using Verse;
using Verse.AI;

namespace Allen.BPCAreaOOBFix
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            var original = AccessTools.Method(typeof(Job), "IsTargetOutsideArea");
            var prefix = AccessTools.Method(typeof(JobIsTargetOutsideAreaPatch), nameof(JobIsTargetOutsideAreaPatch.Prefix));

            if (original == null || prefix == null)
            {
                Log.Error("[BPC Area OOB Fix] Failed to locate patch target/prefix.");
                return;
            }

            new Harmony("allen.bpc.area.oobfix").Patch(
                original,
                prefix: new HarmonyMethod(prefix));

            Log.Message("[BPC Area OOB Fix] Active.");
        }
    }

    internal static class JobIsTargetOutsideAreaPatch
    {
        public static bool Prefix(LocalTargetInfo __0, Area __1, ref bool __result)
        {
            if (!__0.IsValid)
                return true;

            Map map = __1?.Map;
            IntVec3 cell = __0.Cell;

            if (map == null || !cell.InBounds(map))
            {
                __result = true;
                return false;
            }

            Thing thing = __0.Thing;
            if (thing != null && thing.MapHeld != null && thing.MapHeld != map)
            {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
