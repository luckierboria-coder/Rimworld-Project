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
        // Harmony positional parameters avoid depending on private parameter names.
        public static bool Prefix(LocalTargetInfo __0, Area __1, ref bool __result)
        {
            // Vanilla already handles invalid targets safely.
            if (!__0.IsValid)
                return true;

            Map map = __1?.Map;
            IntVec3 cell = __0.Cell;

            // A valid LocalTargetInfo can still expose a Cell outside this Area's map.
            // Treat it as outside the allowed area instead of indexing Area[cell].
            if (map == null || !cell.InBounds(map))
            {
                __result = true;
                return false;
            }

            // Also handle a Thing target retained from another map whose coordinates
            // happen to be numerically in-bounds on this map.
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
