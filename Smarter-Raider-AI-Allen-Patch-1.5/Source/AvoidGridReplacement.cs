using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Allen.SmarterRaiderAI.Patch15
{
    internal static class AvoidGridReplacement
    {
        private static Traverse instance;
        private static int counter;
        private static ByteGrid tempGrid;
        private static int lastUpdateTicks;
        private static int activeLosCost;

        private const int Stage1Ticks = 30 * 60;
        private const int Stage2Ticks = 60 * 60;
        private const int Stage3Ticks = 90 * 60;

        private static readonly Dictionary<Map, int> assaultStartTicks = new Dictionary<Map, int>();

        public static bool Prefix(AvoidGrid __instance)
        {
            instance = Traverse.Create(__instance);
            var gridDirty = instance.Field("gridDirty");
            if (lastUpdateTicks != 0 && (Find.TickManager.TicksGame - lastUpdateTicks) / 60 < 5)
            {
                gridDirty.SetValue(false);
                return false;
            }

            gridDirty.SetValue(false);
            __instance.Grid.Clear(0);
            counter = 0;

            try
            {
                activeLosCost = GetRaidAdjustedLosCost(__instance.map);

                var corpses = __instance.map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)
                    .Where(x => ((Corpse)x).Age < 1800 && (((Corpse)x).InnerPawn?.Faction?.HostileTo(Faction.OfPlayer) ?? false));
                foreach (Corpse corpse in corpses)
                {
                    PrintAvoidGridAroundPos(__instance, __instance.map, corpse.Position, 1, 1000 * (1800 - corpse.Age) / 1800);
                }

                var downed = __instance.map.mapPawns.SpawnedDownedPawns.Where(x => x.Faction.HostileTo(Faction.OfPlayer));
                foreach (Pawn raider in downed)
                {
                    PrintAvoidGridAroundPos(__instance, __instance.map, raider.Position, 1, PogoAI.Init.settings.costLOS);
                }

                var draftedColonists = __instance.map.PlayerPawnsForStoryteller.Where(x =>
                    x.Drafted && x.equipment?.PrimaryEq != null && x.CurJobDef == JobDefOf.Wait_Combat && x.TargetCurrentlyAimingAt == null);
                foreach (Pawn pawn in draftedColonists)
                {
                    Verb verb = pawn.equipment.PrimaryEq.PrimaryVerb;
                    if (verb.IsMeleeAttack)
                    {
                        PrintAvoidGridAroundPos(__instance, __instance.map, pawn.Position, 1, activeLosCost);
                    }
                    else
                    {
                        PrintAvoidGridLOSThing(__instance, pawn.Map, pawn.Position, verb);
                    }
                }

                List<Building> allBuildingsColonist = __instance.map.listerBuildings.allBuildingsColonist;
                for (int i = 0; i < allBuildingsColonist.Count; i++)
                {
                    Building building = allBuildingsColonist[i];
                    if (!building.def.building.ai_combatDangerous)
                    {
                        continue;
                    }

                    CompEquippable equip;
                    bool threatCondition;

                    if (PogoAI.Init.combatExtended && building.GetType().ToString() == "CombatExtended.Building_TurretGunCE")
                    {
                        equip = (CompEquippable)building.GetType().GetProperty("GunCompEq").GetValue(building, null);
                        bool active = (bool)building.GetType().GetProperty("Active").GetValue(building, null);
                        var activePowerSource = (CompPowerTrader)building.GetType().GetProperty("PowerComp").GetValue(building, null);
                        var currentTarget = (LocalTargetInfo)building.GetType().GetProperty("CurrentTarget").GetValue(building, null);
                        bool emptyMagazine = (bool)building.GetType().GetProperty("EmptyMagazine").GetValue(building, null);
                        bool isMannable = (bool)building.GetType().GetProperty("IsMannable").GetValue(building, null);
                        bool mannedNow = ((CompMannable)building.GetType().GetProperty("MannableComp").GetValue(building, null))?.MannedNow ?? false;

                        threatCondition = (active || (activePowerSource?.PowerNet?.CanPowerNow(activePowerSource) ?? false))
                            && currentTarget == null
                            && !emptyMagazine
                            && equip != null
                            && (!isMannable || mannedNow);
                    }
                    else
                    {
                        Building_TurretGun turret = building as Building_TurretGun;
                        if (turret == null)
                        {
                            continue;
                        }

                        equip = turret.GunCompEq;
                        CompMannable mannableComp = turret.GetComp<CompMannable>();
                        bool isMannable = turret.IsMannable || mannableComp != null;
                        bool mannedNow = mannableComp?.MannedNow ?? false;

                        threatCondition = equip != null
                            && (turret.Active
                                || (turret.PowerComp?.PowerNet?.CanPowerNow(
                                    Traverse.Create(turret).Field("powerComp").GetValue<CompPowerTrader>()) ?? false))
                            && turret.TargetCurrentlyAimingAt == null
                            && (!isMannable || mannedNow)
                            && (turret.refuelableComp?.HasFuel ?? true);
                    }

                    if (threatCondition)
                    {
                        PrintAvoidGridLOSThing(__instance, building.Map, building.Position, equip.PrimaryVerb);
                    }
                }

                instance.Method("ExpandAvoidGridIntoEdifices").GetValue();
            }
            catch (Exception e)
            {
                Log.Error($"[Smarter Raider AI - Allen Patch 1.5] AvoidGrid error: {e.Message}\n{e.StackTrace}");
            }

            lastUpdateTicks = Find.TickManager.TicksGame;
            return false;
        }

        private static int GetRaidAdjustedLosCost(Map map)
        {
            int baseCost = Math.Max(0, PogoAI.Init.settings.costLOS);
            int now = Find.TickManager.TicksGame;
            bool activeHostileAssault = false;

            if (map?.lordManager?.lords != null)
            {
                for (int i = 0; i < map.lordManager.lords.Count; i++)
                {
                    Lord lord = map.lordManager.lords[i];
                    if (lord?.faction != null && lord.faction.HostileTo(Faction.OfPlayer) && lord.LordJob is LordJob_AssaultColony)
                    {
                        activeHostileAssault = true;
                        break;
                    }
                }
            }

            if (!activeHostileAssault)
            {
                assaultStartTicks.Remove(map);
                return baseCost;
            }

            if (!assaultStartTicks.TryGetValue(map, out int startTick))
            {
                assaultStartTicks[map] = now;
                return baseCost;
            }

            int elapsed = Math.Max(0, now - startTick);
            if (elapsed < Stage1Ticks)
            {
                return baseCost;
            }
            if (elapsed < Stage2Ticks)
            {
                return ScaleCost(baseCost, 35, 45);
            }
            if (elapsed < Stage3Ticks)
            {
                return ScaleCost(baseCost, 25, 45);
            }
            return ScaleCost(baseCost, 15, 45);
        }

        private static int ScaleCost(int baseCost, int numerator, int denominator)
        {
            if (baseCost <= 0)
            {
                return 0;
            }
            return Math.Max(1, (int)Math.Round(baseCost * (numerator / (double)denominator)));
        }

        private static void PrintAvoidGridLOSThing(AvoidGrid __instance, Map map, IntVec3 pos, Verb verb)
        {
            if (verb?.Caster?.def?.defName == "Turret_RocketswarmLauncher")
            {
                return;
            }

            float range = verb.verbProps.range;
            tempGrid = new ByteGrid(map);
            float minRange = verb.verbProps.EffectiveMinRange(true);
            int numCells = GenRadial.NumCellsInRadius(range);

            for (int i = numCells; i > (minRange < 1f ? 0 : GenRadial.NumCellsInRadius(minRange)); i--)
            {
                IntVec3 cell = pos + GenRadial.RadialPattern[i];
                if (cell.InBounds(map) && cell.WalkableByNormal(map)
                    && tempGrid[cell] == 0
                    && GenSight.LineOfSight(pos, cell, map, true, IncrementAvoidGrid, 0, 0))
                {
                    counter++;
                }
            }
        }

        private static bool IncrementAvoidGrid(IntVec3 cell)
        {
            if (tempGrid[cell] == 0)
            {
                instance.Method("IncrementAvoidGrid", cell, activeLosCost).GetValue();
                IncrementLocalAvoidGrid(tempGrid, cell, activeLosCost);
            }
            return true;
        }

        private static void IncrementLocalAvoidGrid(ByteGrid grid, IntVec3 cell, int amount)
        {
            byte value = grid[cell];
            value = (byte)Mathf.Min(255, value + amount);
            grid[cell] = value;
        }

        private static void PrintAvoidGridAroundPos(AvoidGrid __instance, Map map, IntVec3 pos, int radius, int amount)
        {
            for (int i = 0; i < GenRadial.NumCellsInRadius(radius); i++)
            {
                IntVec3 cell = pos + GenRadial.RadialPattern[i];
                if (cell.InBounds(map) && cell.WalkableByNormal(map) && __instance.Grid[cell] == 0)
                {
                    Traverse.Create(__instance).Method("IncrementAvoidGrid", cell, amount).GetValue();
                }
            }
        }
    }
}
