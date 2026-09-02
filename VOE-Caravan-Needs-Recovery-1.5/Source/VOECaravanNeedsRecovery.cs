using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Allen.VOECaravanNeedsFreeze
{
    [StaticConstructorOnStartup]
    public static class VOECaravanNeedsRecoveryBootstrap
    {
        static VOECaravanNeedsRecoveryBootstrap()
        {
            new Harmony("Allen.VOE.CaravanNeedsFreeze").PatchAll();
            Log.Message("[VOE Caravan Needs Recovery] Loaded for RimWorld 1.5. Outpost recovery rate: 16.6% per in-game hour (V3.1).");
        }
    }

    public static class VOECaravanNeedsRecoveryUtility
    {
        private const string OutpostDefPrefix = "Outpost_";
        private const float NeedGainPerHour = 0.166f;
        private const float TicksPerHour = 2500f;
        private const float NeedGainPerTick = NeedGainPerHour / TicksPerHour;
        private const int OutpostCacheLifetimeTicks = 60;

        private static readonly Dictionary<int, bool> outpostTileCache = new Dictionary<int, bool>();
        private static int cacheTick = -999999;

        public static bool ShouldRecover(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || pawn.Spawned)
            {
                return false;
            }

            Caravan caravan = CaravanUtility.GetCaravan(pawn);
            return ShouldRecover(caravan);
        }

        public static bool ShouldRecover(Caravan caravan)
        {
            if (caravan == null || caravan.Destroyed || !caravan.Spawned)
            {
                return false;
            }

            if (caravan.pather != null && caravan.pather.MovingNow)
            {
                return false;
            }

            return IsVOEOutpostTile(caravan.Tile);
        }

        public static void RecoverAllNeeds(Pawn_NeedsTracker tracker, Pawn pawn, int delta)
        {
            if (tracker == null || pawn == null || delta <= 0)
            {
                return;
            }

            float gain = delta * NeedGainPerTick;
            if (gain <= 0f)
            {
                return;
            }

            List<Need> needs = tracker.AllNeeds;
            if (needs == null)
            {
                return;
            }

            for (int i = 0; i < needs.Count; i++)
            {
                Need need = needs[i];
                if (need == null || need.MaxLevel <= 0f)
                {
                    continue;
                }

                try
                {
                    float pct = need.CurLevelPercentage;
                    if (float.IsNaN(pct) || float.IsInfinity(pct) || pct >= 1f)
                    {
                        continue;
                    }

                    need.CurLevelPercentage = Math.Min(1f, pct + gain);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[VOE Caravan Needs Recovery] Failed to recover need " + need.def?.defName + " for " + pawn.ToStringSafe() + ": " + ex, 78124531 ^ need.GetHashCode());
                }
            }
        }

        private static bool IsVOEOutpostTile(int tile)
        {
            if (tile < 0 || Find.WorldObjects == null)
            {
                return false;
            }

            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (now < cacheTick || now - cacheTick >= OutpostCacheLifetimeTicks)
            {
                outpostTileCache.Clear();
                cacheTick = now;
            }

            bool cached;
            if (outpostTileCache.TryGetValue(tile, out cached))
            {
                return cached;
            }

            bool result = false;
            List<WorldObject> allWorldObjects = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < allWorldObjects.Count; i++)
            {
                WorldObject worldObject = allWorldObjects[i];
                if (worldObject == null || worldObject.Destroyed || worldObject.Tile != tile || worldObject.def == null)
                {
                    continue;
                }

                string defName = worldObject.def.defName;
                if (!defName.NullOrEmpty() && defName.StartsWith(OutpostDefPrefix, StringComparison.Ordinal))
                {
                    result = true;
                    break;
                }
            }

            outpostTileCache[tile] = result;
            return result;
        }
    }

    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval))]
    public static class PawnNeedsTrackerTickIntervalRecoveryPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn_NeedsTracker __instance, Pawn ___pawn, int delta)
        {
            if (!VOECaravanNeedsRecoveryUtility.ShouldRecover(___pawn))
            {
                return true;
            }

            VOECaravanNeedsRecoveryUtility.RecoverAllNeeds(__instance, ___pawn, delta);
            return false;
        }
    }

    [HarmonyPatch(typeof(Caravan_NeedsTracker), nameof(Caravan_NeedsTracker.TrySatisfyPawnsNeeds))]
    public static class CaravanTrySatisfyPawnsNeedsRecoveryPatch
    {
        private static readonly System.Reflection.FieldInfo CaravanField = AccessTools.Field(typeof(Caravan_NeedsTracker), "caravan");

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Caravan_NeedsTracker __instance)
        {
            Caravan caravan = CaravanField != null ? CaravanField.GetValue(__instance) as Caravan : null;
            return !VOECaravanNeedsRecoveryUtility.ShouldRecover(caravan);
        }
    }
}
