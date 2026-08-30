using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace RimMTS53Composite
{
    internal static partial class CompositeOptimizerS53
    {
        private static readonly ConditionalWeakTable<Map, TendMapCache> TendCaches = new ConditionalWeakTable<Map, TendMapCache>();
        private static readonly ConditionalWeakTable<Map, BillMapCache> BillCaches = new ConditionalWeakTable<Map, BillMapCache>();
        private static readonly Thing[] EmptyThings = new Thing[0];

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
                    bool hasTarget;
                    if (humanlikeOnly) hasTarget = urgent ? cache.UrgentHumanlike : cache.AnyHumanlike;
                    else if (animalOnly) hasTarget = urgent ? cache.UrgentAnimal : cache.AnyAnimal;
                    else hasTarget = urgent ? cache.UrgentAny : cache.Any;
                    Tend.GateCheck();
                    if (!hasTarget)
                    {
                        Tend.GateHit();
                        __result = EmptyThings;
                    }
                }
                return;
            }

            WorkGiver_DoBill billGiverWork = __instance as WorkGiver_DoBill;
            if (!DoBill.Enabled || billGiverWork == null) return;
            if (DoBill.ShouldParityBypass()) return;
            BillMapCache billCache = BillCaches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
            __result = billCache.Get(billGiverWork, pawn.Map);
        }

        private sealed class TendMapCache
        {
            private int tick = int.MinValue;
            internal bool Any;
            internal bool AnyHumanlike;
            internal bool AnyAnimal;
            internal bool UrgentAny;
            internal bool UrgentHumanlike;
            internal bool UrgentAnimal;

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
                    bool normal;
                    bool urgent;
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
                if (tick != now)
                {
                    tick = now;
                    byDef.Clear();
                }

                List<Thing> cached;
                if (byDef.TryGetValue(giver.def, out cached))
                {
                    DoBill.IndexHit();
                    return cached;
                }

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
