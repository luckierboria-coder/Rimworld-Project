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
            Log.Message("[VOE Caravan Needs Recovery] Loaded for RimWorld 1.5. Outpost recovery rate: 16.6% per in-game hour (V3.2 low-overhead).");
        }
    }

    public static class VOECaravanNeedsRecoveryUtility
    {
        private const string OutpostDefPrefix = "Outpost_";
        private const float NeedGainPerHour = 0.166f;
        private const float TicksPerHour = 2500f;
        private const float NeedGainPerTick = NeedGainPerHour / TicksPerHour;
        private const int RecoveryApplyIntervalTicks = 150;
        private const int OutpostCacheLifetimeTicks = 250;

        private static readonly Dictionary<int, bool> outpostTileCache = new Dictionary<int, bool>();
        private static readonly Dictionary<Caravan, int> lastRecoveryTick = new Dictionary<Caravan, int>();
        private static int cacheTick = -999999;

        public static bool ShouldRecover(Pawn pawn)
        {
            // Hot-path fast exit: map Pawns never need any world/caravan lookup.
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

            // Never cache movement state: recovery must stop immediately when travel resumes.
            if (caravan.pather != null && caravan.pather.MovingNow)
            {
                return false;
            }

            return IsVOEOutpostTile(caravan.Tile);
        }

        public static void ClearRecoveryClock(Caravan caravan)
        {
            if (caravan != null)
            {
                lastRecoveryTick.Remove(caravan);
            }
        }

        public static void RecoverCaravanNeeds(Caravan caravan)
        {
            if (caravan == null)
            {
                return;
            }

            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int previous;
            if (!lastRecoveryTick.TryGetValue(caravan, out previous))
            {
                lastRecoveryTick[caravan] = now;
                return;
            }

            int elapsedTicks = now - previous;
            if (elapsedTicks < RecoveryApplyIntervalTicks)
            {
                return;
            }

            lastRecoveryTick[caravan] = now;
            float gain = elapsedTicks * NeedGainPerTick;
            if (gain <= 0f)
            {
                return;
            }

            List<Pawn> pawns = caravan.PawnsListForReading;
            if (pawns == null)
            {
                return;
            }

            for (int p = 0; p < pawns.Count; p++)
            {
                Pawn pawn = pawns[p];
                if (pawn == null || pawn.Dead || pawn.Destroyed || pawn.needs == null)
                {
                    continue;
                }

                List<Need> needs = pawn.needs.AllNeeds;
                if (needs == null)
                {
                    continue;
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

    // RimWorld 1.5: this is the already-proven V2 freeze hook.
    // It does no recovery work; it only suppresses normal NeedInterval decay while the caravan rests at an Outpost.
    [HarmonyPatch(typeof(Pawn_NeedsTracker), "NeedsTrackerTick")]
    public static class PawnNeedsTrackerFreezePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn ___pawn)
        {
            return !VOECaravanNeedsRecoveryUtility.ShouldRecover(___pawn);
        }
    }

    // RimWorld 1.5 Caravan.Tick calls Caravan_NeedsTracker.NeedsTrackerTick().
    // Recovery is applied in one batch per caravan, at most once per 150 ticks.
    // Returning false also suppresses vanilla caravan auto-satisfaction/food-drug consumption while resting at the Outpost.
    [HarmonyPatch(typeof(Caravan_NeedsTracker), "NeedsTrackerTick")]
    public static class CaravanNeedsTrackerRecoveryPatch
    {
        private static readonly System.Reflection.FieldInfo CaravanField = AccessTools.Field(typeof(Caravan_NeedsTracker), "caravan");

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Caravan_NeedsTracker __instance)
        {
            Caravan caravan = CaravanField != null ? CaravanField.GetValue(__instance) as Caravan : null;
            if (!VOECaravanNeedsRecoveryUtility.ShouldRecover(caravan))
            {
                VOECaravanNeedsRecoveryUtility.ClearRecoveryClock(caravan);
                return true;
            }

            VOECaravanNeedsRecoveryUtility.RecoverCaravanNeeds(caravan);
            return false;
        }
    }
}
