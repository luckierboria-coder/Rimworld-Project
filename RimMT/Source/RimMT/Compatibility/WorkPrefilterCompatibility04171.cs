using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    // V0.4.17.1 compatibility shim for the first real 390-mod playtest.
    //
    // The V0.4.17 core intentionally fails closed when another Harmony owner touches an
    // authoritative WorkGiver method. This shim only re-enables two source-reviewed cases:
    //
    //  * Humanoid Alien Races harvest Postfix is a downstream true->false restriction. It
    //    cannot turn a RimMT hard-negative false into true, so exact coexistence is safe.
    //  * Biomes! Core GrowerSow Prefix changes GrowthSeasonNow semantics by temporarily
    //    installing the wanted plant in shared static context. Workers must not reproduce
    //    that shared-static protocol. In Biomes mode RimMT may still use other Sow negatives,
    //    but any prediction containing OutOfGrowthSeason is forced back to live Vanilla.
    //
    // Watchtowers BuildRoof remains fail-closed until its Postfix semantics are source-reviewed.
    [StaticConstructorOnStartup]
    internal static class WorkPrefilterCompatibility04171
    {
        private const int OutOfGrowthSeasonBit = 1 << 2;

        private static readonly FieldInfo SowCompatibleField =
            AccessTools.Field(typeof(ParallelWorkPrefilter), "sowCompatible");
        private static readonly FieldInfo HarvestCompatibleField =
            AccessTools.Field(typeof(ParallelWorkPrefilter), "harvestCompatible");

        private static readonly Type DecisionType =
            typeof(ParallelWorkPrefilter).GetNestedType("FastNegativeDecision", BindingFlags.NonPublic);
        private static readonly FieldInfo DecisionStateField = DecisionType == null
            ? null
            : DecisionType.GetField("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type SampleStateType =
            typeof(ParallelWorkPrefilter).GetNestedType("SampleState", BindingFlags.NonPublic);
        private static readonly FieldInfo StateReasonField = SampleStateType == null
            ? null
            : SampleStateType.GetField("Reason", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static volatile bool biomesSowRestricted;
        private static volatile bool alienHarvestCoexists;
        private static long biomesSeasonVanillaFallbacks;
        private static long shimFaults;

        static WorkPrefilterCompatibility04171()
        {
            try
            {
                MethodInfo markReady = AccessTools.Method(typeof(ParallelWorkPrefilter), "MarkCompatibilityReady");
                MethodInfo tryFastNegative = AccessTools.Method(typeof(ParallelWorkPrefilter), "TryFastNegative");
                if (markReady == null || tryFastNegative == null || SowCompatibleField == null ||
                    HarvestCompatibleField == null || DecisionStateField == null || StateReasonField == null)
                {
                    Log.Warning("[RimMT] V0.4.17.1 Work compatibility shim unavailable: private Work-prefilter reflection contract was not found. V0.4.17 fail-closed behavior remains in force.");
                    return;
                }

                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);

                HarmonyMethod readyPostfix = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04171), nameof(CompatibilityReadyPostfix));
                readyPostfix.priority = Priority.Last;
                harmony.Patch(markReady, postfix: readyPostfix);

                HarmonyMethod fastNegativePostfix = new HarmonyMethod(
                    typeof(WorkPrefilterCompatibility04171), nameof(TryFastNegativePostfix));
                fastNegativePostfix.priority = Priority.Last;
                harmony.Patch(tryFastNegative, postfix: fastNegativePostfix);

                Log.Message("[RimMT] V0.4.17.1 Work compatibility shim installed. AlienRace harvest may coexist only through its exact downstream restriction Postfix; Biomes Core sow may coexist only in restricted mode where worker OutOfGrowthSeason negatives always fall back to Vanilla. Watchtowers roof remains blocked.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] V0.4.17.1 Work compatibility shim failed to install; V0.4.17 fail-closed behavior remains in force. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void CompatibilityReadyPostfix()
        {
            try
            {
                MethodBase sowTarget = AccessTools.Method(
                    typeof(WorkGiver_GrowerSow),
                    nameof(WorkGiver_GrowerSow.JobOnCell),
                    new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
                MethodBase harvestTarget = AccessTools.Method(
                    typeof(WorkGiver_GrowerHarvest),
                    nameof(WorkGiver_GrowerHarvest.HasJobOnCell),
                    new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
                MethodBase baseHasJobTarget = AccessTools.Method(
                    typeof(WorkGiver_Scanner),
                    nameof(WorkGiver_Scanner.HasJobOnCell),
                    new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });

                bool sawBiomes;
                string sowBlocker;
                bool sowTargetSafe = OnlyAllowedForeignPatches(
                    sowTarget,
                    IsExactBiomesSowPrefix,
                    out sawBiomes,
                    out sowBlocker);

                bool ignoredAllowed;
                string baseBlocker;
                bool baseSafe = OnlyAllowedForeignPatches(
                    baseHasJobTarget,
                    NeverAllowForeign,
                    out ignoredAllowed,
                    out baseBlocker);

                if (sowTargetSafe && baseSafe && sawBiomes)
                {
                    SowCompatibleField.SetValue(null, 1);
                    biomesSowRestricted = true;
                    Log.Message("[RimMT] V0.4.17.1 enables GrowerSow prefilter in Biomes-restricted mode. Exact Biomes Core Prefix detected; worker predictions carrying OutOfGrowthSeason are never authoritative and fall back to live Vanilla JobOnCell.");
                }
                else if (sawBiomes && (!sowTargetSafe || !baseSafe))
                {
                    Log.Warning("[RimMT] V0.4.17.1 keeps GrowerSow fail-closed because an additional foreign patch is present. blocker=" +
                        (!sowTargetSafe ? sowBlocker : baseBlocker));
                }

                bool sawAlienRace;
                string harvestBlocker;
                bool harvestSafe = OnlyAllowedForeignPatches(
                    harvestTarget,
                    IsExactAlienRaceHarvestPostfix,
                    out sawAlienRace,
                    out harvestBlocker);

                if (harvestSafe && sawAlienRace)
                {
                    HarvestCompatibleField.SetValue(null, 1);
                    alienHarvestCoexists = true;
                    Log.Message("[RimMT] V0.4.17.1 enables GrowerHarvest prefilter with exact Humanoid Alien Races coexistence. AlienRace HasJobOnCellHarvestPostfix is retained as downstream true->false authority.");
                }
                else if (sawAlienRace && !harvestSafe)
                {
                    Log.Warning("[RimMT] V0.4.17.1 keeps GrowerHarvest fail-closed because an additional foreign patch is present. blocker=" + harvestBlocker);
                }

                Log.Message("[RimMT] V0.4.17.1 Work coexistence result: biomesSowRestricted=" + biomesSowRestricted +
                    ", alienHarvest=" + alienHarvestCoexists +
                    ", buildRoof remains governed by V0.4.17 fail-closed compatibility.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref shimFaults);
                Log.Warning("[RimMT] V0.4.17.1 Work coexistence evaluation failed; existing fail-closed decisions remain authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Harmony supplies the final argument array after the private original returns. The
        // fifth argument is the out FastNegativeDecision struct. We inspect it reflectively so
        // this shim does not expose or widen the V0.4.17 core's private nested types.
        private static void TryFastNegativePostfix(ref bool __result, object[] __args)
        {
            if (!__result || !biomesSowRestricted)
                return;

            try
            {
                if (__args == null || __args.Length < 5)
                {
                    __result = false;
                    Interlocked.Increment(ref shimFaults);
                    return;
                }

                object kind = __args[3];
                if (kind == null || !string.Equals(kind.ToString(), "Sow", StringComparison.Ordinal))
                    return;

                object decision = __args[4];
                if (decision == null)
                {
                    __result = false;
                    Interlocked.Increment(ref shimFaults);
                    return;
                }

                object state = DecisionStateField.GetValue(decision);
                if (state == null)
                {
                    __result = false;
                    Interlocked.Increment(ref shimFaults);
                    return;
                }

                object reason = StateReasonField.GetValue(state);
                if (reason == null)
                {
                    __result = false;
                    Interlocked.Increment(ref shimFaults);
                    return;
                }

                int reasonBits = Convert.ToInt32(reason);
                if ((reasonBits & OutOfGrowthSeasonBit) != 0)
                {
                    // Critical safety rule: Biomes' per-plant season context is main-thread
                    // shared state. Never let a worker-generated season negative bypass it.
                    __result = false;
                    Interlocked.Increment(ref biomesSeasonVanillaFallbacks);
                }
            }
            catch
            {
                // Failure is conservative: for a Sow call, any inability to inspect the
                // prediction means the caller must execute live Vanilla.
                if (__args != null && __args.Length > 3 && __args[3] != null &&
                    string.Equals(__args[3].ToString(), "Sow", StringComparison.Ordinal))
                    __result = false;
                Interlocked.Increment(ref shimFaults);
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

        private static bool IsExactBiomesSowPrefix(Patch patch, string kind)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            return string.Equals(kind, "prefix", StringComparison.Ordinal) &&
                   string.Equals(patch.owner, "rimworld.biomes.core", StringComparison.OrdinalIgnoreCase) &&
                   method != null && method.DeclaringType != null &&
                   string.Equals(method.DeclaringType.FullName,
                       "BiomesCore.Patches.Plants.WorkGiver_GrowerSow_JobOnCell_Patch",
                       StringComparison.Ordinal) &&
                   string.Equals(method.Name, "Prefix", StringComparison.Ordinal);
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

        private static bool NeverAllowForeign(Patch patch, string kind)
        {
            return false;
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
            return "Work prefilter compatibility V0.4.17.1: biomesSowRestricted=" + biomesSowRestricted +
                ", alienHarvest=" + alienHarvestCoexists +
                ", biomesSeasonVanillaFallbacks=" + Interlocked.Read(ref biomesSeasonVanillaFallbacks) +
                ", shimFaults=" + Interlocked.Read(ref shimFaults) +
                ". Watchtowers BuildRoof is intentionally not whitelisted.";
        }
    }
}
