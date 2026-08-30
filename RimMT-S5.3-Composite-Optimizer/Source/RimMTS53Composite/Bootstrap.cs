using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTS53Composite
{
    [StaticConstructorOnStartup]
    internal static partial class CompositeOptimizerS53
    {
        internal const string HarmonyId = "allen.rimmt";
        internal const int ParityMask = 31;

        internal static readonly FeatureState BuildRoof = new FeatureState("BuildRoof candidate pruning");
        internal static readonly FeatureState Tend = new FeatureState("Tend map-state gate");
        internal static readonly FeatureState Harvest = new FeatureState("GrowerHarvest candidate pruning");
        internal static readonly FeatureState ClearSnow = new FeatureState("ClearSnow candidate pruning");
        internal static readonly FeatureState Sow = new FeatureState("GrowerSow candidate/index restructuring");
        internal static readonly FeatureState DoBill = new FeatureState("DoBill BillGiver index");

        private static Harmony harmony;
        private static bool installed;

        static CompositeOptimizerS53()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                harmony = new Harmony(HarmonyId);
                InstallReportHook();
                InstallBuildRoof();
                InstallTend();
                InstallGrowers();
                InstallClearSnow();
                InstallDoBill();
                InstallSharedThingSource();
                Log.Message("[RimMT-S5.3] Composite optimizer installed with trusted owner=allen.rimmt. Six features are independent and fail closed on unsafe foreign authority patches.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT-S5.3] install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InstallReportHook()
        {
            Type t = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = t == null ? null : AccessTools.Method(t, "LogRuntimeReport");
            if (report != null)
                harmony.Patch(report, postfix: new HarmonyMethod(typeof(CompositeOptimizerS53), nameof(RuntimeReportPostfix)) { priority = Priority.Last });
        }

        private static void InstallBuildRoof()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            string blocker;
            if (source == null || authority == null) { BuildRoof.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(authority, null, out blocker)) { BuildRoof.Disable("foreign HasJobOnCell: " + blocker); return; }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeOptimizerS53), nameof(BuildRoofCellsPostfix)) { priority = Priority.Last });
            BuildRoof.Enable();
        }

        private static void InstallTend()
        {
            MethodInfo normal = AccessTools.Method(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            MethodInfo urgent = AccessTools.Method(typeof(WorkGiver_TendOtherUrgent), nameof(WorkGiver_TendOtherUrgent.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            string blocker;
            if (normal == null || urgent == null) { Tend.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(normal, null, out blocker) || HasUnsafeForeignPatch(urgent, null, out blocker)) { Tend.Disable("foreign Tend authority: " + blocker); return; }
            Tend.Enable();
        }

        private static void InstallGrowers()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo harvestAuthority = AccessTools.Method(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            MethodInfo sowAuthority = AccessTools.Method(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            MethodInfo wanted = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.CalculateWantedPlantDef), new Type[] { typeof(IntVec3), typeof(Map) });
            string blocker;
            if (source == null) { Harvest.Disable("source lookup failed"); Sow.Disable("source lookup failed"); return; }
            if (harvestAuthority == null || HasUnsafeForeignPatch(harvestAuthority, IsKnownSafeHarvestPatch, out blocker)) Harvest.Disable(harvestAuthority == null ? "authority lookup failed" : "foreign Harvest authority: " + blocker); else Harvest.Enable();
            if (sowAuthority == null || wanted == null) Sow.Disable("authority/index lookup failed");
            else if (HasUnsafeForeignPatch(sowAuthority, null, out blocker) || HasUnsafeForeignPatch(wanted, null, out blocker)) Sow.Disable("foreign Sow authority: " + blocker); else Sow.Enable();
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeOptimizerS53), nameof(GrowerCellsPostfix)) { priority = Priority.Last });
        }

        private static void InstallClearSnow()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_ClearSnow), nameof(WorkGiver_ClearSnow.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_ClearSnow), nameof(WorkGiver_ClearSnow.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
            string blocker;
            if (source == null || authority == null) { ClearSnow.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(authority, null, out blocker)) { ClearSnow.Disable("foreign HasJobOnCell: " + blocker); return; }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeOptimizerS53), nameof(ClearSnowCellsPostfix)) { priority = Priority.Last });
            ClearSnow.Enable();
        }

        private static void InstallDoBill()
        {
            MethodInfo authority = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.JobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            MethodInfo baseHas = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing), new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            string blocker;
            if (authority == null || baseHas == null) { DoBill.Disable("target lookup failed"); return; }
            if (HasUnsafeForeignPatch(authority, null, out blocker) || HasUnsafeForeignPatch(baseHas, null, out blocker)) { DoBill.Disable("foreign DoBill authority: " + blocker); return; }
            DoBill.Enable();
        }

        private static void InstallSharedThingSource()
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
            if (source == null) { if (Tend.Enabled) Tend.Disable("shared source lookup failed"); if (DoBill.Enabled) DoBill.Disable("shared source lookup failed"); return; }
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(CompositeOptimizerS53), nameof(PotentialWorkThingsGlobalPostfix)) { priority = Priority.Last });
        }

        private static bool IsKnownSafeHarvestPatch(Patch patch)
        {
            if (patch == null || patch.PatchMethod == null) return false;
            string typeName = patch.PatchMethod.DeclaringType == null ? string.Empty : patch.PatchMethod.DeclaringType.FullName;
            string methodName = patch.PatchMethod.Name ?? string.Empty;
            return methodName.IndexOf("HasJobOnCellHarvestPostfix", StringComparison.OrdinalIgnoreCase) >= 0 || (typeName.IndexOf("AlienRace", StringComparison.OrdinalIgnoreCase) >= 0 && methodName.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasUnsafeForeignPatch(MethodBase target, Func<Patch, bool> safeForeign, out string blocker)
        {
            blocker = null;
            Patches info = target == null ? null : Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return Check(info.Prefixes, safeForeign, out blocker) || Check(info.Postfixes, safeForeign, out blocker) || Check(info.Transpilers, safeForeign, out blocker) || Check(info.Finalizers, safeForeign, out blocker);
        }

        private static bool Check(IList<Patch> patches, Func<Patch, bool> safeForeign, out string blocker)
        {
            blocker = null;
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null || string.Equals(p.owner, HarmonyId, StringComparison.Ordinal)) continue;
                if (safeForeign != null && safeForeign(p)) continue;
                MethodInfo mi = p.PatchMethod;
                blocker = (p.owner ?? "<unknown-owner>") + " :: " + (mi == null || mi.DeclaringType == null ? "<unknown-method>" : mi.DeclaringType.FullName + "." + mi.Name);
                return true;
            }
            return false;
        }

        internal static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        public static void RuntimeReportPostfix()
        {
            Log.Message("[RimMT] S5.3 Composite report: " + BuildRoof.Summary() + " | " + Tend.Summary() + " | " + Harvest.Summary() + " | " + ClearSnow.Summary() + " | " + Sow.Summary() + " | " + DoBill.Summary());
        }
    }
}
