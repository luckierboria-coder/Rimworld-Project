using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using GiddyUp;

namespace Allen.GiddyUp15ReverseLookupHotfix
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        private const string HarmonyId = "allen.giddyup15.reverselookuphotfix";

        static Bootstrap()
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(ExtendedDataStorage),
                    "ReverseLookup",
                    new Type[] { typeof(int), typeof(ExtendedPawnData).MakeByRefType() });

                MethodInfo prefix = typeof(ReverseLookupPatch).GetMethod(
                    "Prefix",
                    BindingFlags.Public | BindingFlags.Static);

                if (target == null || prefix == null)
                {
                    Log.Error("[Giddy-Up 2 1.5 ReverseLookup Hotfix] Patch target not found; hotfix disabled. Original Giddy-Up behavior remains authoritative.");
                    return;
                }

                new Harmony(HarmonyId).Patch(target, prefix: new HarmonyMethod(prefix));
                Log.Message("[Giddy-Up 2 1.5 ReverseLookup Hotfix] ACTIVE: expected ReverseLookup misses now return the original false/fallback result silently; successful lookups and Giddy-Up reservation authority are unchanged.");
            }
            catch (Exception ex)
            {
                Log.Error("[Giddy-Up 2 1.5 ReverseLookup Hotfix] Failed to install; original Giddy-Up behavior remains authoritative. " + ex);
            }
        }
    }

    public static class ReverseLookupPatch
    {
        public static bool Prefix(
            ExtendedDataStorage __instance,
            int ID,
            ref ExtendedPawnData pawnData,
            ref bool __result)
        {
            try
            {
                if (__instance == null || __instance._store == null)
                    return true;

                foreach (KeyValuePair<int, ExtendedPawnData> pair in __instance._store)
                {
                    ExtendedPawnData data = pair.Value;
                    Pawn reservedBy = data != null ? data.reservedBy : null;

                    if (reservedBy != null && reservedBy.thingIDNumber == ID)
                    {
                        pawnData = data;
                        __result = true;
                        return false;
                    }
                }

                pawnData = new ExtendedPawnData(null);
                __result = false;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
