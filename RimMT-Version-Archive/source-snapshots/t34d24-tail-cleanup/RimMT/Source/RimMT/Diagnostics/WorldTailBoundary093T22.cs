using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld.Planet;

namespace RimMT
{
    internal static class WorldTailBoundary093T22
    {
        private static bool installed;
        private static int patched;
        private static int installFailures;

        private static long worldCalls;
        private static long worldTicks;
        private static long worldMaxTicks;
        private static long worldOver50;
        private static long worldOver100;
        private static long worldOver1000;

        private static long componentCalls;
        private static long componentTicks;
        private static long componentMaxTicks;
        private static long componentOver50;
        private static long componentOver100;
        private static long componentOver1000;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase worldTick = AccessTools.Method(typeof(World), "WorldTick");
                if (worldTick != null)
                {
                    harmony.Patch(worldTick,
                        prefix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(WorldPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(WorldPostfix)));
                    patched++;
                }

                MethodBase components = AccessTools.Method(typeof(WorldComponentUtility), "WorldComponentTick",
                    new Type[] { typeof(World) });
                if (components != null)
                {
                    harmony.Patch(components,
                        prefix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(ComponentPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(ComponentPostfix)));
                    patched++;
                }

                installed = patched > 0;
            }
            catch
            {
                installFailures++;
                installed = false;
            }
        }

        public static void WorldPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void WorldPostfix(long __state)
        {
            Record(__state, ref worldCalls, ref worldTicks, ref worldMaxTicks,
                ref worldOver50, ref worldOver100, ref worldOver1000);
        }

        public static void ComponentPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void ComponentPostfix(long __state)
        {
            Record(__state, ref componentCalls, ref componentTicks, ref componentMaxTicks,
                ref componentOver50, ref componentOver100, ref componentOver1000);
        }

        private static void Record(long started,
            ref long calls, ref long total, ref long max,
            ref long over50, ref long over100, ref long over1000)
        {
            if (started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed < 0L) return;
            Interlocked.Increment(ref calls);
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
            long ms50 = Stopwatch.Frequency / 20;
            long ms100 = Stopwatch.Frequency / 10;
            long ms1000 = Stopwatch.Frequency;
            if (elapsed >= ms50) Interlocked.Increment(ref over50);
            if (elapsed >= ms100) Interlocked.Increment(ref over100);
            if (elapsed >= ms1000) Interlocked.Increment(ref over1000);
        }

        private static void UpdateMax(ref long field, long value)
        {
            long observed;
            while (value > (observed = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

        internal static string Summary()
        {
            long wc = Interlocked.Read(ref worldCalls);
            long cc = Interlocked.Read(ref componentCalls);
            double wTotalMs = Interlocked.Read(ref worldTicks) * 1000.0 / Stopwatch.Frequency;
            double cTotalMs = Interlocked.Read(ref componentTicks) * 1000.0 / Stopwatch.Frequency;
            return "T22 world-tail direct boundary: installed=" + installed +
                ", patched=" + patched +
                ", World.WorldTick[calls=" + wc +
                ", avgUs=" + (wc == 0 ? 0.0 : wTotalMs * 1000.0 / wc).ToString("F1") +
                ", maxMs=" + (Interlocked.Read(ref worldMaxTicks) * 1000.0 / Stopwatch.Frequency).ToString("F3") +
                ", >=50/100/1000ms=" + Interlocked.Read(ref worldOver50) + "/" +
                Interlocked.Read(ref worldOver100) + "/" + Interlocked.Read(ref worldOver1000) + "]" +
                ", WorldComponentUtility[calls=" + cc +
                ", avgUs=" + (cc == 0 ? 0.0 : cTotalMs * 1000.0 / cc).ToString("F1") +
                ", maxMs=" + (Interlocked.Read(ref componentMaxTicks) * 1000.0 / Stopwatch.Frequency).ToString("F3") +
                ", >=50/100/1000ms=" + Interlocked.Read(ref componentOver50) + "/" +
                Interlocked.Read(ref componentOver100) + "/" + Interlocked.Read(ref componentOver1000) + "]" +
                ", installFailures=" + installFailures +
                ". Direct Harmony timing closes the T1 WorldTick attribution hole; measurement-only.";
        }
    }
}
