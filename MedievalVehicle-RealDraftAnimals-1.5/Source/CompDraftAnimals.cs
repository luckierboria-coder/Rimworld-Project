using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Vehicles;

namespace MedievalVehicleDraftAnimals
{
    public sealed class CompProperties_DraftAnimals : VehicleCompProperties
    {
        public int requiredAnimals = 2;
        public float minimumBodySize = 1f;
        public float forwardOffset = 4f;
        public float lateralSpacing = 0.9f;
        public float drawAltitudeOffset = 0.08f;

        public CompProperties_DraftAnimals()
        {
            compClass = typeof(CompDraftAnimals);
        }
    }

    public sealed class CompDraftAnimals : VehicleComp, IThingHolder
    {
        private ThingOwner<Pawn> draftAnimals;
        private List<string> assignedAnimalIds = new List<string>();
        private int lastFoodTick = -999999;
        private int suppressHolderTickUntil = -1;

        public CompProperties_DraftAnimals Props => (CompProperties_DraftAnimals)props;
        public IThingHolder ParentHolder => Vehicle;

        private ThingOwner<Pawn> DraftAnimals
        {
            get
            {
                draftAnimals ??= new ThingOwner<Pawn>(this, false, LookMode.Deep);
                return draftAnimals;
            }
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            draftAnimals ??= new ThingOwner<Pawn>(this, false, LookMode.Deep);
            assignedAnimalIds ??= new List<string>();
        }

        public override bool CanDraft(out string failReason, out bool allowDevMode)
        {
            allowDevMode = false;
            failReason = string.Empty;
            if (Vehicle?.Faction != Faction.OfPlayer)
            {
                return true;
            }

            int valid = ValidAnimalCount;
            if (valid < Props.requiredAnimals)
            {
                failReason = "MVRDA_NotEnoughDraftAnimals".Translate(Vehicle.LabelShortCap, valid, Props.requiredAnimals);
                return false;
            }
            return true;
        }

        public override void CompTick()
        {
            base.CompTick();

            // Vehicle handlers use ThingOwner ticking for contained Pawns as well. The only
            // dangerous window is the same tick in which a Pawn was removed from a map: it can
            // still exist in the map TickList and would then be ticked twice (notably upsetting
            // Performance Fish caches). Resume normal contained-Pawn ticking from the next tick.
            if (DraftAnimals.Count > 0 && Find.TickManager.TicksGame > suppressHolderTickUntil)
            {
                DraftAnimals.ThingOwnerTick();
            }

            // When a VehicleCaravan returns to a map, VF spawns its top-level animal members.
            // Reattach the same Pawn instances to the vehicle on the next map tick.
            TryReattachAssignedAnimalsFromMap();

            if (Vehicle?.Faction == Faction.OfPlayer && Vehicle.Drafted && ValidAnimalCount < Props.requiredAnimals)
            {
                Vehicle.ignition.Drafted = false;
            }

            if (Find.TickManager.TicksGame - lastFoodTick >= 250)
            {
                lastFoodTick = Find.TickManager.TicksGame;
                SatisfyDraftAnimalNeeds();
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            if (Vehicle?.Faction != Faction.OfPlayer || !Vehicle.Spawned)
            {
                yield break;
            }

            yield return new Command_Action
            {
                defaultLabel = "MVRDA_GizmoLabel".Translate(),
                defaultDesc = "MVRDA_GizmoDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/DraftAnimals", false),
                action = OpenDraftAnimalMenu
            };
        }

        public override string CompInspectStringExtra()
        {
            return "MVRDA_Inspect".Translate(ValidAnimalCount, Props.requiredAnimals);
        }

        public override void PostDraw()
        {
            base.PostDraw();
            if (Vehicle == null || DraftAnimals.Count == 0)
            {
                return;
            }

            int drawIndex = 0;
            foreach (Pawn pawn in DraftAnimals.InnerListForReading)
            {
                if (pawn == null || pawn.Destroyed || pawn.Dead)
                {
                    continue;
                }

                float side = (drawIndex % 2 == 0 ? -1f : 1f) * Props.lateralSpacing;
                Vector3 drawPos = DraftAnimalDrawPos(Vehicle.DrawPos, Vehicle.Rotation, side, Props.forwardOffset);
                drawPos.y += Props.drawAltitudeOffset;
                pawn.Drawer.renderer.RenderPawnAt(drawPos, rotOverride: Vehicle.Rotation);
                drawIndex++;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref lastFoodTick, nameof(lastFoodTick), -999999);
            Scribe_Collections.Look(ref assignedAnimalIds, "assignedDraftAnimalIds", LookMode.Value);
            Scribe_Deep.Look(ref draftAnimals, "draftAnimals", new object[] { this });
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                draftAnimals ??= new ThingOwner<Pawn>(this, false, LookMode.Deep);
                assignedAnimalIds ??= new List<string>();
                RemoveDuplicateAssignmentIds();
                foreach (Pawn pawn in DraftAnimals.InnerListForReading)
                {
                    EnsureAssigned(pawn);
                }
                suppressHolderTickUntil = Find.TickManager?.TicksGame + 1 ?? 1;
            }
        }

