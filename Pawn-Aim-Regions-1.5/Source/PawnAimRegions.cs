using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PawnAimRegions15
{
    internal enum AimRegion
    {
        Head = 0,
        Torso = 1,
        LowerBody = 2
    }

    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                new Harmony("allen.pawnaimregions.1.5").PatchAll();
                Log.Message("[Pawn Aim Regions 1.5] loaded");
            }
            catch (Exception e)
            {
                Log.Error("[Pawn Aim Regions 1.5] startup failed: " + e);
            }
        }
    }

    public class AimRegionGameComponent : GameComponent
    {
        internal static AimRegionGameComponent Instance;

        private Dictionary<int, int> modes = new Dictionary<int, int>();
        private Dictionary<int, bool> playerManaged = new Dictionary<int, bool>();

        public AimRegionGameComponent(Game game)
        {
            Instance = this;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref modes, "PAR_modes", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref playerManaged, "PAR_playerManaged", LookMode.Value, LookMode.Value);
            if (modes == null) modes = new Dictionary<int, int>();
            if (playerManaged == null) playerManaged = new Dictionary<int, bool>();
        }

        internal static AimRegion GetMode(Pawn pawn)
        {
            if (pawn == null) return AimRegion.Torso;
            if (Instance == null) return IsPlayerPawn(pawn) ? AimRegion.Torso : RollNpcMode();
            return Instance.GetOrAssign(pawn);
        }

        internal static void SetPlayerMode(Pawn pawn, AimRegion mode)
        {
            if (pawn == null || Instance == null) return;
            int id = pawn.thingIDNumber;
            Instance.modes[id] = (int)mode;
            Instance.playerManaged[id] = true;
        }

        private AimRegion GetOrAssign(Pawn pawn)
        {
            int id = pawn.thingIDNumber;
            bool isPlayer = IsPlayerPawn(pawn);
            bool wasPlayer = playerManaged.TryGetValue(id, out var priorPlayer) && priorPlayer;

            if (isPlayer)
            {
                if (!modes.ContainsKey(id) || !wasPlayer)
                {
                    modes[id] = (int)AimRegion.Torso;
                    playerManaged[id] = true;
                }
            }
            else
            {
                if (!modes.ContainsKey(id) || wasPlayer)
                {
                    modes[id] = (int)RollNpcMode();
                    playerManaged[id] = false;
                }
            }

            int raw = modes[id];
            if (raw < (int)AimRegion.Head || raw > (int)AimRegion.LowerBody)
            {
                raw = isPlayer ? (int)AimRegion.Torso : (int)RollNpcMode();
                modes[id] = raw;
            }
            return (AimRegion)raw;
        }

        private static bool IsPlayerPawn(Pawn pawn)
        {
            return pawn?.RaceProps != null && pawn.RaceProps.Humanlike && pawn.Faction == Faction.OfPlayer;
        }

        private static AimRegion RollNpcMode()
        {
            float r = Rand.Value;
            if (r < 0.35f) return AimRegion.Head;
            if (r < 0.80f) return AimRegion.Torso;
            return AimRegion.LowerBody;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class PawnGizmoPatch
    {
        private static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__result == null || !ShouldShow(__instance)) return;
            __result = AppendAimGizmo(__result, __instance);
        }

        private static bool ShouldShow(Pawn pawn)
        {
            return pawn != null && !pawn.Dead && pawn.RaceProps != null && pawn.RaceProps.Humanlike && pawn.Faction == Faction.OfPlayer;
        }

        private static IEnumerable<Gizmo> AppendAimGizmo(IEnumerable<Gizmo> original, Pawn pawn)
        {
            foreach (var gizmo in original)
                yield return gizmo;

            AimRegion mode = AimRegionGameComponent.GetMode(pawn);
            var command = new Command_Action
            {
                defaultLabel = "PAR_AimRegionLabel".Translate(ModeLabel(mode)).ToString(),
                defaultDesc = "PAR_AimRegionDesc".Translate().ToString(),
                icon = TexCommand.Attack,
                groupable = false,
                action = delegate
                {
                    var options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("PAR_Head".Translate().ToString(), delegate { AimRegionGameComponent.SetPlayerMode(pawn, AimRegion.Head); }),
                        new FloatMenuOption("PAR_Torso".Translate().ToString(), delegate { AimRegionGameComponent.SetPlayerMode(pawn, AimRegion.Torso); }),
                        new FloatMenuOption("PAR_LowerBody".Translate().ToString(), delegate { AimRegionGameComponent.SetPlayerMode(pawn, AimRegion.LowerBody); })
                    };
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
            yield return command;
        }

        private static string ModeLabel(AimRegion mode)
        {
            switch (mode)
            {
                case AimRegion.Head: return "PAR_Head".Translate().ToString();
                case AimRegion.LowerBody: return "PAR_LowerBody".Translate().ToString();
                default: return "PAR_Torso".Translate().ToString();
            }
        }
    }

    [HarmonyPatch(typeof(DamageWorker_AddInjury), "ChooseHitPart")]
    internal static class ChooseHitPartPatch
    {
        private const float PreferredRegionWeight = 3f;

        private static bool Prefix(DamageInfo dinfo, Pawn pawn, ref BodyPartRecord __result)
        {
            try
            {
                if (pawn?.health?.hediffSet == null) return true;
                if (!(dinfo.Instigator is Pawn attacker)) return true;
                if (attacker == pawn || attacker.RaceProps == null || !attacker.RaceProps.Humanlike) return true;
                if (!attacker.HostileTo(pawn)) return true;

                // Respect systems that already explicitly constrain body height / hit part.
                if (dinfo.Height != BodyPartHeight.Undefined) return true;

                AimRegion region = AimRegionGameComponent.GetMode(attacker);
                if (!TryChoosePart(pawn, dinfo, region, out var chosen)) return true;

                __result = chosen;
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Pawn Aim Regions 1.5] hit-part selection failed: " + e, 0x50415215);
                return true;
            }
        }

        private static bool TryChoosePart(Pawn target, DamageInfo dinfo, AimRegion region, out BodyPartRecord chosen)
        {
            chosen = null;
            var all = target.health.hediffSet.GetNotMissingParts()
                .Where(p => p != null && p.coverageAbs > 0f &&
                            (dinfo.Depth == BodyPartDepth.Undefined || p.depth == dinfo.Depth))
                .ToList();
            if (all.Count == 0) return false;

            // Normal humanlike bodies use semantic body-part groups. Modded bodies that do not
            // expose those groups fall back to RimWorld's Top/Middle/Bottom heights.
            bool hasSemanticRegion = all.Any(p => IsPreferredPart(p, region));
            BodyPartHeight fallbackHeight = region == AimRegion.Head
                ? BodyPartHeight.Top
                : region == AimRegion.LowerBody ? BodyPartHeight.Bottom : BodyPartHeight.Middle;

            // Keep every normally valid body part in the lottery. The selected region only gets
            // 3x its vanilla hit weight; it is NOT guaranteed to be hit.
            if (all.TryRandomElementByWeight(
                    p => VanillaWeight(p, dinfo) *
                         (IsInAimedRegion(p, region, hasSemanticRegion, fallbackHeight) ? PreferredRegionWeight : 1f),
                    out chosen))
                return true;

            // Defensive fallback if a modded DamageDef returns zero hit chance for every part.
            return all.TryRandomElementByWeight(
                p => p.coverageAbs *
                     (IsInAimedRegion(p, region, hasSemanticRegion, fallbackHeight) ? PreferredRegionWeight : 1f),
                out chosen);
        }

        private static float VanillaWeight(BodyPartRecord part, DamageInfo dinfo)
        {
            return part.coverageAbs * part.def.GetHitChanceFactorFor(dinfo.Def);
        }

        private static bool IsInAimedRegion(BodyPartRecord part, AimRegion region, bool hasSemanticRegion, BodyPartHeight fallbackHeight)
        {
            return hasSemanticRegion ? IsPreferredPart(part, region) : part.height == fallbackHeight;
        }

        private static bool IsPreferredPart(BodyPartRecord part, AimRegion region)
        {
            switch (region)
            {
                case AimRegion.Head:
                    return HasGroupInSelfOrAncestors(part, BodyPartGroupDefOf.FullHead);

                case AimRegion.Torso:
                    return HasGroupInSelfOrAncestors(part, BodyPartGroupDefOf.Torso)
                           && !HasGroupInSelfOrAncestors(part, BodyPartGroupDefOf.FullHead)
                           && !IsLowerBody(part);

                case AimRegion.LowerBody:
                    return IsLowerBody(part);

                default:
                    return false;
            }
        }

        private static bool IsLowerBody(BodyPartRecord part)
        {
            for (BodyPartRecord p = part; p != null; p = p.parent)
            {
                if (HasDirectGroup(p, BodyPartGroupDefOf.Legs)) return true;
                string defName = p.def?.defName;
                if (defName == "Pelvis" || defName == "Waist") return true;
            }
            return false;
        }

        private static bool HasGroupInSelfOrAncestors(BodyPartRecord part, BodyPartGroupDef group)
        {
            if (group == null) return false;
            for (BodyPartRecord p = part; p != null; p = p.parent)
                if (HasDirectGroup(p, group)) return true;
            return false;
        }

        private static bool HasDirectGroup(BodyPartRecord part, BodyPartGroupDef group)
        {
            return part?.groups != null && group != null && part.groups.Contains(group);
        }
    }
}
