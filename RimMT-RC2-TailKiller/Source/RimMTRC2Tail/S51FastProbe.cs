using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMTRC2Tail
{
    [StaticConstructorOnStartup]
    internal static class S51FastProbe
    {
        private const string HarmonyId = "allen.rimmt";
        private const double KeepMs = 4.0;
        private const int KeepTop = 12;
        private static readonly List<Sample> Top = new List<Sample>();
        private static long calls;
        private static long over4;
        private static long over8;
        private static long over16;
        private static long over32;
        private static double maxMs;
        private static bool patched;
        private static int failures;

        private sealed class Sample
        {
            internal double Ms;
            internal int Count;
            internal string Validator;
        }

        static S51FastProbe()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Type t = AccessTools.TypeByName("RimMT.JobGiverHybridTailS51");
                MethodInfo target = t == null ? null : AccessTools.Method(t, "TryFast");
                if (target == null)
                {
                    Log.Warning("[RimMT-RC2] S5.1 fast-tail probe unavailable: TryFast not found.");
                    return;
                }
                Harmony h = new Harmony(HarmonyId);
                h.Patch(target,
                    prefix: new HarmonyMethod(typeof(S51FastProbe), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(S51FastProbe), nameof(Postfix)) { priority = Priority.Last });
                Type d = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = d == null ? null : AccessTools.Method(d, "LogRuntimeReport");
                if (report != null) h.Patch(report, postfix: new HarmonyMethod(typeof(S51FastProbe), nameof(ReportPostfix)) { priority = Priority.Last });
                patched = true;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warning("[RimMT-RC2] S5.1 fast-tail probe install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(object[] __args, ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Postfix(object[] __args, long __state)
        {
            if (__state == 0L) return;
            double ms = (double)(Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            calls++;
            if (ms > maxMs) maxMs = ms;
            if (ms < KeepMs) return;
            over4++;
            if (ms >= 8.0) over8++;
            if (ms >= 16.0) over16++;
            if (ms >= 32.0) over32++;
            int count = -1;
            string validator = "<unknown>";
            try
            {
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        object a = __args[i];
                        if (a is Predicate<Thing>)
                        {
                            Predicate<Thing> p = (Predicate<Thing>)a;
                            MethodInfo mi = p.Method;
                            validator = (mi == null || mi.DeclaringType == null) ? "<delegate>" : mi.DeclaringType.FullName + "." + mi.Name;
                        }
                        if (count < 0)
                        {
                            ICollection<Thing> cg = a as ICollection<Thing>;
                            if (cg != null) count = cg.Count;
                            else
                            {
                                ICollection c = a as ICollection;
                                if (c != null) count = c.Count;
                            }
                        }
                    }
                }
            }
            catch { }
            Keep(ms, count, validator);
        }

        private static void Keep(double ms, int count, string validator)
        {
            Sample s = new Sample { Ms = ms, Count = count, Validator = validator };
            int i = 0;
            while (i < Top.Count && Top[i].Ms >= ms) i++;
            Top.Insert(i, s);
            if (Top.Count > KeepTop) Top.RemoveAt(Top.Count - 1);
        }

        public static void ReportPostfix()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[RimMT] RC2 S5.1 fast-tail report: patched=").Append(patched)
              .Append(", calls=").Append(calls)
              .Append(", >4/8/16/32ms=").Append(over4).Append('/').Append(over8).Append('/').Append(over16).Append('/').Append(over32)
              .Append(", maxMs=").Append(maxMs.ToString("F3"))
              .Append(", failures=").Append(failures).Append('.');
            int n = Math.Min(Top.Count, 8);
            for (int i = 0; i < n; i++)
            {
                Sample s = Top[i];
                sb.Append("\n #").Append(i + 1).Append(' ').Append(s.Ms.ToString("F3")).Append("ms count=").Append(s.Count).Append(" validator=").Append(s.Validator);
            }
            Log.Message(sb.ToString());
        }
    }
}