        public ThingOwner GetDirectlyHeldThings()
        {
            return DraftAnimals;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, DraftAnimals);
        }

        internal void TransferHeldAnimalsToCaravan(VehicleCaravan caravan)
        {
            if (caravan == null || Vehicle == null || !caravan.ContainsPawn(Vehicle) || DraftAnimals.Count == 0)
            {
                return;
            }

            List<Pawn> toTransfer = DraftAnimals.InnerListForReading.ToList();
            foreach (Pawn pawn in toTransfer)
            {
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }

                EnsureAssigned(pawn);
                DraftAnimals.Remove(pawn);

                try
                {
                    if (!caravan.ContainsPawn(pawn))
                    {
                        caravan.AddPawn(pawn, true);
                    }

                    // Caravan.AddPawn does not register the pawn itself in WorldPawns. Vanilla
                    // caravan creation does this explicitly after AddPawn. Without this step the
                    // next Caravan.Tick removes the draft animal as an invalid non-world pawn.
                    if (!pawn.IsWorldPawn())
                    {
                        Find.WorldPawns.PassToWorld(pawn);
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce($"[Medieval Vehicle - Real Draft Animals] Failed moving {pawn} from {Vehicle} into VehicleCaravan: {ex}",
                        Gen.HashCombineInt(Vehicle.thingIDNumber, pawn.thingIDNumber));
                    if (pawn.ParentHolder == null)
                    {
                        DraftAnimals.TryAdd(pawn, false);
                    }
                }
            }
        }

        internal int OperationalAssignedAnimalCount(VehicleCaravan caravan)
        {
            if (assignedAnimalIds.NullOrEmpty())
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < assignedAnimalIds.Count; i++)
            {
                string id = assignedAnimalIds[i];
                Pawn pawn = FindAssignedPawnInCaravan(caravan, id);
                if (pawn == null)
                {
                    pawn = DraftAnimals.InnerListForReading.FirstOrDefault(p => p?.ThingID == id);
                }

                if (IsOperationalDraftAnimal(pawn))
                {
                    count++;
                }
            }
            return count;
        }

        internal bool HasAssignments => !assignedAnimalIds.NullOrEmpty();

        private int ValidAnimalCount
        {
            get
            {
                int count = 0;
                foreach (Pawn pawn in DraftAnimals.InnerListForReading)
                {
                    if (IsOperationalDraftAnimal(pawn))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        private bool IsOperationalDraftAnimal(Pawn pawn)
        {
            return pawn != null &&
                   !pawn.Destroyed &&
                   !pawn.Dead &&
                   !pawn.Downed &&
                   IsDraftAnimalByRace(pawn);
        }

        private bool IsDraftAnimalByRace(Pawn pawn)
        {
            return pawn != null &&
                   pawn.RaceProps != null &&
                   pawn.RaceProps.Animal &&
                   pawn.BodySize >= Props.minimumBodySize;
        }

        private bool IsEligibleMapAnimal(Pawn pawn)
        {
            if (!IsDraftAnimalByRace(pawn) || pawn.Dead || pawn.Downed)
            {
                return false;
            }

            return !pawn.Spawned || AnimalPenUtility.NeedsToBeManagedByRope(pawn);
        }

        private void OpenDraftAnimalMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            foreach (Pawn pawn in DraftAnimals.InnerListForReading.ToList())
            {
                Pawn captured = pawn;
                options.Add(new FloatMenuOption("MVRDA_Unhitch".Translate(captured.LabelShortCap), () => Unhitch(captured)));
            }

            if (DraftAnimals.Count < Props.requiredAnimals)
            {
                List<Pawn> candidates = EligibleMapAnimals().OrderBy(p => p.Position.DistanceToSquared(Vehicle.Position)).ToList();
                if (candidates.Count == 0)
                {
                    options.Add(new FloatMenuOption("MVRDA_NoEligibleAnimals".Translate(), null));
                }
                else
                {
                    foreach (Pawn pawn in candidates)
                    {
                        Pawn captured = pawn;
                        options.Add(new FloatMenuOption("MVRDA_Hitch".Translate(captured.LabelShortCap), () => Hitch(captured)));
                    }
                }
            }
            else
            {
                options.Add(new FloatMenuOption("MVRDA_SlotsFull".Translate(), null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private IEnumerable<Pawn> EligibleMapAnimals()
        {
            if (Vehicle?.Map == null)
            {
                yield break;
            }

            IReadOnlyList<Pawn> pawns = Vehicle.Map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn.Faction == Faction.OfPlayer && IsEligibleMapAnimal(pawn))
                {
                    yield return pawn;
                }
            }
        }

        private void Hitch(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || DraftAnimals.Count >= Props.requiredAnimals || !IsEligibleMapAnimal(pawn))
            {
                if (pawn != null)
                {
                    Messages.Message("MVRDA_InvalidAnimal".Translate(pawn.LabelShortCap), MessageTypeDefOf.RejectInput, false);
                }
                return;
            }

            if (Vehicle.Drafted)
            {
                Vehicle.ignition.Drafted = false;
            }

            Map map = pawn.Map;
            IntVec3 originalCell = pawn.Position;
            IntVec3 fallbackCell = CellFinder.RandomClosewalkCellNear(Vehicle.Position, map, 5);

            pawn.DeSpawn(DestroyMode.Vanish);

            if (DraftAnimals.TryAdd(pawn, false))
            {
                EnsureAssigned(pawn);
                suppressHolderTickUntil = Find.TickManager.TicksGame + 1;
            }
            else
            {
                IntVec3 respawnCell = fallbackCell.IsValid ? fallbackCell : originalCell;
                if (!respawnCell.InBounds(map))
                {
                    respawnCell = CellFinder.RandomClosewalkCellNear(Vehicle.Position, map, 5);
                }
                GenSpawn.Spawn(pawn, respawnCell, map);
            }
        }

        private void Unhitch(Pawn pawn)
        {
            if (pawn == null || !DraftAnimals.Contains(pawn) || !Vehicle.Spawned)
            {
                return;
            }

            if (Vehicle.Drafted)
            {
                Vehicle.ignition.Drafted = false;
            }

            Map map = Vehicle.Map;
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(Vehicle.Position, map, 5);
            DraftAnimals.Remove(pawn);
            RemoveAssignment(pawn);
            GenSpawn.Spawn(pawn, cell, map);
        }

        private void TryReattachAssignedAnimalsFromMap()
        {
            if (Vehicle == null || !Vehicle.Spawned || Vehicle.Map == null || assignedAnimalIds.NullOrEmpty())
            {
                return;
            }

            HashSet<string> heldIds = new HashSet<string>(DraftAnimals.InnerListForReading.Where(p => p != null).Select(p => p.ThingID));
            if (heldIds.Count >= assignedAnimalIds.Count)
            {
                return;
            }

            IReadOnlyList<Pawn> spawned = Vehicle.Map.mapPawns.AllPawnsSpawned;
            for (int i = spawned.Count - 1; i >= 0; i--)
            {
                Pawn pawn = spawned[i];
                if (pawn == null || pawn == Vehicle || pawn.Destroyed || pawn.Dead || heldIds.Contains(pawn.ThingID) ||
                    !assignedAnimalIds.Contains(pawn.ThingID))
                {
                    continue;
                }

                pawn.DeSpawn(DestroyMode.Vanish);
                if (DraftAnimals.TryAdd(pawn, false))
                {
                    heldIds.Add(pawn.ThingID);
                    suppressHolderTickUntil = Find.TickManager.TicksGame + 1;
                }
                else
                {
                    IntVec3 cell = CellFinder.RandomClosewalkCellNear(Vehicle.Position, Vehicle.Map, 5);
                    GenSpawn.Spawn(pawn, cell, Vehicle.Map);
                }
            }
        }

        private void SatisfyDraftAnimalNeeds()
        {
            if (Vehicle == null)
            {
                return;
            }

            foreach (Pawn pawn in DraftAnimals.InnerListForReading)
            {
                if (pawn == null || pawn.Dead || pawn.needs == null)
                {
                    continue;
                }

                Need_Rest rest = pawn.needs.rest;
                if (rest != null && (Vehicle.vehiclePather == null || !Vehicle.vehiclePather.Moving))
                {
                    rest.TickResting(0.8f);
                }

                Need_Food food = pawn.needs.food;
                if (food != null && food.CurCategory >= HungerCategory.Hungry)
                {
                    TryEatFromVehicleCargo(pawn, food);
                }
            }
        }

        private void TryEatFromVehicleCargo(Pawn pawn, Need_Food foodNeed)
        {
            ThingOwner<Thing> cargo = Vehicle.inventory?.innerContainer;
            if (cargo == null || cargo.Count == 0)
            {
                return;
            }

            for (int i = cargo.Count - 1; i >= 0; i--)
            {
                Thing food = cargo[i];
                if (food == null || food.Destroyed || food.def?.ingestible == null || !pawn.RaceProps.CanEverEat(food.def))
                {
                    continue;
                }

                float wanted = Mathf.Max(0.01f, foodNeed.MaxLevel - foodNeed.CurLevel);
                float nutrition = food.Ingested(pawn, wanted);
                foodNeed.CurLevel += nutrition;
                return;
            }
        }

        private void EnsureAssigned(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }

            assignedAnimalIds ??= new List<string>();
            if (!assignedAnimalIds.Contains(pawn.ThingID))
            {
                assignedAnimalIds.Add(pawn.ThingID);
            }
        }

        private void RemoveAssignment(Pawn pawn)
        {
            if (pawn == null || assignedAnimalIds == null)
            {
                return;
            }
            assignedAnimalIds.RemoveAll(id => id == pawn.ThingID);
        }

        private void RemoveDuplicateAssignmentIds()
        {
            if (assignedAnimalIds == null || assignedAnimalIds.Count < 2)
            {
                return;
            }

            HashSet<string> seen = new HashSet<string>();
            assignedAnimalIds.RemoveAll(id => id.NullOrEmpty() || !seen.Add(id));
        }

        private static Pawn FindAssignedPawnInCaravan(VehicleCaravan caravan, string thingId)
        {
            if (caravan == null || thingId.NullOrEmpty())
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

        private static Vector3 DraftAnimalDrawPos(Vector3 center, Rot4 rotation, float side, float forward)
        {
            switch (rotation.AsInt)
            {
                case 0:
                    return center + new Vector3(side, 0f, forward);
                case 1:
                    return center + new Vector3(forward, 0f, -side);
                case 2:
                    return center + new Vector3(-side, 0f, -forward);
                default:
                    return center + new Vector3(-forward, 0f, side);
            }
        }
    }

    /// <summary>
    /// Bridges the map-only hitch holder to Vehicle Framework's world caravan model.
    /// Hitched animals become ordinary top-level VehicleCaravan pawns while traveling so
    /// vanilla caravan food/health logic (and caravan-aware thirst mods) operate on the same
    /// Pawn instances. Their vehicle assignment is retained by ThingID and restored on map entry.
    /// </summary>
    public sealed class WorldComponent_DraftAnimalCaravanBridge : WorldComponent
    {
        private const int SyncIntervalTicks = 30;

        public WorldComponent_DraftAnimalCaravanBridge(World world) : base(world)
        {
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            if (Find.TickManager.TicksGame % SyncIntervalTicks != 0)
            {
                return;
            }

            List<Caravan> caravans = Find.WorldObjects.Caravans;
            for (int i = 0; i < caravans.Count; i++)
            {
                if (caravans[i] is not VehicleCaravan vehicleCaravan || vehicleCaravan.Destroyed)
                {
                    continue;
                }

                List<VehiclePawn> vehicles = vehicleCaravan.VehiclesListForReading;
                for (int j = 0; j < vehicles.Count; j++)
                {
                    VehiclePawn vehicle = vehicles[j];
                    CompDraftAnimals comp = vehicle?.GetComp<CompDraftAnimals>();
                    if (comp == null)
                    {
                        continue;
                    }

                    comp.TransferHeldAnimalsToCaravan(vehicleCaravan);

                    if (vehicleCaravan.Faction == Faction.OfPlayer && comp.HasAssignments &&
                        comp.OperationalAssignedAnimalCount(vehicleCaravan) < comp.Props.requiredAnimals &&
                        vehicleCaravan.vehiclePather != null && vehicleCaravan.vehiclePather.Moving)
                    {
                        vehicleCaravan.vehiclePather.Paused = true;
                    }
                }
            }
        }
    }
}
