using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTWaitProbe
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string Id = "allen.rimmt.waitprobe";

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(Id);
                DraftTrace.Install(harmony);
                WaitTrace.Install(harmony);
                Log.Message("[RimMT Wait Probe v0.3] Installed minimal draft-origin trace: patchedMethods=2. " +
                    "No GenClosest, WorkGiver, job, draft state, or ThinkResult is modified.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT Wait Probe v0.3] bootstrap failed: " + ex);
            }
        }
    }

    internal static class DraftTrace
    {
        private const int MaxReports = 128;
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_DraftController), "pawn");
        private static int reports;

        internal static void Install(Harmony harmony)
        {
            MethodInfo setter = AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted");
            if (setter == null) throw new MissingMethodException(typeof(Pawn_DraftController).FullName, "set_Drafted");
            harmony.Patch(setter, prefix: new HarmonyMethod(typeof(DraftTrace), nameof(Prefix)) { priority = Priority.First });
        }

        public static void Prefix(Pawn_DraftController __instance, bool value)
        {
            if (!value || reports >= MaxReports) return;
            bool alreadyDrafted;
            try { alreadyDrafted = __instance.Drafted; }
            catch { return; }
            if (alreadyDrafted) return;

            Pawn pawn;
            try { pawn = PawnField == null ? null : PawnField.GetValue(__instance) as Pawn; }
            catch { pawn = null; }
            if (!Eligible(pawn)) return;

            int number = ++reports;
            int tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
            string job = pawn.CurJob == null || pawn.CurJob.def == null ? "none" : pawn.CurJob.def.defName;
            Log.Message("[RimMT Wait Probe v0.3] DRAFT-ON#" + number +
                " pawn=" + pawn.LabelShortCap + "#" + pawn.thingIDNumber +
                " tick=" + tick + " pos=" + pawn.Position + " curJob=" + job +
                " source=" + CompactStack(new StackTrace(1, false)));
        }

        private static string CompactStack(StackTrace trace)
        {
            StackFrame[] frames = trace.GetFrames();
            if (frames == null || frames.Length == 0) return "<no-stack>";
            List<string> rows = new List<string>(12);
            for (int i = 0; i < frames.Length && rows.Count < 12; i++)
            {
                MethodBase method = frames[i].GetMethod();
                if (method == null) continue;
                Type type = method.DeclaringType;
                string full = (type == null ? "<global>" : type.FullName) + "." + method.Name;
                if (full.StartsWith("RimMTWaitProbe.", StringComparison.Ordinal) ||
                    full.StartsWith("HarmonyLib.", StringComparison.Ordinal) ||
                    full.StartsWith("System.Reflection.", StringComparison.Ordinal)) continue;
                rows.Add(full);
            }
            return rows.Count == 0 ? "<filtered>" : string.Join(" <- ", rows.ToArray());
        }

        private static bool Eligible(Pawn pawn)
        {
            return pawn != null && pawn.Faction == Faction.OfPlayer && pawn.RaceProps != null && pawn.RaceProps.Humanlike;
        }
    }

    internal static class WaitTrace
    {
        private const int MaxReports = 128;
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
        private static int reports;

        internal static void Install(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob");
            if (target == null) throw new MissingMethodException(typeof(Pawn_JobTracker).FullName, "DetermineNextJob");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(WaitTrace), nameof(Postfix)) { priority = Priority.Last });
        }

        public static void Postfix(Pawn_JobTracker __instance, ThinkResult __result)
        {
            if (reports >= MaxReports) return;
            Pawn pawn;
            try { pawn = PawnField == null ? null : PawnField.GetValue(__instance) as Pawn; }
            catch { return; }
            if (pawn == null || !pawn.Spawned || pawn.Faction != Faction.OfPlayer || pawn.RaceProps == null || !pawn.RaceProps.Humanlike) return;

            Job job;
            ThinkNode source;
            try { job = __result.Job; source = __result.SourceNode; }
            catch { return; }
            if (job == null || job.def == null || job.def != JobDefOf.Wait_Combat) return;

            int number = ++reports;
            int tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
            string assignment = "<none>";
            try { assignment = pawn.timetable == null || pawn.timetable.CurrentAssignment == null ? "<none>" : pawn.timetable.CurrentAssignment.defName; }
            catch { }
            Log.Message("[RimMT Wait Probe v0.3] WAIT-COMBAT#" + number +
                " pawn=" + pawn.LabelShortCap + "#" + pawn.thingIDNumber +
                " tick=" + tick + " pos=" + pawn.Position +
                " drafted=" + pawn.Drafted + " assignment=" + assignment +
                " source=" + (source == null ? "<null>" : source.GetType().FullName));
        }
    }
}
