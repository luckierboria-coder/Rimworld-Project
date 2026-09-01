using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTRC2T2
{
    /// <summary>
    /// Stage 4C: tail-only DoBill outcome/repetition telemetry.
    /// No gameplay result is changed. JobOnThing always executes normally.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Stage4CDoBillOutcomeProfiler
    {
        private const string HarmonyId = "allen.rimmt";
        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly FieldInfo WorkGiverDefField = AccessTools.Field(typeof(WorkGiver), "def");

        [ThreadStatic] private static long seenToken;
        [ThreadStatic] private static Dictionary<int, byte> seenTargets;

        private static bool installed;
        private static int patched;
        private static long failures;
        private static long observed;
        private static long nullResults;
        private static long jobResults;
        private static long repeatedCalls;
        private static long repeatedNullResults;
        private static long repeatedJobResults;
        private static long packagesWithSamples;
        private static long lastCountedPackage;
        private static long bill0, bill1, bill2, bill3to4, bill5plus;
        private static long bucket32_63, bucket64_127, bucket128plus;
        private static double totalMs;
        private static double repeatMs;
        private static double nullMs;
        private static double jobMs;
        private static double maxMs;
        private static readonly object Gate = new object();
        private static readonly Dictionary<ThingDef, Stat> ByThingDef = new Dictionary<ThingDef, Stat>();
        private static readonly Dictionary<WorkGiverDef, Stat> ByWorkGiverDef = new Dictionary<WorkGiverDef, Stat>();

        private sealed class Stat
        {
            public long Calls, Nulls, Jobs, Repeats;
            public double Ms;
        }

        private struct CallState
        {
            public bool Sample;
            public bool Repeated;
            public long Started;
            public Thing Thing;
            public WorkGiverDef WorkDef;
            public int BillCount;
            public double EntryElapsedMs;
        }

        static Stage4CDoBillOutcomeProfiler() { LongEventHandler.ExecuteWhenFinished(Install); }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                MethodInfo[] methods = typeof(WorkGiver_DoBill).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m == null || m.Name != "JobOnThing" || !typeof(Job).IsAssignableFrom(m.ReturnType)) continue;
                    Harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(Stage4CDoBillOutcomeProfiler), nameof(Prefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(Stage4CDoBillOutcomeProfiler), nameof(Postfix)) { priority = Priority.Last });
                    patched++;
                }

                Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
                if (report != null)
                    Harmony.Patch(report, postfix: new HarmonyMethod(typeof(Stage4CDoBillOutcomeProfiler), nameof(ReportPostfix)) { priority = Priority.Last });

                Log.Message("[RimMT] RC2-T2 Stage 4C DoBill Outcome/Repetition Profiler installed. Tail-only telemetry observes full JobOnThing results; no result is skipped, cached, or modified.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 Stage 4C failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(object __instance, object[] __args, out CallState __state)
        {
            __state = default(CallState);
            try
            {
                if (!PreTailStructureProfiler.IsTailActive) return;
                long token = PreTailStructureProfiler.CurrentJobToken;
                if (token == 0L) return;

                Thing thing = null;
                if (__args != null)
                    for (int i = 0; i < __args.Length; i++)
                    {
                        Thing t = __args[i] as Thing;
                        if (t != null) { thing = t; break; }
                    }
                if (thing == null) return;

                if (seenTargets == null) seenTargets = new Dictionary<int, byte>(32);
                if (seenToken != token)
                {
                    seenToken = token;
                    seenTargets.Clear();
                }

                int id = thing.thingIDNumber;
                byte count;
                bool repeated = seenTargets.TryGetValue(id, out count);
                if (count < 255) seenTargets[id] = (byte)(count + 1); else seenTargets[id] = count;

                if (Interlocked.Read(ref lastCountedPackage) != token)
                {
                    Interlocked.Exchange(ref lastCountedPackage, token);
                    Interlocked.Increment(ref packagesWithSamples);
                }

                int billCount = -1;
                IBillGiver bg = thing as IBillGiver;
                if (bg != null && bg.BillStack != null && bg.BillStack.Bills != null) billCount = bg.BillStack.Bills.Count;

                WorkGiverDef workDef = null;
                if (WorkGiverDefField != null && __instance != null) workDef = WorkGiverDefField.GetValue(__instance) as WorkGiverDef;

                __state.Sample = true;
                __state.Repeated = repeated;
                __state.Started = Stopwatch.GetTimestamp();
                __state.Thing = thing;
                __state.WorkDef = workDef;
                __state.BillCount = billCount;
                __state.EntryElapsedMs = PreTailStructureProfiler.CurrentJobElapsedMs;
            }
            catch { Interlocked.Increment(ref failures); }
        }

        public static void Postfix(Job __result, CallState __state)
        {
            if (!__state.Sample) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * TickToMs;
                bool isNull = __result == null;
                Interlocked.Increment(ref observed);
                if (isNull) Interlocked.Increment(ref nullResults); else Interlocked.Increment(ref jobResults);
                if (__state.Repeated)
                {
                    Interlocked.Increment(ref repeatedCalls);
                    if (isNull) Interlocked.Increment(ref repeatedNullResults); else Interlocked.Increment(ref repeatedJobResults);
                }

                int bc = __state.BillCount;
                if (bc == 0) Interlocked.Increment(ref bill0); else if (bc == 1) Interlocked.Increment(ref bill1); else if (bc == 2) Interlocked.Increment(ref bill2); else if (bc >= 3 && bc <= 4) Interlocked.Increment(ref bill3to4); else if (bc >= 5) Interlocked.Increment(ref bill5plus);
                double e = __state.EntryElapsedMs;
                if (e < 64.0) Interlocked.Increment(ref bucket32_63); else if (e < 128.0) Interlocked.Increment(ref bucket64_127); else Interlocked.Increment(ref bucket128plus);

                lock (Gate)
                {
                    totalMs += ms;
                    if (__state.Repeated) repeatMs += ms;
                    if (isNull) nullMs += ms; else jobMs += ms;
                    if (ms > maxMs) maxMs = ms;
                    Add(ByThingDef, __state.Thing == null ? null : __state.Thing.def, ms, isNull, __state.Repeated);
                    Add(ByWorkGiverDef, __state.WorkDef, ms, isNull, __state.Repeated);
                }
            }
            catch { Interlocked.Increment(ref failures); }
        }

        private static void Add<T>(Dictionary<T, Stat> map, T key, double ms, bool isNull, bool repeat) where T : class
        {
            if (key == null) return;
            Stat s;
            if (!map.TryGetValue(key, out s)) { s = new Stat(); map[key] = s; }
            s.Calls++; s.Ms += ms; if (isNull) s.Nulls++; else s.Jobs++; if (repeat) s.Repeats++;
        }

        public static void ReportPostfix()
        {
            long calls = Interlocked.Read(ref observed);
            long reps = Interlocked.Read(ref repeatedCalls);
            double t, r, n, j, mx;
            List<KeyValuePair<ThingDef, Stat>> things;
            List<KeyValuePair<WorkGiverDef, Stat>> works;
            lock (Gate)
            {
                t=totalMs; r=repeatMs; n=nullMs; j=jobMs; mx=maxMs;
                things = new List<KeyValuePair<ThingDef, Stat>>(ByThingDef);
                works = new List<KeyValuePair<WorkGiverDef, Stat>>(ByWorkGiverDef);
            }
            things.Sort((a,b) => b.Value.Ms.CompareTo(a.Value.Ms));
            works.Sort((a,b) => b.Value.Ms.CompareTo(a.Value.Ms));

            Log.Message("[RimMT] RC2-T2 Stage 4C DoBill Outcome report: patched=" + patched +
                ", packages=" + Interlocked.Read(ref packagesWithSamples) +
                ", calls=" + calls +
                ", result(null/job)=" + Interlocked.Read(ref nullResults) + "/" + Interlocked.Read(ref jobResults) +
                ", repeatedCalls=" + reps + " (" + (calls == 0 ? 0.0 : 100.0 * reps / calls).ToString("F1") + "%)" +
                ", repeatedResult(null/job)=" + Interlocked.Read(ref repeatedNullResults) + "/" + Interlocked.Read(ref repeatedJobResults) +
                ", ms(total/null/job/repeat/max)=" + t.ToString("F2") + "/" + n.ToString("F2") + "/" + j.ToString("F2") + "/" + r.ToString("F2") + "/" + mx.ToString("F2") +
                ", billCount(0/1/2/3-4/5+)=" + Interlocked.Read(ref bill0) + "/" + Interlocked.Read(ref bill1) + "/" + Interlocked.Read(ref bill2) + "/" + Interlocked.Read(ref bill3to4) + "/" + Interlocked.Read(ref bill5plus) +
                ", entryBucket32-63/64-127/128+=" + Interlocked.Read(ref bucket32_63) + "/" + Interlocked.Read(ref bucket64_127) + "/" + Interlocked.Read(ref bucket128plus) +
                ", failures=" + Interlocked.Read(ref failures) + ".");

            int tn = Math.Min(6, things.Count);
            for (int i=0;i<tn;i++)
            {
                var kv=things[i]; Stat s=kv.Value;
                Log.Message("[RimMT]   Stage4C ThingDef #"+(i+1)+" " + kv.Key.defName + ": calls="+s.Calls+", null/job="+s.Nulls+"/"+s.Jobs+", repeats="+s.Repeats+", totalMs="+s.Ms.ToString("F2")+".");
            }
            int wn = Math.Min(6, works.Count);
            for (int i=0;i<wn;i++)
            {
                var kv=works[i]; Stat s=kv.Value;
                Log.Message("[RimMT]   Stage4C WorkGiver #"+(i+1)+" " + kv.Key.defName + ": calls="+s.Calls+", null/job="+s.Nulls+"/"+s.Jobs+", repeats="+s.Repeats+", totalMs="+s.Ms.ToString("F2")+".");
            }
        }
    }
}
