using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimOPT
{
    internal static class DoBillIndex
    {
        private static readonly ConditionalWeakTable<Map, MapCache> Caches = new ConditionalWeakTable<Map, MapCache>();
        private static bool sourcePatched;
        private static bool shouldSkipPatched;
        private static int warnings;

        internal static string Status { get { return "DoBill[source=" + sourcePatched + ", shouldSkip=" + shouldSkipPatched + "]"; } }

        internal static void Install(Harmony harmony)
        {
            MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
            if (source != null)
            {
                harmony.Patch(source, postfix: new HarmonyMethod(typeof(DoBillIndex), nameof(SourcePostfix)) { priority = Priority.Last });
                sourcePatched = true;
            }

            MethodInfo shouldSkip = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.ShouldSkip), new Type[] { typeof(Pawn), typeof(bool) });
            if (shouldSkip != null && !HasForeignPatch(shouldSkip))
            {
                harmony.Patch(shouldSkip, prefix: new HarmonyMethod(typeof(DoBillIndex), nameof(ShouldSkipPrefix)) { priority = Priority.First });
                shouldSkipPatched = true;
            }

            MethodInfo spawn = AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup), new Type[] { typeof(Map), typeof(bool) });
            MethodInfo despawn = AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn), new Type[] { typeof(DestroyMode) });
            if (spawn != null) harmony.Patch(spawn, postfix: new HarmonyMethod(typeof(DoBillIndex), nameof(SpawnPostfix)) { priority = Priority.Last });
            if (despawn != null) harmony.Patch(despawn, prefix: new HarmonyMethod(typeof(DoBillIndex), nameof(DeSpawnPrefix)) { priority = Priority.First });
        }

        public static void SourcePostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!sourcePatched || __result != null || __instance == null || pawn == null || pawn.Map == null) return;
            WorkGiver_DoBill giver = __instance as WorkGiver_DoBill;
            if (giver == null || giver.def == null) return;
            try
            {
                Thing[] source = Caches.GetValue(pawn.Map, _ => new MapCache()).Get(giver, pawn.Map);
                List<Thing> filtered = null;
                for (int i = 0; i < source.Length; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || !thing.Spawned || thing.Map != pawn.Map)
                    {
                        if (filtered == null)
                        {
                            filtered = new List<Thing>(source.Length);
                            for (int j = 0; j < i; j++) filtered.Add(source[j]);
                        }
                        continue;
                    }
                    IBillGiver billGiver = thing as IBillGiver;
                    BillStack stack = billGiver == null ? null : billGiver.BillStack;
                    if (stack == null || ShouldDoNow(stack))
                    {
                        if (filtered != null) filtered.Add(thing);
                        continue;
                    }
                    if (filtered == null)
                    {
                        filtered = new List<Thing>(source.Length);
                        for (int j = 0; j < i; j++) filtered.Add(source[j]);
                    }
                }
                __result = filtered == null ? (IEnumerable<Thing>)source : filtered;
            }
            catch (Exception ex)
            {
                Warn("DoBill source filter failed open", ex);
            }
        }

        public static bool ShouldSkipPrefix(WorkGiver_DoBill __instance, Pawn pawn, ref bool __result)
        {
            if (!shouldSkipPatched || __instance == null || __instance.def == null || pawn == null || pawn.Map == null) return true;
            try
            {
                Thing[] source = Caches.GetValue(pawn.Map, _ => new MapCache()).Get(__instance, pawn.Map);
                for (int i = 0; i < source.Length; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || !thing.Spawned || thing.Map != pawn.Map) continue;
                    IBillGiver giver = thing as IBillGiver;
                    if (giver == null || ReferenceEquals(thing, pawn) || giver.BillStack == null) continue;
                    if (ShouldDoNow(giver.BillStack))
                    {
                        __result = false;
                        return false;
                    }
                }
                __result = true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool ShouldDoNow(BillStack stack)
        {
            if (PackageScope.IsInactive(stack)) return false;
            bool active = stack.AnyShouldDoNow;
            if (!active) PackageScope.MarkInactive(stack);
            return active;
        }

        public static void SpawnPostfix(Thing __instance, Map map)
        {
            if (!(__instance is IBillGiver) || map == null) return;
            MapCache cache;
            if (!Caches.TryGetValue(map, out cache)) return;
            try { cache.Add(__instance, map); }
            catch (Exception ex) { cache.Invalidate(); Warn("DoBill spawn update reset its index", ex); }
        }

        public static void DeSpawnPrefix(Thing __instance)
        {
            if (!(__instance is IBillGiver)) return;
            Map map = null;
            try { map = __instance.Map; } catch { }
            if (map == null) return;
            MapCache cache;
            if (!Caches.TryGetValue(map, out cache)) return;
            try { cache.Remove(__instance); }
            catch (Exception ex) { cache.Invalidate(); Warn("DoBill despawn update reset its index", ex); }
        }

        private static bool HasForeignPatch(MethodBase target)
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
                if (patch != null && !string.Equals(patch.owner, Bootstrap.HarmonyId, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Warn(string message, Exception ex)
        {
            if (System.Threading.Interlocked.Increment(ref warnings) <= 4)
                Log.Warning("[RimOPT] " + message + ": " + ex.GetType().Name + ": " + ex.Message);
        }

        private sealed class MapCache
        {
            private readonly object gate = new object();
            private readonly Dictionary<WorkGiverDef, Entry> byDef = new Dictionary<WorkGiverDef, Entry>();

            internal Thing[] Get(WorkGiver_DoBill giver, Map map)
            {
                lock (gate)
                {
                    Entry entry;
                    if (byDef.TryGetValue(giver.def, out entry)) return entry.Things;
                    ThingRequest request = giver.PotentialWorkThingRequest;
                    List<Thing> live = map.listerThings.ThingsMatching(request);
                    List<Thing> copy = new List<Thing>(live == null ? 0 : live.Count);
                    if (live != null)
                    {
                        for (int i = 0; i < live.Count; i++)
                        {
                            Thing thing = live[i];
                            if (thing != null && thing.Spawned && thing.Map == map && thing is IBillGiver) copy.Add(thing);
                        }
                    }
                    Thing[] snapshot = copy.ToArray();
                    byDef[giver.def] = new Entry(request, snapshot);
                    return snapshot;
                }
            }

            internal void Add(Thing thing, Map map)
            {
                if (thing == null || map == null || !thing.Spawned || thing.Map != map) return;
                lock (gate)
                {
                    foreach (Entry entry in byDef.Values)
                    {
                        if (!entry.Request.Accepts(thing) || Array.IndexOf(entry.Things, thing) >= 0) continue;
                        Thing[] next = new Thing[entry.Things.Length + 1];
                        Array.Copy(entry.Things, next, entry.Things.Length);
                        next[next.Length - 1] = thing;
                        entry.Things = next;
                    }
                }
            }

            internal void Remove(Thing thing)
            {
                lock (gate)
                {
                    foreach (Entry entry in byDef.Values)
                    {
                        int index = Array.IndexOf(entry.Things, thing);
                        if (index < 0) continue;
                        Thing[] next = new Thing[entry.Things.Length - 1];
                        if (index > 0) Array.Copy(entry.Things, 0, next, 0, index);
                        if (index < next.Length) Array.Copy(entry.Things, index + 1, next, index, next.Length - index);
                        entry.Things = next;
                    }
                }
            }

            internal void Invalidate() { lock (gate) byDef.Clear(); }
        }

        private sealed class Entry
        {
            internal readonly ThingRequest Request;
            internal Thing[] Things;
            internal Entry(ThingRequest request, Thing[] things) { Request = request; Things = things; }
        }
    }
}
