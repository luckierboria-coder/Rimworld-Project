using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    internal static class DiagnosticReport
    {
        internal static string Build()
        {
            StringBuilder sb = new StringBuilder(49152);
            sb.AppendLine("============================================================");
            sb.AppendLine("RimMT Diagnostics v" + DiagnosticsBootstrap.Version + " report");
            sb.AppendLine("ProgramState=" + Current.ProgramState + ", RimWorld=" + VersionControl.CurrentVersionStringWithRev);
            try { sb.AppendLine("LoadedMods=" + LoadedModManager.RunningModsListForReading.Count()); }
            catch { }
            try { sb.AppendLine("GameTick=" + (Find.TickManager == null ? -1 : Find.TickManager.TicksGame)); }
            catch { }
            sb.AppendLine("Settings: sampleEveryTicks=" + RimMTDiagnosticsSettings.SampleEveryTicks +
                ", tailThresholdMs=" + RimMTDiagnosticsSettings.TailThresholdMs +
                ", postSpikeBurstTicks=" + RimMTDiagnosticsSettings.PostSpikeBurstTicks +
                ", waitTrace=" + RimMTDiagnosticsSettings.EnableWaitTrace +
                ", searchTiming=" + RimMTDiagnosticsSettings.EnableSearchTiming);
            sb.AppendLine("------------------------------------------------------------");
            sb.Append(DiagnosticsHub.BuildSummary());
            sb.Append(DiagnosticsV02.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[RimMT production summaries via reflection]");
            sb.Append(RimMTBridge.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[Harmony audit]");
            sb.Append(HarmonyAudit.BuildSummary());
            sb.AppendLine("============================================================");
            return sb.ToString();
        }
    }

    internal static class RimMTBridge
    {
        private static readonly string[] SummaryTypes = new string[]
        {
            "RimMT.JobSearchTransaction093T20",
            "RimMT.GenClosestTransactionIndex093T22",
            "RimMT.AggressiveReachabilityProfilesV17",
            "RimMT.SimulationEpochCoordinator093T26",
            "RimMT.WorkGiverParallelSafety093T27_2",
            "RimMT.JobGiverSlowSearch0419S",
            "RimMT.DoBillTailFabric092",
            "RimMT.PersistentDoBillIndex092",
            "RimMT.WorkGiverMergePartnerIndex093T4"
        };

        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(12288);
            Assembly rimmt = null;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly a = assemblies[i];
                    if (a.GetName().Name == "RimMT") { rimmt = a; break; }
                }
            }
            catch { }
            if (rimmt == null)
            {
                sb.AppendLine("RimMT assembly not found.");
                return sb.ToString();
            }

            sb.AppendLine("Assembly=" + rimmt.FullName);
            for (int i = 0; i < SummaryTypes.Length; i++)
            {
                Type t = null;
                try { t = rimmt.GetType(SummaryTypes[i], false); }
                catch { }
                if (t == null) continue;
                AppendMethod(sb, t, "Summary");
                if (SummaryTypes[i].EndsWith("WorkGiverParallelSafety093T27_2", StringComparison.Ordinal))
                    AppendMethod(sb, t, "ApiMatrixSummary");
            }
            return sb.ToString();
        }

        private static void AppendMethod(StringBuilder sb, Type type, string methodName)
        {
            try
            {
                MethodInfo m = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(string)) return;
                object value = m.Invoke(null, null);
                if (value != null) sb.AppendLine(value.ToString());
            }
            catch (Exception ex)
            {
                sb.AppendLine(type.FullName + "." + methodName + " reflection failed: " + ex.GetType().Name);
            }
        }
    }

    internal static class HarmonyAudit
    {
        internal static string BuildSummary()
        {
            StringBuilder sb = new StringBuilder(12288);
            AuditOne(sb, AccessTools.Method(typeof(TickManager), "DoSingleTick"), "TickManager.DoSingleTick");
            AuditOne(sb, AccessTools.Method(typeof(Pawn), "Tick"), "Pawn.Tick");
            AuditOne(sb, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), "Pawn_JobTracker.JobTrackerTick");
            AuditOne(sb, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), "Pawn_JobTracker.DetermineNextJob");
            AuditOne(sb, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"), "Pawn_PathFollower.PatherTick");
            AuditOne(sb, AccessTools.Method(typeof(Map), "MapPostTick"), "Map.MapPostTick");
            AuditOne(sb, AccessTools.Method(typeof(World), "WorldTick"), "World.WorldTick");
            AuditOne(sb, AccessTools.Method(typeof(Storyteller), "StorytellerTick"), "Storyteller.StorytellerTick");
            AuditNamed(sb, typeof(Reachability), "CanReach", "Reachability.CanReach");
            AuditNamed(sb, typeof(GenClosest), "ClosestThingReachable", "GenClosest.ClosestThingReachable");
            AuditNamed(sb, typeof(GenClosest), "ClosestThing_Global", "GenClosest.ClosestThing_Global");
            AuditOne(sb, AccessTools.Method(typeof(Thing), "CanStackWith"), "Thing.CanStackWith");
            return sb.ToString();
        }

        private static void AuditNamed(StringBuilder sb, Type type, string name, string label)
        {
            List<MethodInfo> methods;
            try { methods = AccessTools.GetDeclaredMethods(type); }
            catch { return; }
            int n = 0;
            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo m = methods[i];
                if (m == null || m.Name != name) continue;
                AuditOne(sb, m, label + "#" + n);
                n++;
            }
        }

        private static void AuditOne(StringBuilder sb, MethodBase method, string label)
        {
            if (method == null)
            {
                sb.AppendLine(label + ": missing");
                return;
            }
            try
            {
                Patches p = Harmony.GetPatchInfo(method);
                if (p == null)
                {
                    sb.AppendLine(label + ": no patches");
                    return;
                }
                List<string> rows = new List<string>();
                Add(rows, "P", p.Prefixes);
                Add(rows, "Q", p.Postfixes);
                Add(rows, "T", p.Transpilers);
                Add(rows, "F", p.Finalizers);
                sb.AppendLine(label + ": " + (rows.Count == 0 ? "no patches" : string.Join(" | ", rows.ToArray())));
            }
            catch (Exception ex)
            {
                sb.AppendLine(label + ": audit failed " + ex.GetType().Name);
            }
        }

        private static void Add(List<string> rows, string kind, IEnumerable<Patch> patches)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                string method = patch.PatchMethod == null ? "<null>" : patch.PatchMethod.DeclaringType.FullName + "." + patch.PatchMethod.Name;
                rows.Add(kind + "[" + patch.owner + ",pri=" + patch.priority + "," + method + "]");
            }
        }
    }
}
