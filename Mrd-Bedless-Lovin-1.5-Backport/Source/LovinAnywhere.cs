using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace LovinAnywhere
{
    public class LovinSettings : ModSettings
    {
        public float chanceInteracao = 1f;
        public int duracaoAto = 1250; // Retained only for backward settings compatibility.

        public override void ExposeData()
        {
            Scribe_Values.Look(ref chanceInteracao, "chanceInteracao", 1f);
            Scribe_Values.Look(ref duracaoAto, "duracaoAto", 1250);
            chanceInteracao = Mathf.Clamp01(chanceInteracao);
            base.ExposeData();
        }
    }

    public class LovinMod : Mod
    {
        internal static LovinSettings settings;

        public LovinMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<LovinSettings>();
        }

        public override string SettingsCategory()
        {
            return "MBL_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("MBL_InteractionChance".Translate(settings.chanceInteracao.ToStringPercent()));
            settings.chanceInteracao = listing.Slider(settings.chanceInteracao, 0f, 1f);
            listing.Gap();
            listing.Label("MBL_DurationManagedExternally".Translate());
            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony("MrDeliberto.Bedless.Lovin.1.5.RJWBridge");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Message("[Mrd Bedless Lovin 1.5] v5 Lovin bridge active. Romantic meeting spots dispatch vanilla JobDefOf.Lovin so RJW/other Lovin patches can participate.");
            }
            catch (Exception ex)
            {
                Log.Error("[Mrd Bedless Lovin 1.5] Failed to initialize Lovin bridge: " + ex);
            }
        }
    }

    internal static class MeetingSpotUtility
    {
        internal const string SpotDefName = "InteracaoPrivada_Spot";

        private static readonly FieldInfo OwnersField = AccessTools.Field(typeof(Building_Bed), "owners");
        private static bool ownersFieldFailureLogged;

        internal static bool IsMeetingSpot(Thing thing)
        {
            return thing != null && IsMeetingSpot(thing.def);
        }

        internal static bool IsMeetingSpot(ThingDef def)
        {
            return def != null && def.defName == SpotDefName;
        }

        internal static List<Pawn> GetOwners(Building_Bed bed)
        {
            if (bed == null || OwnersField == null)
            {
                if (OwnersField == null && !ownersFieldFailureLogged)
                {
                    ownersFieldFailureLogged = true;
                    Log.Error("[Mrd Bedless Lovin 1.5] Could not resolve Building_Bed owners field. Meeting-point Lovin is disabled to protect normal bed ownership.");
                }
                return null;
            }

            try
            {
                return OwnersField.GetValue(bed) as List<Pawn>;
            }
            catch (Exception ex)
            {
                if (!ownersFieldFailureLogged)
                {
                    ownersFieldFailureLogged = true;
                    Log.Error("[Mrd Bedless Lovin 1.5] Could not access Building_Bed owners field. Meeting-point Lovin is disabled: " + ex);
                }
                return null;
            }
        }

        internal static Building_Bed GetMeetingSpotFromJob(Job job)
        {
            if (job == null)
                return null;

            Thing thing = job.GetTarget(TargetIndex.B).Thing;
            Building_Bed bed = thing as Building_Bed;
            return bed != null && IsMeetingSpot(bed) ? bed : null;
        }

        internal static bool PawnIsActivelyUsing(Pawn pawn, Building_Bed bed)
        {
            if (pawn == null || bed == null || pawn.jobs == null || pawn.CurJob == null)
                return false;

            return GetMeetingSpotFromJob(pawn.CurJob) == bed;
        }

        internal static void PruneStaleOwners(Building_Bed bed)
        {
            List<Pawn> owners = GetOwners(bed);
            if (owners == null)
                return;

            for (int i = owners.Count - 1; i >= 0; i--)
            {
                Pawn owner = owners[i];
                if (owner == null || owner.Destroyed || owner.Dead || !PawnIsActivelyUsing(owner, bed))
                    owners.RemoveAt(i);
            }
        }

        internal static bool HasFreeTemporarySlot(Building_Bed bed)
        {
            List<Pawn> owners = GetOwners(bed);
            if (owners == null)
                return false;

            PruneStaleOwners(bed);
            return owners.Count < bed.SleepingSlotsCount;
        }

        internal static bool AddTemporarySlot(Pawn pawn, Building_Bed bed)
        {
            if (pawn == null || bed == null)
                return false;

            List<Pawn> owners = GetOwners(bed);
            if (owners == null)
                return false;

            PruneStaleOwners(bed);
            if (owners.Contains(pawn))
                return true;
            if (owners.Count >= bed.SleepingSlotsCount)
                return false;

            owners.Add(pawn);
            return true;
        }

        internal static void ReleaseTemporarySlot(Pawn pawn, Building_Bed bed)
        {
            if (pawn == null || bed == null)
                return;

            List<Pawn> owners = GetOwners(bed);
            if (owners != null)
                owners.Remove(pawn);
        }

        internal static void ClearTemporarySlots(Building_Bed bed)
        {
            List<Pawn> owners = GetOwners(bed);
            if (owners != null)
                owners.Clear();
        }
    }

    public class Building_RomanticMeetingSpot : Building_Bed
    {
        public override Color DrawColor
        {
            get { return Color.white; }
        }

        public override Color DrawColorTwo
        {
            get { return Color.white; }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            // Do not expose bed owner / prisoner / medical-bed controls for a meeting point.
            yield break;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            MeetingSpotUtility.ClearTemporarySlots(this);
            base.SpawnSetup(map, respawningAfterLoad);

            // A romantic meeting point is never a medical or prisoner bed.
            MeetingSpotUtility.ClearTemporarySlots(this);
            if (Medical)
                Medical = false;
            if (ForPrisoners)
                ForPrisoners = false;
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Building_Bed.DeSpawn normally unclaims every listed owner. Our owners are
            // temporary Lovin slot markers and must never affect a pawn's real OwnedBed.
            MeetingSpotUtility.ClearTemporarySlots(this);
            base.DeSpawn(mode);
        }
    }

    public class JoyGiver_InteracaoPrivada : JoyGiver
    {
        private const float SearchRadius = 80f;

        public override Job TryGiveJob(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Drafted || pawn.Map == null)
                return null;

            LovinSettings cfg = LovinMod.settings;
            if (cfg != null && !Rand.Chance(cfg.chanceInteracao))
                return null;

            // Prisoner lovin has different routing in RJW and vanilla bed rules. Do not
            // force the meeting-point bridge into that path.
            if (pawn.IsPrisoner)
                return null;

            Pawn partner = LovePartnerRelationUtility.ExistingLovePartner(pawn);
            if (!CanUsePartner(pawn, partner))
                return null;

            ThingDef spotDef = DefDatabase<ThingDef>.GetNamedSilentFail(MeetingSpotUtility.SpotDefName);
            if (spotDef == null)
                return null;

            Thing spot = GenClosest.ClosestThingReachable(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForDef(spotDef),
                PathEndMode.Touch,
                TraverseParms.For(pawn),
                SearchRadius,
                delegate(Thing t)
                {
                    Building_Bed bed = t as Building_Bed;
                    if (bed == null || !bed.Spawned || bed.IsForbidden(pawn))
                        return false;

                    if (!MeetingSpotUtility.HasFreeTemporarySlot(bed))
                        return false;

                    return pawn.CanReserve(bed, bed.SleepingSlotsCount, -1, null, false);
                });

            if (spot == null)
                return null;

            // Critical compatibility point: use the actual vanilla Lovin JobDef and
            // JobDriver_Lovin. RJW patches JobDriver_Lovin.MakeNewToils, so its own
            // sex-type/needs/experience/pregnancy logic now sees this interaction.
            return JobMaker.MakeJob(JobDefOf.Lovin, partner, spot);
        }

        private static bool CanUsePartner(Pawn pawn, Pawn partner)
        {
            if (partner == null || partner == pawn || !partner.Spawned || partner.Map != pawn.Map)
                return false;
            if (partner.Drafted || partner.Dead || partner.Downed || partner.IsPrisoner)
                return false;
            if (partner.InMentalState)
                return false;
            if (partner.jobs == null || pawn.jobs == null)
                return false;
            if (!pawn.CanReach(partner, PathEndMode.Touch, Danger.Some))
                return false;
            return true;
        }
    }

    // Save/backward-compatibility shim for jobs created by v1-v4. New jobs no longer
    // use this driver; they are JobDefOf.Lovin so RJW can patch the real vanilla path.
    public class JobDriver_InteracaoPrivada : JobDriver_Lovin
    {
    }

    [HarmonyPatch(typeof(RestUtility), "CanUseBedEver")]
    internal static class Patch_RestUtility_CanUseBedEver
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ThingDef bedDef, ref bool __result)
        {
            if (!MeetingSpotUtility.IsMeetingSpot(bedDef))
                return true;

            // Keep the spot out of ordinary rest/medical bed searches. Direct Lovin jobs
            // already have the target and do not need RestUtility.FindBedFor.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_Ownership), "ClaimBedIfNonMedical")]
    internal static class Patch_PawnOwnership_ClaimBed
    {
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_Ownership), "pawn");

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Pawn_Ownership __instance, Building_Bed newBed)
        {
            if (!MeetingSpotUtility.IsMeetingSpot(newBed))
                return true;

            Pawn pawn = PawnField != null ? PawnField.GetValue(__instance) as Pawn : null;
            if (pawn == null)
                return false;

            // Emulate only the slot ownership expected by JobDriver_Lovin's bed toils.
            // Deliberately do NOT call UnclaimBed() and do NOT alter Pawn_Ownership.OwnedBed.
            MeetingSpotUtility.AddTemporarySlot(pawn, newBed);
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver), "Cleanup")]
    internal static class Patch_JobDriver_Cleanup
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(JobDriver __instance)
        {
            if (__instance == null || __instance.pawn == null || __instance.job == null)
                return;

            Building_Bed bed = MeetingSpotUtility.GetMeetingSpotFromJob(__instance.job);
            if (bed != null)
                MeetingSpotUtility.ReleaseTemporarySlot(__instance.pawn, bed);
        }
    }
}
