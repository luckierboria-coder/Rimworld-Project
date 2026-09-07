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

        // Pawn thingID -> AimRegion integer.
        private Dictionary<int, int> modes = new Dictionary<int, int>();
        // Pawn thingID -> true when the pawn was last managed as a player pawn.
        // This lets newly recruited NPCs reset to the requested player default (Torso),
        // and former player pawns become NPC-random again if they later leave the player faction.
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
                // Colonists/player humanlikes always enter player control at the requested default: torso.
                if (!modes.ContainsKey(id) || !wasPlayer)
                {
                    modes[id] = (int)AimRegion.Torso;
                    playerManaged[id] = true;
                }
            }
            else
            {
                // NPCs get one persistent random choice. If a former player pawn becomes an NPC,
                // reroll once so it follows NPC rules rather than retaining a player order forever.
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
        private static bool Prefix(DamageInfo dinfo, Pawn pawn, ref BodyPartRecord __result)
        {
            try
            {
                if (pawn?.health?.hediffSet == null) return true;
                if (!(dinfo.Instigator is Pawn attacker)) return true;
                if (attacker == pawn || attacker.RaceProps == null || !attacker.RaceProps.Humanlike) return true;
                if (!attacker.HostileTo(pawn)) return true;

                // Respect another system that already deliberately constrained body height.
                // Explicit HitPart never reaches ChooseHitPart in vanilla, so it is inherently preserved too.
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

            var candidates = all.Where(p => IsPreferredPart(p, region)).ToList();

            // Modded humanlike bodies do not always use the vanilla FullHead/Torso/Legs groups.
            // If the semantic group does not exist, fall back to the engine's Top/Middle/Bottom height system.
            if (candidates.Count == 0)
            {
                BodyPartHeight fallbackHeight = region == AimRegion.Head
                    ? BodyPartHeight.Top
                    : region == AimRegion.LowerBody ? BodyPartHeight.Bottom : BodyPartHeight.Middle;
                candidates = all.Where(p => p.height == fallbackHeight).ToList();
            }

            if (candidates.Count == 0) return false;

            // Preserve vanilla weighting inside the chosen region.
            if (candidates.TryRandomElementByWeight(
                    p => p.coverageAbs * p.def.GetHitChanceFactorFor(dinfo.Def), out chosen))
                return true;

            return candidates.TryRandomElementByWeight(p => p.coverageAbs, out chosen);
        }

        private static bool IsPreferredPart(BodyPartRecord part, AimRegion region)
        {
            switch (region)
            {
                case AimRegion.Head:
                    return HasGroupInSelfOrAncestors(part, BodyPartGroupDefOf.FullHead);

                case AimRegion.Torso:
                    // Torso-tagged chest/internal organs, but pelvis belongs to the lower-body order.
                    return HasDirectGroup(part, BodyPartGroupDefOf.Torso) && !IsLowerBody(part);

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
