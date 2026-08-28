using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    // V0.4.17.1 originally combined AlienRace Harvest coexistence with a reflective
    // TryFastNegative(..., out FastNegativeDecision) Postfix used for Biomes Sow fallback.
    // The first loaded-save run exposed an impossible metric combination:
    // shadowSamples == authoritativeFalse while shadowMatches == parityMismatches == 0.
    //
    // That proves the out FastNegativeDecision state was not reaching the caller intact after
    // the object[] __args Harmony wrapper. V0.4.17.2 therefore removes that patch completely.
    // This legacy shim now does one job only: exact source-reviewed AlienRace Harvest
    // coexistence. It never patches TryFastNegative and never touches sampled parity state.
    // Sow compatibility is handled separately by WorkPrefilterCompatibility04172.
    [StaticConstructorOnStartup]
    internal static class WorkPrefilterCompatibility04171
    {
        private static readonly FieldInfo HarvestCompatibleField =
            AccessTools.Field(typeof(ParallelWorkPrefilter), "harvestCompatible");

        private static volatile bool alienHarvestCoexists;
        private static long shimFaults;

        static WorkPrefilterCompatibility04171()
        {
            try
            {
                MethodInfo markReady = AccessTools.Method(typeof(ParallelWorkPrefilter), "MarkCompatibilityReady");
                if (markReady == null || HarvestCompatibleField == null)
                {
                    Log.Warning("[RimMT] V0.4.17.2 Harvest compatibility layer unavailable: private Work-prefilter reflection contract was not found. V0.4.17 fail-closed behavior remains in force.");
                    return;
                }

                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                HarmonyMethod readyPostfix = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04171), nameof(CompatibilityReadyPostfix));
                readyPostfix.priority = Priority.Last;
                harmony.Patch(markReady, postfix: readyPostfix);

                Log.Message("[RimMT] V0.4.17.2 Harvest compatibility layer installed. Exact Humanoid Alien Races downstream restriction may coexist; no Harmony patch is installed on TryFastNegative, so native V0.4.17 sampled parity state remains untouched.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] V0.4.17.2 Harvest compatibility layer failed to install; V0.4.17 fail-closed behavior remains in force. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CompatibilityReadyPostfix()
        {
            try
            {
                MethodBase harvestTarget = AccessTools.Method(
                    typeof(WorkGiver_GrowerHarvest),
                    nameof(WorkGiver_GrowerHarvest.HasJobOnCell),
                    new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });

                bool sawAlienRace;
                string blocker;
                bool safe = OnlyAllowedForeignPatches(
                    harvestTarget,
                    IsExactAlienRaceHarvestPostfix,
                    out sawAlienRace,
                    out blocker);

                if (safe && sawAlienRace)
                {
                    HarvestCompatibleField.SetValue(null, 1);
                    alienHarvestCoexists = true;
                    Log.Message("[RimMT] V0.4.17.2 enables GrowerHarvest prefilter with exact Humanoid Alien Races coexistence. AlienRace HasJobOnCellHarvestPostfix remains downstream true->false authority; V0.4.17 warmup/sample parity executes unmodified.");
                }
                else if (sawAlienRace && !safe)
                {
                    Log.Warning("[RimMT] V0.4.17.2 keeps GrowerHarvest fail-closed because an additional foreign patch is present. blocker=" + blocker);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref shimFaults);
                Log.Warning("[RimMT] V0.4.17.2 Harvest coexistence evaluation failed; existing fail-closed decision remains authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private delegate bool AllowedPatch(Patch patch, string kind);

        private static bool OnlyAllowedForeignPatches(
            MethodBase target,
            AllowedPatch allowed,
            out bool sawAllowed,
            out string blocker)
        {
            sawAllowed = false;
            blocker = null;
            if (target == null)
            {
                blocker = "<missing-target>";
                return false;
            }

            Patches info = Harmony.GetPatchInfo(target);
            if (info == null)
                return true;

            if (!CheckPatchList(info.Prefixes, "prefix", allowed, ref sawAllowed, out blocker)) return false;
            if (!CheckPatchList(info.Postfixes, "postfix", allowed, ref sawAllowed, out blocker)) return false;
            if (!CheckPatchList(info.Transpilers, "transpiler", allowed, ref sawAllowed, out blocker)) return false;
            if (!CheckPatchList(info.Finalizers, "finalizer", allowed, ref sawAllowed, out blocker)) return false;
            return true;
        }

        private static bool CheckPatchList(
            IList<Patch> patches,
            string kind,
            AllowedPatch allowed,
            ref bool sawAllowed,
            out string blocker)
        {
            blocker = null;
            if (patches == null)
                return true;

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    continue;

                if (allowed != null && allowed(patch, kind))
                {
                    sawAllowed = true;
                    continue;
                }

                blocker = Describe(patch, kind);
                return false;
            }
            return true;
        }

        private static bool IsExactAlienRaceHarvestPostfix(Patch patch, string kind)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            return string.Equals(kind, "postfix", StringComparison.Ordinal) &&
                   string.Equals(patch.owner, "rimworld.erdelf.alien_race.main", StringComparison.OrdinalIgnoreCase) &&
                   method != null && method.DeclaringType != null &&
                   string.Equals(method.DeclaringType.FullName, "AlienRace.HarmonyPatches", StringComparison.Ordinal) &&
                   string.Equals(method.Name, "HasJobOnCellHarvestPostfix", StringComparison.Ordinal);
        }

        private static string Describe(Patch patch, string kind)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            return kind + " " + (patch == null ? "<null>" : patch.owner ?? "<unknown-owner>") + " :: " +
                (method == null || method.DeclaringType == null
                    ? "<unknown-method>"
                    : method.DeclaringType.FullName + "." + method.Name);
        }

        internal static string Summary()
        {
            return "Work prefilter Harvest compatibility V0.4.17.2: alienHarvest=" + alienHarvestCoexists +
                ", shimFaults=" + Interlocked.Read(ref shimFaults) +
                ". TryFastNegative is intentionally unpatched so sampled parity state cannot be rewritten by a compatibility wrapper.";
        }
    }
}
