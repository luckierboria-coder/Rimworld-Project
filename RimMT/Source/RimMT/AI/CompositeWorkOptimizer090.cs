using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// V0.9.0 RC1 integration of the S5.3 candidate-source optimizers.
    /// This layer never creates Jobs, reserves targets, performs final reachability,
    /// or replaces the authoritative WorkGiver JobOnThing/JobOnCell result.
    /// It only removes candidates that are provably impossible under reviewed semantics.
    /// Every feature fails closed independently when a foreign authority patch is not reviewed.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class CompositeWorkOptimizer090
    {
        internal const int ParityMask = 31;

        private static readonly WorkOptimizerFeature BuildRoof = new WorkOptimizerFeature("BuildRoof candidate pruning");
        private static readonly WorkOptimizerFeature Tend = new WorkOptimizerFeature("Tend map-state gate");
        private static readonly WorkOptimizerFeature Harvest = new WorkOptimizerFeature("GrowerHarvest candidate pruning");
        private static readonly WorkOptimizerFeature ClearSnow = new WorkOptimizerFeature("ClearSnow candidate pruning");
        private static readonly WorkOptimizerFeature Sow = new WorkOptimizerFeature("GrowerSow candidate/index restructuring");
        private static readonly WorkOptimizerFeature DoBill = new WorkOptimizerFeature("DoBill BillGiver index");

        private static readonly ConditionalWeakTable<Map, SowMapCache> SowCaches = new ConditionalWeakTable<Map, SowMapCache>();
        private static readonly ConditionalWeakTable<Map, TendMapCache> TendCaches = new ConditionalWeakTable<Map, TendMapCache>();
        private static readonly ConditionalWeakTable<Map, BillMapCache> BillCaches = new ConditionalWeakTable<Map, BillMapCache>();
        private static readonly Thing[] EmptyThings = new Thing[0];

        private static Harmony harmony;
        private static bool installed;

        static CompositeWorkOptimizer090()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                harmony = new Harmony(RimMTBootstrap.HarmonyId);
                InstallReportHook();
                InstallBuildRoof();
                InstallTend();
                InstallGrowers();
                InstallClearSnow();
                InstallDoBill();
                InstallSharedThingSource();
                Log.Message("[RimMT] V0.9.0 RC1 composite Work optimizer installed. S5.3 candidate pruning is now part of the main RimMT assembly; features fail closed independently on unreviewed authority patches.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] V0.9.0 RC1 composite Work optimizer install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InstallReportHook()
        {
            MethodInfo report = AccessTools.Method(typeof(RimMTDiagnostics), "LogRuntimeReport");
            if (report != null)
                harmony.Patch(report, postfix: new HarmonyMethod(typeof(CompositeWorkOptimizer090), nameof(RuntimeReportPostfix)) { priority = Priority.Last });
        }

        private static void InstallBuildRoof()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            string blocker;
            if (source == null || authority == null) { BuildRoof.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(authority, null, out blocker)) { BuildRoof.Disable("foreign HasJobOnCell: " + blocker); return; }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkOptimizer090), nameof(BuildRoofCellsPostfix)) { priority = Priority.Last });
            BuildRoof.Enable("active");
        }

        private static void InstallTend()
        {
            MethodInfo normal = AccessTools.Method(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            MethodInfo urgent = AccessTools.Method(typeof(WorkGiver_TendOtherUrgent), nameof(WorkGiver_TendOtherUrgent.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            string blocker;
            if (normal == null || urgent == null) { Tend.Disable("target lookup failed"); return; }
            // Smart Medicine changes Tend authority. Until its exact transpiler semantics are source-reviewed,
            // RC1 deliberately stays fail-closed rather than assuming Vanilla HealthAIUtility is a complete gate.
            if (HasUnsafeForeignPatch(normal, null, out blocker) || HasUnsafeForeignPatch(urgent, null, out blocker))
            {
                Tend.Disable("foreign Tend authority: " + blocker);
                return;
            }
            Tend.Enable("active");
        }

        private static void InstallGrowers()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo harvestAuthority = AccessTools.Method(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            MethodInfo sowAuthority = AccessTools.Method(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            MethodInfo wanted = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.CalculateWantedPlantDef), new Type[] { typeof(IntVec3), typeof(Map) });
            string blocker;
            if (source == null) { Harvest.Disable("source lookup failed"); Sow.Disable("source lookup failed"); return; }

            if (harvestAuthority == null)
                Harvest.Disable("authority lookup failed");
            else if (HasUnsafeForeignPatch(harvestAuthority, IsKnownSafeHarvestPatch, out blocker))
                Harvest.Disable("foreign Harvest authority: " + blocker);
            else
                Harvest.Enable("active");

            if (sowAuthority == null || wanted == null)
                Sow.Disable("authority/index lookup failed");
            else if (HasUnsafeForeignPatch(sowAuthority, IsReviewedRestrictedSowPatch, out blocker))
                Sow.Disable("foreign Sow authority: " + blocker);
            else if (HasUnsafeForeignPatch(wanted, null, out blocker))
                Sow.Disable("foreign wanted-plant index patch: " + blocker);
            else
                Sow.Enable(HasReviewedRestrictedSowPatch(sowAuthority) ? "restricted reviewed Biomes/ReGrowth coexistence" : "active");

            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkOptimizer090), nameof(GrowerCellsPostfix)) { priority = Priority.Last });
        }

        private static void InstallClearSnow()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_ClearSnow), nameof(WorkGiver_ClearSnow.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_ClearSnow), nameof(WorkGiver_ClearSnow.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            string blocker;
            if (source == null || authority == null) { ClearSnow.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(authority, null, out blocker)) { ClearSnow.Disable("foreign HasJobOnCell: " + blocker); return; }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkOptimizer090), nameof(ClearSnowCellsPostfix)) { priority = Priority.Last });
            ClearSnow.Enable("active");
        }

        private static void InstallDoBill()
        {
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.JobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            MethodInfo baseHas = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            string blocker;
            if (authority == null || baseHas == null) { DoBill.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(authority, null, out blocker) || HasUnsafeForeignPatch(baseHas, null, out blocker))
            {
                DoBill.Disable("foreign DoBill authority: " + blocker);
                return;
            }
            DoBill.Enable("active");
        }

        private static void InstallSharedThingSource()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
            if (source == null)
            {
                if (Tend.Enabled) Tend.Disable("shared source lookup failed");
                if (DoBill.Enabled) DoBill.Disable("shared source lookup failed");
                return;
            }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeWorkOptimizer090), nameof(PotentialWorkThingsGlobalPostfix)) { priority = Priority.Last });
        }

        public static void BuildRoofCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (!BuildRoof.Enabled || pawn == null || pawn.Map == null || __result == null || BuildRoof.ShouldParityBypass()) return;
            __result = FilterBuildRoof(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterBuildRoof(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 c in source)
            {
                BuildRoof.Seen();
                if (!c.Roofed(map)) { BuildRoof.Kept(); yield return c; }
                else BuildRoof.Pruned();
            }
        }

        public static void GrowerCellsPostfix(WorkGiver_Grower __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (__instance == null || pawn == null || pawn.Map == null || __result == null) return;
            if (__instance.GetType() == typeof(WorkGiver_GrowerHarvest) && Harvest.Enabled)
            {
                if (!Harvest.ShouldParityBypass()) __result = FilterHarvest(__result, pawn.Map);
                return;
            }
            if (__instance.GetType() == typeof(WorkGiver_GrowerSow) && Sow.Enabled && !Sow.ShouldParityBypass())
                __result = FilterSow(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterHarvest(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 c in source)
            {
                Harvest.Seen();
                Plant plant = c.GetPlant(map);
                if (plant != null && plant.HarvestableNow && plant.LifeStage == PlantLifeStage.Mature && plant.CanYieldNow())
                {
                    Harvest.Kept();
                    yield return c;
                }
                else Harvest.Pruned();
            }
        }

        private static IEnumerable<IntVec3> FilterSow(IEnumerable<IntVec3> source, Map map)
        {
            SowMapCache cache = SowCaches.GetValue(map, delegate(Map m) { return new SowMapCache(); });
            cache.Prepare(CurrentTick());
            foreach (IntVec3 c in source)
            {
                Sow.Seen();
                ThingDef wanted;
                if (!cache.Wanted.TryGetValue(c, out wanted))
                {
                    wanted = WorkGiver_Grower.CalculateWantedPlantDef(c, map);
                    cache.Wanted[c] = wanted;
                    Sow.IndexBuild();
                }
                if (wanted == null) { Sow.Pruned(); continue; }

                List<Thing> things = c.GetThingList(map);
                bool samePlantPresent = false;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing != null && thing.def == wanted) { samePlantPresent = true; break; }
                }
                if (samePlantPresent) { Sow.Pruned(); continue; }
                Sow.Kept();
                yield return c;
            }
        }

        public static void ClearSnowCellsPostfix(Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (!ClearSnow.Enabled || pawn == null || pawn.Map == null || __result == null || ClearSnow.ShouldParityBypass()) return;
            __result = FilterSnow(__result, pawn.Map);
        }

        private static IEnumerable<IntVec3> FilterSnow(IEnumerable<IntVec3> source, Map map)
        {
            foreach (IntVec3 c in source)
            {
                ClearSnow.Seen();
                if (map.snowGrid.GetDepth(c) >= 0.2f) { ClearSnow.Kept(); yield return c; }
                else ClearSnow.Pruned();
            }
        }

        public static void PotentialWorkThingsGlobalPostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (__instance == null || pawn == null || pawn.Map == null || __result != null) return;

            WorkGiver_Tend tendGiver = __instance as WorkGiver_Tend;
            string typeName = __instance.GetType().Name;
            if (Tend.Enabled && tendGiver != null && typeName.IndexOf("TendOther", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!Tend.ShouldParityBypass())
                {
                    TendMapCache cache = TendCaches.GetValue(pawn.Map, delegate(Map m) { return new TendMapCache(); });
                    cache.RefreshIfNeeded(pawn.Map);
                    bool urgent = __instance is WorkGiver_TendOtherUrgent || typeName.IndexOf("Urgent", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool humanlikeOnly = typeName.IndexOf("Humanlike", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool animalOnly = typeName.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool hasTarget = humanlikeOnly ? (urgent ? cache.UrgentHumanlike : cache.AnyHumanlike)
                        : animalOnly ? (urgent ? cache.UrgentAnimal : cache.AnyAnimal)
                        : (urgent ? cache.UrgentAny : cache.Any);
                    Tend.GateCheck();
                    if (!hasTarget) { Tend.GateHit(); __result = EmptyThings; }
                }
                return;
            }

            WorkGiver_DoBill billWork = __instance as WorkGiver_DoBill;
            if (!DoBill.Enabled || billWork == null || DoBill.ShouldParityBypass()) return;
            BillMapCache billCache = BillCaches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
            __result = billCache.Get(billWork, pawn.Map);
        }

        private static bool IsKnownSafeHarvestPatch(Patch patch)
        {
            if (patch == null || patch.PatchMethod == null) return false;
            string typeName = patch.PatchMethod.DeclaringType == null ? string.Empty : patch.PatchMethod.DeclaringType.FullName;
            string methodName = patch.PatchMethod.Name ?? string.Empty;
            return methodName.IndexOf("HasJobOnCellHarvestPostfix", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (typeName.IndexOf("AlienRace", StringComparison.OrdinalIgnoreCase) >= 0 && methodName.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsReviewedRestrictedSowPatch(Patch patch)
        {
            if (patch == null || patch.PatchMethod == null || patch.PatchMethod.DeclaringType == null) return false;
            MethodInfo method = patch.PatchMethod;
            if (string.Equals(patch.owner, "rimworld.biomes.core", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(method.DeclaringType.FullName, "BiomesCore.Patches.Plants.WorkGiver_GrowerSow_JobOnCell_Patch", StringComparison.Ordinal) &&
                string.Equals(method.Name, "Prefix", StringComparison.Ordinal)) return true;
            if (string.Equals(patch.owner, "Helixien.ReGrowthCore", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(method.DeclaringType.FullName, "ReGrowthCore.PlantExpandable+WorkGiver_GrowerSow_JobOnCell_Patch", StringComparison.Ordinal) &&
                (string.Equals(method.Name, "Prefix", StringComparison.Ordinal) || string.Equals(method.Name, "Postfix", StringComparison.Ordinal))) return true;
            return false;
        }

        private static bool HasReviewedRestrictedSowPatch(MethodBase target)
        {
            Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return AnyReviewed(info.Prefixes) || AnyReviewed(info.Postfixes) || AnyReviewed(info.Transpilers) || AnyReviewed(info.Finalizers);
        }

        private static bool AnyReviewed(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++) if (IsReviewedRestrictedSowPatch(patches[i])) return true;
            return false;
        }

        private static bool HasUnsafeForeignPatch(MethodBase target, Func<Patch, bool> safeForeign, out string blocker)
        {
            blocker = null;
            Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return Check(info.Prefixes, "prefix", safeForeign, out blocker) || Check(info.Postfixes, "postfix", safeForeign, out blocker) ||
                   Check(info.Transpilers, "transpiler", safeForeign, out blocker) || Check(info.Finalizers, "finalizer", safeForeign, out blocker);
        }

        private static bool Check(IList<Patch> patches, string kind, Func<Patch, bool> safeForeign, out string blocker)
        {
            blocker = null;
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null || string.Equals(p.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                if (safeForeign != null && safeForeign(p)) continue;
                MethodInfo mi = p.PatchMethod;
                blocker = kind + " " + (p.owner ?? "<unknown-owner>") + " :: " +
                    (mi == null || mi.DeclaringType == null ? "<unknown-method>" : mi.DeclaringType.FullName + "." + mi.Name);
                return true;
            }
            return false;
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        public static void RuntimeReportPostfix()
        {
            Log.Message("[RimMT] V0.9.0 RC1 Work optimizer report: " + BuildRoof.Summary() + " | " + Tend.Summary() + " | " + Harvest.Summary() + " | " + ClearSnow.Summary() + " | " + Sow.Summary() + " | " + DoBill.Summary());
        }

        private sealed class SowMapCache
        {
            private int tick = int.MinValue;
            internal readonly Dictionary<IntVec3, ThingDef> Wanted = new Dictionary<IntVec3, ThingDef>();
            internal void Prepare(int currentTick)
            {
                if (tick == currentTick) return;
                tick = currentTick;
                Wanted.Clear();
            }
        }

        private sealed class TendMapCache
        {
            private int tick = int.MinValue;
            internal bool Any, AnyHumanlike, AnyAnimal, UrgentAny, UrgentHumanlike, UrgentAnimal;
            internal void RefreshIfNeeded(Map map)
            {
                int now = CurrentTick();
                if (tick == now) return;
                tick = now;
                Any = AnyHumanlike = AnyAnimal = UrgentAny = UrgentHumanlike = UrgentAnimal = false;
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn patient = pawns[i];
                    if (patient == null || patient.Dead) continue;
                    bool normal, urgent;
                    try
                    {
                        normal = HealthAIUtility.ShouldBeTendedNowByPlayer(patient);
                        urgent = normal && HealthAIUtility.ShouldBeTendedNowByPlayerUrgent(patient);
                    }
                    catch { continue; }
                    if (!normal) continue;
                    Any = true;
                    if (patient.RaceProps != null && patient.RaceProps.Humanlike) AnyHumanlike = true;
                    if (patient.RaceProps != null && patient.RaceProps.Animal) AnyAnimal = true;
                    if (!urgent) continue;
                    UrgentAny = true;
                    if (patient.RaceProps != null && patient.RaceProps.Humanlike) UrgentHumanlike = true;
                    if (patient.RaceProps != null && patient.RaceProps.Animal) UrgentAnimal = true;
                }
            }
        }

        private sealed class BillMapCache
        {
            private int tick = int.MinValue;
            private readonly Dictionary<WorkGiverDef, List<Thing>> byDef = new Dictionary<WorkGiverDef, List<Thing>>();
            internal IEnumerable<Thing> Get(WorkGiver_DoBill giver, Map map)
            {
                int now = CurrentTick();
                if (tick != now) { tick = now; byDef.Clear(); }
                List<Thing> cached;
                if (byDef.TryGetValue(giver.def, out cached)) { DoBill.IndexHit(); return cached; }

                DoBill.IndexBuild();
                cached = new List<Thing>();
                IEnumerable<Thing> source = map.listerThings.ThingsMatching(giver.PotentialWorkThingRequest);
                if (source != null)
                {
                    foreach (Thing thing in source)
                    {
                        DoBill.Seen();
                        IBillGiver billGiver = thing as IBillGiver;
                        if (billGiver == null || !giver.ThingIsUsableBillGiver(thing) || billGiver.BillStack == null || !billGiver.BillStack.AnyShouldDoNow)
                        {
                            DoBill.Pruned();
                            continue;
                        }
                        DoBill.Kept();
                        cached.Add(thing);
                    }
                }
                byDef[giver.def] = cached;
                return cached;
            }
        }
    }
}
