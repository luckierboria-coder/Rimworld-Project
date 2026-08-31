using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Vehicles;

namespace MedievalVehicleDraftAnimals
{
    /// <summary>
    /// v0.1.6 hotfix:
    /// 1) VehicleComp.CompTick does not run while a vehicle is inside a VehicleCaravan, so
    ///    hitched animals transferred to the caravan could starve even when the vehicle cargo
    ///    contained edible food. Feed assigned draft animals from that vehicle's cargo here.
    /// 2) On map re-entry, prefer the current caravan/map Pawn instance over any stale held
    ///    instance with the same ThingID. This prevents needs/health appearing to roll back when
    ///    the animal is unhitched after a world trip.
    /// </summary>
    public sealed class WorldComponent_DraftAnimalNeedsHotfix : WorldComponent
    {
        private const int FeedIntervalTicks = 250;
        private int lastFeedTick = -999999;

        private static readonly FieldInfo AssignedIdsField = typeof(CompDraftAnimals).GetField(
            "assignedAnimalIds", BindingFlags.Instance | BindingFlags.NonPublic);

        public WorldComponent_DraftAnimalNeedsHotfix(World world) : base(world)
        {
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();

            int ticks = Find.TickManager.TicksGame;
            if (ticks - lastFeedTick < FeedIntervalTicks)
            {
                return;
            }
            lastFeedTick = ticks;

            List<Caravan> caravans = Find.WorldObjects.Caravans;
            for (int i = 0; i < caravans.Count; i++)
            {
                if (caravans[i] is not VehicleCaravan caravan || caravan.Destroyed)
                {
                    continue;
                }

                List<VehiclePawn> vehicles = caravan.VehiclesListForReading;
                for (int j = 0; j < vehicles.Count; j++)
                {
                    VehiclePawn vehicle = vehicles[j];
                    CompDraftAnimals comp = vehicle?.GetComp<CompDraftAnimals>();
                    if (comp == null)
                    {
                        continue;
                    }

                    List<string> ids = GetAssignedIds(comp);
                    if (ids.NullOrEmpty())
                    {
                        continue;
                    }

                    for (int k = 0; k < ids.Count; k++)
                    {
                        Pawn pawn = FindPawn(caravan, ids[k]);
                        if (pawn == null || pawn.Dead || pawn.needs?.food == null)
                        {
                            continue;
                        }

                        Need_Food foodNeed = pawn.needs.food;
                        if (foodNeed.CurCategory >= HungerCategory.Hungry)
                        {
                            TryEatFromVehicleCargo(vehicle, pawn, foodNeed);
                        }
                    }
                }
            }
        }

        private static List<string> GetAssignedIds(CompDraftAnimals comp)
        {
            return AssignedIdsField?.GetValue(comp) as List<string>;
        }

        private static Pawn FindPawn(VehicleCaravan caravan, string thingId)
        {
            if (thingId.NullOrEmpty())
            {
                return null;
            }

            List<Pawn> pawns = caravan.PawnsListForReading;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn != null && pawn.ThingID == thingId)
                {
                    return pawn;
                }
            }
            return null;
        }

        private static bool TryEatFromVehicleCargo(VehiclePawn vehicle, Pawn pawn, Need_Food foodNeed)
        {
            ThingOwner<Thing> cargo = vehicle?.inventory?.innerContainer;
            if (cargo == null || cargo.Count == 0)
            {
                return false;
            }

            for (int i = cargo.Count - 1; i >= 0; i--)
            {
                Thing food = cargo[i];
                if (food == null || food.Destroyed || food.def?.ingestible == null ||
                    pawn.RaceProps == null || !pawn.RaceProps.CanEverEat(food.def))
                {
                    continue;
                }

                float wanted = Mathf.Max(0.01f, foodNeed.MaxLevel - foodNeed.CurLevel);
                float nutrition = food.Ingested(pawn, wanted);
                if (nutrition > 0f)
                {
                    foodNeed.CurLevel = Mathf.Min(foodNeed.MaxLevel, foodNeed.CurLevel + nutrition);
                    return true;
                }
            }
            return false;
        }
    }

    public sealed class GameComponent_DraftAnimalStateRepair : GameComponent
    {
        private const int RepairIntervalTicks = 30;
        private int nextRepairTick;

        private static readonly FieldInfo AssignedIdsField = typeof(CompDraftAnimals).GetField(
            "assignedAnimalIds", BindingFlags.Instance | BindingFlags.NonPublic);

        public GameComponent_DraftAnimalStateRepair(Game game)
        {
        }

        public override void GameComponentTick()
        {
            int ticks = Find.TickManager.TicksGame;
            if (ticks < nextRepairTick)
            {
                return;
            }
            nextRepairTick = ticks + RepairIntervalTicks;

            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                RepairMap(maps[m]);
            }
        }

        private static void RepairMap(Map map)
        {
            if (map == null)
            {
                return;
            }

            List<VehiclePawn> vehicles = map.mapPawns.AllPawnsSpawned.OfType<VehiclePawn>().ToList();
            if (vehicles.Count == 0)
            {
                return;
            }

            IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
            for (int v = 0; v < vehicles.Count; v++)
            {
                VehiclePawn vehicle = vehicles[v];
                CompDraftAnimals comp = vehicle?.GetComp<CompDraftAnimals>();
                if (comp == null)
                {
                    continue;
                }

                List<string> ids = AssignedIdsField?.GetValue(comp) as List<string>;
                if (ids.NullOrEmpty())
                {
                    continue;
                }

                ThingOwner holder = comp.GetDirectlyHeldThings();
                if (holder == null)
                {
                    continue;
                }

                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    Pawn mapPawn = null;
                    for (int p = 0; p < spawned.Count; p++)
                    {
                        Pawn candidate = spawned[p];
                        if (candidate != null && candidate != vehicle && candidate.ThingID == id)
                        {
                            mapPawn = candidate;
                            break;
                        }
                    }

                    if (mapPawn == null)
                    {
                        continue;
                    }

                    Pawn heldPawn = null;
                    for (int h = 0; h < holder.Count; h++)
                    {
                        if (holder[h] is Pawn candidate && candidate.ThingID == id)
                        {
                            heldPawn = candidate;
                            break;
                        }
                    }

                    if (heldPawn != null && ReferenceEquals(heldPawn, mapPawn))
                    {
                        continue;
                    }

                    if (heldPawn != null)
                    {
                        holder.Remove(heldPawn);
                    }

                    IntVec3 originalCell = mapPawn.Position;
                    mapPawn.DeSpawn(DestroyMode.Vanish);
                    if (!holder.TryAdd(mapPawn, false))
                    {
                        GenSpawn.Spawn(mapPawn, originalCell, map);
                        if (heldPawn != null && heldPawn.ParentHolder == null)
                        {
                            holder.TryAdd(heldPawn, false);
                        }
                    }
                    else if (heldPawn != null && heldPawn.ParentHolder == null && !heldPawn.Destroyed)
                    {
                        // Same ThingID but stale state copy. It must not survive alongside the
                        // authoritative caravan/map Pawn instance.
                        heldPawn.Destroy(DestroyMode.Vanish);
                    }
                }
            }
        }
    }
}
