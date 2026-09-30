using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Allen.TradeCaravanLordIntegrityFix15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.tradecaravanlordintegrityfix15";

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo makeNewLord = AccessTools.Method(typeof(LordMaker), nameof(LordMaker.MakeNewLord));
                MethodInfo lordManagerTick = AccessTools.Method(typeof(LordManager), nameof(LordManager.LordManagerTick));

                harmony.Patch(makeNewLord,
                    prefix: new HarmonyMethod(typeof(Bootstrap), nameof(MakeNewLordPrefix)));
                harmony.Patch(lordManagerTick,
                    prefix: new HarmonyMethod(typeof(Bootstrap), nameof(LordManagerTickPrefix)));

                Log.Message("[Trade Caravan Lord Integrity Fix 1.5] Installed source validation and orphan repair.");
            }
            catch (Exception ex)
            {
                Log.Error("[Trade Caravan Lord Integrity Fix 1.5] Bootstrap failed: " + ex);
            }
        }

        public static bool MakeNewLordPrefix(
            Faction faction,
            ref LordJob lordJob,
            Map map,
            ref IEnumerable<Pawn> startingPawns,
            ref Lord __result)
        {
            if (!(lordJob is LordJob_TradeWithColony) || map == null || startingPawns == null)
            {
                return true;
            }

            List<Pawn> input = startingPawns.Where(p => p != null).Distinct().ToList();
            List<Pawn> eligible = new List<Pawn>(input.Count);
            List<string> rejected = new List<string>();

            for (int i = 0; i < input.Count; i++)
            {
                Pawn pawn = input[i];
                Lord existingLord = map.lordManager.LordOf(pawn);
                bool belongsToThisMap = pawn.Spawned && pawn.Map == map;
                bool usable = belongsToThisMap && !pawn.Destroyed && !pawn.Dead && existingLord == null;
                if (usable)
                {
                    eligible.Add(pawn);
                }
                else
                {
                    rejected.Add(DescribePawn(pawn, existingLord));
                }
            }

            Pawn trader = eligible.FirstOrDefault(p => p.TraderKind != null);
            if (trader != null)
            {
                if (rejected.Count > 0)
                {
                    startingPawns = eligible;
                    Log.Warning("[Trade Caravan Lord Integrity Fix 1.5] Removed " + rejected.Count +
                        " invalid cross-Lord pawn(s) before TradeWithColony creation: " +
                        string.Join("; ", rejected.ToArray()));
                }
                return true;
            }

            // Never create LordJob_TradeWithColony without a trader: its vanilla graph
            // captures FindTrader(lord) and dereferences the null pawn every Lord tick.
            if (eligible.Count > 0)
            {
                startingPawns = eligible;
                lordJob = new LordJob_ExitMapBest(LocomotionUrgency.Walk, canDig: false, canDefendSelf: true);
                Log.Error("[Trade Caravan Lord Integrity Fix 1.5] Cancelled trader-less caravan for " +
                    FactionLabel(faction) + "; " + eligible.Count +
                    " newly spawned pawn(s) were assigned an exit Lord. Rejected: " +
                    DescribeRejected(rejected));
                return true;
            }

            __result = null;
            Log.Error("[Trade Caravan Lord Integrity Fix 1.5] Blocked empty trader-less caravan for " +
                FactionLabel(faction) + ". Existing Lord memberships were preserved. Rejected: " +
                DescribeRejected(rejected));
            return false;
        }

        public static void LordManagerTickPrefix(LordManager __instance)
        {
            if (__instance == null || __instance.lords == null)
            {
                return;
            }

            for (int i = __instance.lords.Count - 1; i >= 0; i--)
            {
                Lord lord = __instance.lords[i];
                if (lord == null || !(lord.LordJob is LordJob_TradeWithColony))
                {
                    continue;
                }

                Pawn trader = FindValidTrader(lord, __instance.map);
                if (trader != null)
                {
                    continue;
                }

                int pawnCount = lord.ownedPawns == null ? 0 : lord.ownedPawns.Count;
                if (pawnCount == 0)
                {
                    Log.Error("[Trade Caravan Lord Integrity Fix 1.5] Removed existing empty TradeWithColony Lord #" +
                        lord.loadID + " before it could tick.");
                    __instance.RemoveLord(lord);
                    continue;
                }

                Log.Error("[Trade Caravan Lord Integrity Fix 1.5] Converted existing trader-less TradeWithColony Lord #" +
                    lord.loadID + " with " + pawnCount + " pawn(s) to ExitMapBest.");
                lord.SetJob(new LordJob_ExitMapBest(LocomotionUrgency.Walk, canDig: false, canDefendSelf: true));
            }
        }

        private static string DescribePawn(Pawn pawn, Lord lord)
        {
            string pawnText = pawn == null
                ? "null"
                : pawn.LabelShort + "[" + pawn.ThingID + ", spawned=" + pawn.Spawned + "]";
            string lordText = lord == null
                ? "none"
                : (lord.LordJob == null ? "null-job" : lord.LordJob.GetType().Name) + "#" + lord.loadID;
            return pawnText + " existingLord=" + lordText;
        }

        private static Pawn FindValidTrader(Lord lord, Map map)
        {
            if (lord.ownedPawns == null)
            {
                return null;
            }
            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                Pawn pawn = lord.ownedPawns[i];
                if (pawn != null && pawn.TraderKind != null && pawn.Spawned &&
                    pawn.Map == map && !pawn.Destroyed && !pawn.Dead)
                {
                    return pawn;
                }
            }
            return null;
        }

        private static string DescribeRejected(List<string> rejected)
        {
            return rejected.Count == 0 ? "none" : string.Join("; ", rejected.ToArray());
        }

        private static string FactionLabel(Faction faction)
        {
            return faction == null ? "null faction" : faction.Name + "[" + faction.loadID + "]";
        }
    }
}
