using System;
using System.Collections.Generic;
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
        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly ConditionalWeakTable<Map, BillMapCache> BillCaches = new ConditionalWeakTable<Map, BillMapCache>();

        private static bool installed;
        private static bool legacyDoBillDisabled;
        private static bool sourcePatched;
        private static bool shouldSkipPatched;
        private static bool invalidationHooks;
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

        private sealed class Entry
        {
            internal int RawCount;
            internal List<Thing> Things;
        }

        private sealed class BillMapCache
        {
            private readonly Dictionary<WorkGiverDef, Entry> byDef = new Dictionary<WorkGiverDef, Entry>();

            internal List<Thing> Get(WorkGiver_DoBill giver, Map map)
            {
                Interlocked.Increment(ref calls);
                if (giver == null || giver.def == null || map == null) return null;

                List<Thing> source = map.listerThings.ThingsMatching(giver.PotentialWorkThingRequest);
                int rawCount = source == null ? 0 : source.Count;
                Entry entry;
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
                        if (!(thing is IBillGiver)) continue;
                        bool usable;
                        try { usable = giver.ThingIsUsableBillGiver(thing); }
                        catch { usable = false; }
                        if (!usable) continue;
                        stable.Add(thing);
                        Interlocked.Increment(ref stableKept);
                    }
                }

                byDef[giver.def] = new Entry { RawCount = rawCount, Things = stable };
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
                DisableLegacyDoBill();
                InstallPersistentDoBill();
                HookReport();
                Log.Message("[RimMT] V0.9.1 RC2-T2 Production Tail Killer (Baseline-first) installed. Baseline B1 S5.1 24ms admission is unchanged. No RC2Tail probes or threshold retuning are active. Persistent DoBill index enabled when compatibility checks pass.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 baseline-first install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void DisableLegacyDoBill()
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
                disable.Invoke(state, new object[] { "superseded by RC2-T2 baseline-first persistent DoBill index" });
                legacyDoBillDisabled = true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 could not disable legacy DoBill optimization; persistent replacement stays inactive. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void InstallPersistentDoBill()
        {
            if (!legacyDoBillDisabled) return;

            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
            if (source != null)
            {
                Harmony.Patch(source, postfix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(SourcePostfix)) { priority = Priority.Last });
                sourcePatched = true;
            }

            MethodInfo shouldSkip = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.ShouldSkip), new Type[] { typeof(Pawn), typeof(bool) });
            if (shouldSkip != null && !HasUnsafeForeignPatch(shouldSkip))
            {
                Harmony.Patch(shouldSkip, prefix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(ShouldSkipPrefix)) { priority = Priority.First });
                shouldSkipPatched = true;
            }

            MethodInfo spawn = AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup), new Type[] { typeof(Map), typeof(bool) });
            MethodInfo despawn = AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn), new Type[] { typeof(DestroyMode) });
            if (spawn != null) Harmony.Patch(spawn, postfix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(SpawnPostfix)) { priority = Priority.Last });
            if (despawn != null) Harmony.Patch(despawn, prefix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(DeSpawnPrefix)) { priority = Priority.First });
            invalidationHooks = spawn != null && despawn != null;
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

        public static void SourcePostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!sourcePatched || __result != null || __instance == null || pawn == null || pawn.Map == null) return;
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
                if (Interlocked.Read(ref failures) <= 4)
                    Log.Warning("[RimMT] RC2-T2 DoBill source index failed; Vanilla null source retained. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool ShouldSkipPrefix(WorkGiver_DoBill __instance, Pawn pawn, ref bool __result)
        {
            if (!shouldSkipPatched || __instance == null || pawn == null || pawn.Map == null) return true;
            try
            {
                Interlocked.Increment(ref shouldSkipCalls);
                BillMapCache cache = BillCaches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(__instance, pawn.Map);
                if (things != null)
                {
                    for (int i = 0; i < things.Count; i++)
                    {
                        IBillGiver billGiver = things[i] as IBillGiver;
                        if (billGiver == null || billGiver.BillStack == null) continue;
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

        public static void SpawnPostfix(Thing __instance, Map map)
        {
            if (__instance == null || map == null || !(__instance is IBillGiver)) return;
            Invalidate(map);
        }

        public static void DeSpawnPrefix(Thing __instance)
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

        private static void HookReport()
        {
            Type t = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = t == null ? null : AccessTools.Method(t, "LogRuntimeReport");
            if (report != null)
                Harmony.Patch(report, postfix: new HarmonyMethod(typeof(ProductionOptimizer), nameof(ReportPostfix)) { priority = Priority.Last });
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 baseline-first report: legacyDoBillDisabled=" + legacyDoBillDisabled +
                ", source=" + sourcePatched +
                ", shouldSkip=" + shouldSkipPatched +
                ", invalidationHooks=" + invalidationHooks +
                ", s51Admission=24ms-baseline-unchanged" +
                ", doBillCalls/builds/hits=" + Interlocked.Read(ref calls) + "/" + Interlocked.Read(ref builds) + "/" + Interlocked.Read(ref hits) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", rawSeen/stableKept=" + Interlocked.Read(ref rawSeen) + "/" + Interlocked.Read(ref stableKept) +
                ", shouldSkip calls/true/false=" + Interlocked.Read(ref shouldSkipCalls) + "/" + Interlocked.Read(ref shouldSkipTrue) + "/" + Interlocked.Read(ref shouldSkipFalse) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
