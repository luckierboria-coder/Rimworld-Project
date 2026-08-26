using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace LovinAnywhere
{
    public class LovinSettings : ModSettings
    {
        public float chanceInteracao = 1f;
        public int duracaoAto = 1250;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref chanceInteracao, "chanceInteracao", 1f);
            Scribe_Values.Look(ref duracaoAto, "duracaoAto", 1250);
            chanceInteracao = Mathf.Clamp01(chanceInteracao);
            duracaoAto = Math.Max(120, duracaoAto);
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
            listing.Label("MBL_ActDuration".Translate(settings.duracaoAto, (settings.duracaoAto / 60f).ToString("0.0")));
            settings.duracaoAto = (int)listing.Slider(settings.duracaoAto, 120f, 5000f);
            listing.End();
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

            Pawn partner = LovePartnerRelationUtility.ExistingLovePartner(pawn);
            if (!CanUsePartner(pawn, partner))
                return null;

            Thing spot = GenClosest.ClosestThingReachable(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForDef(DefDatabase<ThingDef>.GetNamed("InteracaoPrivada_Spot")),
                PathEndMode.Touch,
                TraverseParms.For(pawn),
                SearchRadius,
                delegate(Thing t)
                {
                    return t != null && t.Spawned && !t.IsForbidden(pawn) && pawn.CanReserve(t);
                });

            if (spot == null)
                return null;

            Job job = JobMaker.MakeJob(def.jobDef, partner, spot);
            job.count = 1;
            return job;
        }

        private static bool CanUsePartner(Pawn pawn, Pawn partner)
        {
            if (partner == null || partner == pawn || !partner.Spawned || partner.Map != pawn.Map)
                return false;
            if (partner.Drafted || partner.Dead || partner.Downed)
                return false;
            if (partner.InMentalState || partner.IsPrisoner != pawn.IsPrisoner)
                return false;
            if (partner.jobs == null || pawn.jobs == null)
                return false;
            if (!pawn.CanReach(partner, PathEndMode.Touch, Danger.Some))
                return false;
            return true;
        }
    }

    public class JobDriver_InteracaoPrivada : JobDriver
    {
        private const TargetIndex PartnerInd = TargetIndex.A;
        private const TargetIndex SpotInd = TargetIndex.B;

        private Pawn Partner
        {
            get { return job.GetTarget(PartnerInd).Thing as Pawn; }
        }

        private Thing Spot
        {
            get { return job.GetTarget(SpotInd).Thing; }
        }

        private bool IsLeader
        {
            get { return job.count != 0; }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (IsLeader && Partner != null)
                return pawn.Reserve(Partner, job, 1, -1, null, errorOnFailed);
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(PartnerInd);
            this.FailOnDespawnedOrNull(SpotInd);

            Toil syncPartner = new Toil();
            syncPartner.initAction = delegate
            {
                if (IsLeader)
                {
                    Pawn partner = Partner;
                    if (partner == null || !partner.Spawned || partner.Map != pawn.Map)
                    {
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }

                    Job counterpart = JobMaker.MakeJob(job.def, pawn, Spot);
                    counterpart.count = 0;
                    if (!CompatJobStarter.TryStartJob(partner, counterpart))
                    {
                        EndJobWith(JobCondition.Incompletable);
                    }
                }
            };
            syncPartner.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return syncPartner;

            yield return Toils_Goto.GotoThing(SpotInd, PathEndMode.Touch);

            Toil waitToSync = Toils_General.Wait(120);
            ToilFailConditions.FailOn<Toil>(waitToSync, new Func<bool>(delegate
            {
                Pawn p = Partner;
                return p == null || !p.Spawned || p.Map != pawn.Map || p.Dead;
            }));
            yield return waitToSync;

            int duration = LovinMod.settings != null ? LovinMod.settings.duracaoAto : 1250;
            Toil act = Toils_General.Wait(duration);
            act.socialMode = RandomSocialMode.Off;
            act.tickAction = delegate
            {
                if (pawn.IsHashIntervalTick(120) && pawn.Map != null)
                    FleckMaker.ThrowMetaIcon(pawn.Position, pawn.Map, FleckDefOf.Heart);
            };
            act.AddFinishAction(delegate
            {
                if (IsLeader)
                    FinishLovin();
            });
            yield return act;
        }

        private void FinishLovin()
        {
            Pawn partner = Partner;
            if (partner == null || pawn == null)
                return;

            TryGiveLovinMemory(pawn, partner);
            TryGiveLovinMemory(partner, pawn);
            CompatPregnancy.TryStartPregnancy(pawn, partner);
        }

        private static void TryGiveLovinMemory(Pawn who, Pawn other)
        {
            try
            {
                if (who.needs != null && who.needs.mood != null && who.needs.mood.thoughts != null && who.needs.mood.thoughts.memories != null)
                    who.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.GotSomeLovin, other);
            }
            catch (Exception ex)
            {
                Log.Warning("[Mrd Bedless Lovin 1.5] Could not apply GotSomeLovin thought to " + who + ": " + ex.Message);
            }
        }
    }

    internal static class CompatJobStarter
    {
        internal static bool TryStartJob(Pawn pawn, Job job)
        {
            if (pawn == null || pawn.jobs == null || job == null)
                return false;

            try
            {
                MethodInfo ordered = pawn.jobs.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "TryTakeOrderedJob")
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length >= 1 && p[0].ParameterType == typeof(Job);
                    });
                if (ordered != null)
                {
                    object result = ordered.Invoke(pawn.jobs, BuildArguments(ordered, job));
                    if (ordered.ReturnType == typeof(bool))
                        return result is bool && (bool)result;
                    return true;
                }

                MethodInfo start = pawn.jobs.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "StartJob")
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length >= 1 && p[0].ParameterType == typeof(Job);
                    });
                if (start != null)
                {
                    start.Invoke(pawn.jobs, BuildArguments(start, job));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Mrd Bedless Lovin 1.5] Could not start partner job: " + ex);
            }
            return false;
        }

        private static object[] BuildArguments(MethodInfo method, Job job)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (i == 0 && t == typeof(Job))
                    args[i] = job;
                else if (t == typeof(JobCondition))
                    args[i] = JobCondition.InterruptForced;
                else if (ps[i].HasDefaultValue)
                    args[i] = ps[i].DefaultValue;
                else if (t == typeof(bool))
                    args[i] = false;
                else if (t.IsValueType)
                    args[i] = Activator.CreateInstance(t);
                else
                    args[i] = null;
            }
            return args;
        }
    }

    internal static class CompatPregnancy
    {
        internal static void TryStartPregnancy(Pawn a, Pawn b)
        {
            if (!ModsConfig.BiotechActive || a == null || b == null || a.Dead || b.Dead)
                return;

            Pawn mother = a.gender == Gender.Female ? a : (b.gender == Gender.Female ? b : null);
            Pawn father = a.gender == Gender.Male ? a : (b.gender == Gender.Male ? b : null);
            if (mother == null || father == null || mother.health == null)
                return;
            if (mother.health.hediffSet != null && mother.health.hediffSet.HasHediff(HediffDefOf.PregnantHuman))
                return;

            try
            {
                float chance = GetPregnancyChance(mother, father);
                if (chance <= 0f || !Rand.Chance(chance))
                    return;

                Hediff hediff = HediffMaker.MakeHediff(HediffDefOf.PregnantHuman, mother);
                if (hediff == null)
                    return;

                TrySetParents(hediff, mother, father);
                mother.health.AddHediff(hediff);

                if (Prefs.DevMode)
                    Messages.Message("MBL_DevPregnancyStarted".Translate(), mother, MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception ex)
            {
                Log.Warning("[Mrd Bedless Lovin 1.5] Pregnancy compatibility path failed: " + ex);
            }
        }

        private static float GetPregnancyChance(Pawn mother, Pawn father)
        {
            MethodInfo method = typeof(PregnancyUtility).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "PregnancyChanceForPartners" && m.ReturnType == typeof(float));
            if (method == null)
                return 0f;

            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            int pawnIndex = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(Pawn))
                    args[i] = pawnIndex++ == 0 ? mother : father;
                else if (ps[i].HasDefaultValue)
                    args[i] = ps[i].DefaultValue;
                else if (ps[i].ParameterType.IsValueType)
                    args[i] = Activator.CreateInstance(ps[i].ParameterType);
                else
                    args[i] = null;
            }
            object value = method.Invoke(null, args);
            return value is float ? (float)value : 0f;
        }

        private static void TrySetParents(Hediff hediff, Pawn mother, Pawn father)
        {
            MethodInfo setParents = hediff.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SetParents");
            if (setParents == null)
                return;

            ParameterInfo[] ps = setParents.GetParameters();
            object[] args = new object[ps.Length];
            int pawnIndex = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (t == typeof(Pawn))
                    args[i] = pawnIndex++ == 0 ? mother : father;
                else
                    args[i] = TryBuildInheritedGenes(t, mother, father);
            }
            setParents.Invoke(hediff, args);
        }

        private static object TryBuildInheritedGenes(Type requestedType, Pawn mother, Pawn father)
        {
            MethodInfo[] methods = typeof(PregnancyUtility).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "GetInheritedGeneSet").ToArray();
            foreach (MethodInfo method in methods)
            {
                if (!requestedType.IsAssignableFrom(method.ReturnType))
                    continue;
                ParameterInfo[] ps = method.GetParameters();
                object[] args = new object[ps.Length];
                int pawnIndex = 0;
                bool usable = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(Pawn))
                        args[i] = pawnIndex++ == 0 ? mother : father;
                    else if (ps[i].HasDefaultValue)
                        args[i] = ps[i].DefaultValue;
                    else if (ps[i].ParameterType.IsValueType)
                        args[i] = Activator.CreateInstance(ps[i].ParameterType);
                    else
                        args[i] = null;
                }
                if (!usable)
                    continue;
                try { return method.Invoke(null, args); }
                catch { }
            }
            return requestedType.IsValueType ? Activator.CreateInstance(requestedType) : null;
        }
    }
}
