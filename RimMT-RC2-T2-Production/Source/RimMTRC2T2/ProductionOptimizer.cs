using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTRC2T2
{
    [StaticConstructorOnStartup]
    internal static class ProductionOptimizer
    {
        private const string HarmonyId = "allen.rimmt";
        private const int EarlyRescueMs = 16;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly ConditionalWeakTable<Map, BillMapCache> BillCaches = new ConditionalWeakTable<Map, BillMapCache>();

        private static bool installed;
        private static bool oldDoBillDisabled;
        private static bool doBillSourcePatched;
        private static bool doBillShouldSkipPatched;
        private static bool billInvalidationPatched;
        private static bool td1Disabled;
        private static bool s51Retuned;
        private static long calls;
        private static long builds;
        private static long hits;
        private static long invalidations;
        private static long rawSeen;
        private static long stableKept;
        private static long shouldSkipCalls;
        private static long shouldSkipTrue;
        private static long shouldSkipFalse;
        private static long failures;

        private sealed class BillIndexEntry
        {
            internal List<Thing> Things;
            internal int RawCount;
        }

        private sealed class BillMapCache
        {
            private readonly Dictionary<WorkGiverDef, BillIndexEntry> byDef = new Dictionary<WorkGiverDef, BillIndexEntry>();

            internal List<Thing> Get(WorkGiver_DoBill giver, Map map)
            {
                Interlocked.Increment(ref calls);
                if (giver == null || giver.def == null || map == null) return null;

                List<Thing> source = map.listerThings.ThingsMatching(giver.PotentialWorkThingRequest);
                int rawCount = source == null ? 0 : source.Count;
                BillIndexEntry entry;
                if (byDef.TryGetValue(giver.def, out entry) && entry != null && entry.RawCount == rawCount)
                {
                    Interlocked.Increment(ref hits);
                    return entry.Things;
                }

                Interlocked.Increment(ref builds);
                List<Thing> stable = new List<Thing>(Math.Min(rawCount, 64));
                if (source != null)
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        Thing thing = source[i];
                        Interlocked.Increment(ref rawSeen);
                        if (thing == null || !thing.Spawned || thing.Map != map) continue;
                        IBillGiver billGiver = thing as IBillGiver;
                        if (billGiver == null) continue;
                        bool usable;
                        try { usable = giver.ThingIsUsableBillGiver(thing); }
                        catch { usable = false; }
                        if (!usable) continue;
                        stable.Add(thing);
                        Interlocked.Increment(ref stableKept);
                    }
                }

                byDef[giver.def] = new BillIndexEntry { Things = stable, RawCount = rawCount };
                return stable;
            }

            internal void Invalidate()
            {
                byDef.Clear();
            }
        }

        static ProductionOptimizer()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                DisableOldDoBillOptimizer();
                DisableTD1();
                RetuneS51();
                InstallDoBillPersistentIndex();
                HookRuntimeReport();
                Log.Message("[RimMT] V0.9.1 RC2-T2 Production Tail Killer installed. T1 high-frequency tail probes are absent; TD1 auto-capture is disabled; DoBill uses persistent per-map indexes; validated S5.1 known-small rescue starts at 16ms instead of 24ms.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 production optimizer install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void DisableOldDoBillOptimizer()
        {
            try
            {
                Type composite = AccessTools.TypeByName("RimMTS53Composite.CompositeOptimizerS53");
                if (composite == null) return;
                FieldInfo field = composite.GetField("DoBill", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object state = field == null ? null : field.GetValue(null);
                if (state == null) return;
                MethodInfo disable = state.GetType().GetMethod("Disable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (disable == null) return;
                disable.Invoke(state, new object[] { "superseded by RC2-T2 persistent DoBill index" });
                oldDoBillDisabled = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 could not disable legacy S5.3 DoBill source; new DoBill index will stay inactive. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void DisableTD1()
        {
            try
            {
                Type t = AccessTools.TypeByName("RimMT.TickTailTraceTD1");
                if (t == null) return;
                FieldInfo capture = t.GetField("captureActive", BindingFlags.Static | BindingFlags.NonPublic);
                if (capture != null && (bool)capture.GetValue(null))
                {
                    MethodInfo stop = t.GetMethod("StopCapture", BindingFlags.Static | BindingFlags.NonPublic);
                    if (stop != null) stop.Invoke(null, null);
                }
                FieldInfo completed = t.GetField("completed", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo attempted = t.GetField("installAttempted", BindingFlags.Static | BindingFlags.NonPublic);
                if (completed != null) completed.SetValue(null, true);
                if (attempted != null) attempted.SetValue(null, true);
                td1Disabled = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 TD1 disable failed; gameplay remains valid but temporary TD1 profiling may still run. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void RetuneS51()
        {
            try
            {
                Type t = AccessTools.TypeByName("RimMT.JobGiverHybridTailS51");
                if (t == null) return;
                RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                FieldInfo threshold = t.GetField("EarlyThresholdTicks", BindingFlags.Static | BindingFlags.NonPublic);
                if (threshold == null) return;
                long ticks = Math.Max(1L, Stopwatch.Frequency * EarlyRescueMs / 1000L);
                threshold.SetValue(null, ticks);
                long live = (long)threshold.GetValue(null);
                s51Retuned = live == ticks;
                if (!s51Retuned)
                    Log.Warning("[RimMT] RC2-T2 S5.1 early-rescue retune did not stick; original 24ms admission remains active.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 S5.1 early-rescue retune failed; original 24ms admission remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InstallDoBillPersistentIndex()
        {
            if (!oldDoBillDisabled) return;

            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
            if (source != null)
            {
                Harmony.Patch(source, postfix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(PotentialWorkThingsGlobalPostfix)) { priority = Priority.Last });
                doBillSourcePatched = true;
            }

            MethodInfo shouldSkip = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.ShouldSkip), new Type[] { typeof(Pawn), typeof(bool) });
            if (shouldSkip != null && !HasUnsafeForeignPatch(shouldSkip))
            {
                Harmony.Patch(shouldSkip, prefix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(DoBillShouldSkipPrefix)) { priority = Priority.First });
                doBillShouldSkipPatched = true;
            }

            MethodInfo spawn = AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup), new Type[] { typeof(Map), typeof(bool) });
            MethodInfo despawn = AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn), new Type[] { typeof(DestroyMode) });
            if (spawn != null)
                Harmony.Patch(spawn, postfix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(ThingSpawnedPostfix)) { priority = Priority.Last });
            if (despawn != null)
                Harmony.Patch(despawn, prefix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(ThingDeSpawnPrefix)) { priority = Priority.First });
            billInvalidationPatched = spawn != null && despawn != null;
        }

        private static bool HasUnsafeForeignPatch(MethodBase target)
        {
            Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null) continue;
                if (string.Equals(p.owner, HarmonyId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        public static void PotentialWorkThingsGlobalPostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!doBillSourcePatched || __result != null || __instance == null || pawn == null || pawn.Map == null) return;
            WorkGiver_DoBill giver = __instance as WorkGiver_DoBill;
            if (giver == null) return;
            try
            {
                BillMapCache cache = BillCaches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> indexed = cache.Get(giver, pawn.Map);
                if (indexed != null) __result = indexed;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 8)
                    Log.Warning("[RimMT] RC2-T2 DoBill source index failed; falling back to Vanilla null source. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool DoBillShouldSkipPrefix(WorkGiver_DoBill __instance, Pawn pawn, ref bool __result)
        {
            if (!doBillShouldSkipPatched || __instance == null || pawn == null || pawn.Map == null) return true;
            try
            {
                Interlocked.Increment(ref shouldSkipCalls);
                BillMapCache cache = BillCaches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(__instance, pawn.Map);
                if (things != null)
                {
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing thing = things[i];
                        IBillGiver billGiver = thing as IBillGiver;
                        if (billGiver == null || object.ReferenceEquals(billGiver, pawn) || billGiver.BillStack == null) continue;
                        if (billGiver.BillStack.AnyShouldDoNow)
                        {
                            __result = false;
                            Interlocked.Increment(ref shouldSkipFalse);
                            return false;
                        }
                    }
                }
                __result = true;
                Interlocked.Increment(ref shouldSkipTrue);
                return false;
            }
            catch
            {
                Interlocked.Increment(ref failures);
                return true;
            }
        }

        public static void ThingSpawnedPostfix(Thing __instance, Map map)
        {
            if (__instance == null || map == null || !(__instance is IBillGiver)) return;
            Invalidate(map);
        }

        public static void ThingDeSpawnPrefix(Thing __instance)
        {
            if (__instance == null || !(__instance is IBillGiver)) return;
            Map map = null;
            try { map = __instance.Map; } catch { }
            if (map != null) Invalidate(map);
        }

        private static void Invalidate(Map map)
        {
            BillMapCache cache;
            if (map != null && BillCaches.TryGetValue(map, out cache) && cache != null)
            {
                cache.Invalidate();
                Interlocked.Increment(ref invalidations);
            }
        }

        private static void HookRuntimeReport()
        {
            Type t = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = t == null ? null : AccessTools.Method(t, "LogRuntimeReport");
            if (report != null)
                Harmony.Patch(report, postfix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(ReportPostfix)) { priority = Priority.Last });
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 production report: legacyDoBillDisabled=" + oldDoBillDisabled +
                ", doBillSource=" + doBillSourcePatched +
                ", doBillShouldSkip=" + doBillShouldSkipPatched +
                ", invalidationHooks=" + billInvalidationPatched +
                ", td1Disabled=" + td1Disabled +
                ", s51EarlyRescue16ms=" + s51Retuned +
                ", doBillCalls/builds/hits=" + Interlocked.Read(ref calls) + "/" + Interlocked.Read(ref builds) + "/" + Interlocked.Read(ref hits) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", rawSeen/stableKept=" + Interlocked.Read(ref rawSeen) + "/" + Interlocked.Read(ref stableKept) +
                ", shouldSkip calls/true/false=" + Interlocked.Read(ref shouldSkipCalls) + "/" + Interlocked.Read(ref shouldSkipTrue) + "/" + Interlocked.Read(ref shouldSkipFalse) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
