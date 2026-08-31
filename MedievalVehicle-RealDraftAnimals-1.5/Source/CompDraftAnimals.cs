using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
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
        private int lastFoodTick = -999999;

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
            DraftAnimals.ThingOwnerTick();

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
            Scribe_Deep.Look(ref draftAnimals, "draftAnimals", new object[] { this });
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                draftAnimals ??= new ThingOwner<Pawn>(this, false, LookMode.Deep);
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
            // IMPORTANT: hitched animals are held inside the vehicle's ThingOwner and are no
            // longer spawned on the map. Do not call map/pen-state helpers here. Those helpers
            // are only meaningful while choosing a free animal from the map and can return false
            // after DeSpawn, which previously made two visibly hitched horses count as 0/2.
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

            // While the pawn is still on the map, keep the original rope-managed preference.
            // This filters the normal horse/yak/camel/muffalo-style livestock without poisoning
            // the operational check after the pawn has been moved into the vehicle holder.
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

            pawn.DeSpawn(DestroyMode.WillReplace);
            if (!DraftAnimals.TryAdd(pawn, false))
            {
                GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(Vehicle.Position, Vehicle.Map, 5), Vehicle.Map);
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

            DraftAnimals.Remove(pawn);
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(Vehicle.Position, Vehicle.Map, 5);
            GenSpawn.Spawn(pawn, cell, Vehicle.Map);
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
}
