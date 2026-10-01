using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// Lean form of JS1.1S4. ThingRequest-backed >=256 searches retain the validated fast path.
    /// Once the current JobPackage has already spent 32ms, later >=16 ThingRequest searches and
    /// explicit custom enumerables may use nearest-first validator-first/live-CanReach rescue.
    /// V0.9.2 records route totals plus validator identity only for genuinely heavy calls (>=64
    /// validator rejects). Runtime Tune additionally resolves the captured WorkGiver_Scanner only
    /// on those heavy calls, with FieldInfo cached per compiler-generated closure type.
    /// </summary>
    internal static class JobGiverSlowSearch0419S
    {
        internal const string FeatureId = "ai.jobSlowSearch";
        private const int LargeSearchThreshold = 256;
        private const int TailMinSourceCount = 16;
        private const int TailRescueThresholdMs = 32;
        private const int EarlyKnownHeavyThresholdMs = 8;
        private const int TargetedEarlyThresholdMs = 8;
        private const long EarlyKnownHeavyMinCalls = 2;
        private const long EarlyKnownHeavyMinRejects = 512;
        private const int MaxSourceCount = 16384;
        private const int HeavyRejectThreshold = 64;
        private const int MaxHeavyValidatorKeys = 24;
        private const int MaxHeavyWorkGiverKeys = 64;
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);
        private static readonly long EarlyKnownHeavyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyKnownHeavyThresholdMs / 1000L);
        private static readonly long TargetedEarlyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TargetedEarlyThresholdMs / 1000L);

        [ThreadStatic] private static Candidate[] candidateScratch;
        [ThreadStatic] private static bool t8DetermineActive;
        [ThreadStatic] private static string t8DetermineTopWorkGiver;
        [ThreadStatic] private static int t8DetermineTopRejects;
        private static volatile bool enabled = true;
        private static volatile bool patched;
        private static int failureLogs;
        private static long observed;
        private static long staticLargeEligible;
        private static long tailEligible;
        private static long customTailEligible;
        private static long accelerated;
        private static long acceleratedNull;
        private static long validatorRejected;
        private static long reachRejected;
        private static long staticLargeValidatorRejected;
        private static long tailListValidatorRejected;
        private static long customTailValidatorRejected;
        private static long staticLargeReachRejected;
        private static long tailListReachRejected;
        private static long customTailReachRejected;
        private static long heavyValidatorCalls;
        private static long heavyValidatorRejects;
        private static long heavyWorkGiverResolved;
        private static long heavyWorkGiverUnresolved;
        private static long earlyKnownChecks;
        private static long earlyKnownHits;
        private static long penPrefilterCalls;
        private static long penPrefilterRejected;
        private static long penPrefilterTakeToPenRejected;
        private static long penPrefilterRoamingRejected;
        private static long targetedPrefilterCalls;
        private static long targetedPrefilterRejected;
        private static long targetedHaulCorpsesRejected;
        private static long targetedHoldingPlatformRejected;
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedFightFiresRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long targetedEarlyChecks;
        private static long targetedEarlyHits;
        private static long targetedEarlyListAdmissions;
        private static long targetedEarlyCustomAdmissions;
        private static long targetedEarlyAuthorityBypass;
        private static long actualValidatorCalls;
        private static long failures;
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyValidators = new Dictionary<string, HeavyValidatorStats>();
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyWorkGivers = new Dictionary<string, HeavyValidatorStats>();
        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, bool> TargetedPrefilterAuthorityCache = new Dictionary<Type, bool>();

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int count = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedOverload(method)) continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(JobGiverSlowSearch0419S), nameof(Prefix)) { priority = Priority.First + 100 });
                    count++;
                }
                patched = count > 0;
                Log.Message("[RimMT] Unified S4 slow-search rescue active on " + count + " ClosestThingReachable overload(s); heavy validator-tail attribution is thresholded and resolves WorkGiver identity only on >=64-reject calls.");
            }
            catch (Exception ex)
            {
                patched = false;
                Log.Warning("[RimMT] Unified S4 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value) { enabled = value; }

        private static bool IsSupportedOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                   p[2].ParameterType == typeof(ThingRequest) && p[3].ParameterType == typeof(PathEndMode) &&
                   p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) &&
                   p[6].ParameterType == typeof(Predicate<Thing>) && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static bool Prefix(IntVec3 __0, Map __1, ThingRequest __2, PathEndMode __3, TraverseParms __4,
            float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
        {
            if (!enabled || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            observed++;
            Map map = __1;
            Pawn pawn = __4.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !__0.IsValid || !__0.InBounds(map) || __5 <= 0f)
                return true;

            TraverseMode mode = __4.mode;
            if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors && mode != TraverseMode.NoPassClosedDoors)
                return true;

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L) return true;

            if (__7 != null)
            {
                long elapsedScope = Stopwatch.GetTimestamp() - scopeStart;
                if (elapsedScope < TailRescueThresholdTicks)
                {
                    if (elapsedScope < TargetedEarlyThresholdTicks || !CanUseTargetedEarly(__6)) return true;
                    targetedEarlyCustomAdmissions++;
                }
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }

            List<Thing> source;
            try { source = map.listerThings.ThingsMatching(__2); }
            catch { return true; }
            if (source == null) return true;

            int count = source.Count;
            if (count > MaxSourceCount) return true;
            if (count >= LargeSearchThreshold)
            {
                staticLargeEligible++;
                return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.StaticLarge, ref __result);
            }
            if (count < TailMinSourceCount) return true;
            long elapsedSmallList = Stopwatch.GetTimestamp() - scopeStart;
            if (elapsedSmallList < TailRescueThresholdTicks)
            {
                if (elapsedSmallList < TargetedEarlyThresholdTicks || !CanUseTargetedEarly(__6)) return true;
                targetedEarlyListAdmissions++;
            }

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
        }

        private static bool TryAccelerateList(List<Thing> source, int count, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, float maxDistance,
            Predicate<Thing> validator, RescueRoute route, ref Thing result)
        {
            try
            {
                Candidate[] candidates = EnsureScratch(count, 0);
                double maxSq = (double)maxDistance * maxDistance;
                int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) continue;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distSq = dx * dx + dz * dz;
                    if (distSq > maxSq) continue;
                    candidates[kept++] = new Candidate(thing, distSq, i);
                }
                return RunCandidates(candidates, kept, root, map, endMode, traverseParms, validator, route, ref result);
            }
            catch (Exception ex) { return Failure(ex); }
        }

        private static bool TryAccelerateCustom(IEnumerable<Thing> source, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, float maxDistance,
            Predicate<Thing> validator, RescueRoute route, ref Thing result)
        {
            try
            {
                Candidate[] candidates = EnsureScratch(256, 0);
                double maxSq = (double)maxDistance * maxDistance;
                int kept = 0;
                int sourceCount = 0;
                foreach (Thing thing in source)
                {
                    int sourceIndex = sourceCount++;
                    if (sourceCount > MaxSourceCount) return true;
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) continue;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distSq = dx * dx + dz * dz;
                    if (distSq > maxSq) continue;
                    if (kept >= candidates.Length) candidates = EnsureScratch(kept + 1, kept);
                    candidates[kept++] = new Candidate(thing, distSq, sourceIndex);
                }
                if (sourceCount < TailMinSourceCount) return true;
                return RunCandidates(candidates, kept, root, map, endMode, traverseParms, validator, route, ref result);
            }
            catch (Exception ex) { return Failure(ex); }
        }

        private static bool RunCandidates(Candidate[] candidates, int kept, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, Predicate<Thing> validator, RescueRoute route, ref Thing result)
        {
            int localValidatorCalls = 0;
            int localValidatorRejected = 0;
            int localReachRejected = 0;
            WorkGiver_Scanner resolvedScanner = TryResolveScanner(validator);
            PenPrefilterKind penKind = ResolvePenPrefilter(resolvedScanner);
            if (penKind != PenPrefilterKind.None && kept > 0)
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    penPrefilterCalls++;
                    Candidate candidate = candidates[i];
                    if (!PassPenCheapNegative(penKind, traverseParms.pawn, candidate.Thing))
                    {
                        penPrefilterRejected++;
                        if (penKind == PenPrefilterKind.TakeRoamingAnimalsToPen) penPrefilterRoamingRejected++;
                        else penPrefilterTakeToPenRejected++;
                        continue;
                    }
                    candidates[write++] = candidate;
                }
                kept = write;
            }

            TargetedPrefilterKind targetedKind = ResolveTargetedPrefilter(resolvedScanner);
            if (targetedKind != TargetedPrefilterKind.None && kept > 0)
            {
                if (!IsTargetedPrefilterAuthoritySafe(resolvedScanner))
                {
                    targetedPrefilterAuthorityBypass++;
                }
                else
                {
                    int write = 0;
                    for (int i = 0; i < kept; i++)
                    {
                        targetedPrefilterCalls++;
                        Candidate candidate = candidates[i];
                        if (!PassTargetedCheapNegative(targetedKind, traverseParms.pawn, candidate.Thing))
                        {
                            targetedPrefilterRejected++;
                            if (targetedKind == TargetedPrefilterKind.HaulCorpses) targetedHaulCorpsesRejected++;
                            else if (targetedKind == TargetedPrefilterKind.TakeEntityToHoldingPlatform) targetedHoldingPlatformRejected++;
                            else if (targetedKind == TargetedPrefilterKind.FeedHemogen) targetedFeedHemogenRejected++;
                            else if (targetedKind == TargetedPrefilterKind.VisitSickPawn) targetedVisitSickRejected++;
                            else if (targetedKind == TargetedPrefilterKind.FightFires) targetedFightFiresRejected++;
                            continue;
                        }
                        candidates[write++] = candidate;
                    }
                    kept = write;
                }
            }

            CarrierPrunerKind093T8 carrierPrunerKind;
            if (kept > 0 && CarrierMechCheapNegative093T8.TryPrepare(resolvedScanner, out carrierPrunerKind))
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    Candidate candidate = candidates[i];
                    if (CarrierMechCheapNegative093T8.Reject(carrierPrunerKind, traverseParms.pawn, candidate.Thing))
                        continue;
                    candidates[write++] = candidate;
                }
                kept = write;
            }

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
            {
                Thing thing = candidates[i].Thing;
                if (validator != null)
                {
                    localValidatorCalls++;
                    if (!validator(thing))
                    {
                        localValidatorRejected++;
                        continue;
                    }
                }
                if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
                {
                    localReachRejected++;
                    continue;
                }
                RecordRoute(route, localValidatorCalls, localValidatorRejected, localReachRejected, validator);
                result = thing;
                accelerated++;
                return false;
            }
            RecordRoute(route, localValidatorCalls, localValidatorRejected, localReachRejected, validator);
            result = null;
            accelerated++;
            acceleratedNull++;
            return false;
        }

        private static WorkGiver_Scanner TryResolveScanner(Predicate<Thing> validator)
        {
            if (validator == null) return null;
            try
            {
                object target = validator.Target;
                if (target == null) return null;
                Type targetType = target.GetType();
                FieldInfo scannerField;
                if (!ScannerFieldCache.TryGetValue(targetType, out scannerField))
                {
                    scannerField = ResolveScannerField(targetType);
                    ScannerFieldCache[targetType] = scannerField;
                }
                return scannerField == null ? null : scannerField.GetValue(target) as WorkGiver_Scanner;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsKnownHeavyWorkGiver(Predicate<Thing> validator)
        {
            earlyKnownChecks++;
            WorkGiver_Scanner scanner = TryResolveScanner(validator);
            if (scanner == null) return false;

            string key = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                ? scanner.GetType().FullName
                : scanner.def.defName;
            if (string.IsNullOrEmpty(key)) return false;

            HeavyValidatorStats stats;
            if (!HeavyWorkGivers.TryGetValue(key, out stats) || stats == null) return false;
            if (stats.Calls < EarlyKnownHeavyMinCalls || stats.Rejects < EarlyKnownHeavyMinRejects) return false;
            earlyKnownHits++;
            return true;
        }

        private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return PenPrefilterKind.None;
            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_TakeRoamingAnimalsToPen)) return PenPrefilterKind.TakeRoamingAnimalsToPen;
            if (type == typeof(WorkGiver_TakeToPen)) return PenPrefilterKind.TakeToPen;
            if (scanner is WorkGiver_TakeToPen) return PenPrefilterKind.DerivedTakeToPen;
            return PenPrefilterKind.None;
        }

        private static bool PassPenCheapNegative(PenPrefilterKind kind, Pawn worker, Thing thing)
        {
            try
            {
                Pawn animal = thing as Pawn;
                if (animal == null || animal.RaceProps == null || !animal.RaceProps.Animal) return false;
                if (worker == null) return true;
                if (animal.Position.IsForbidden(worker)) return false;
                Map map = animal.Map;
                if (map != null && map.designationManager.DesignationOn(animal, DesignationDefOf.ReleaseAnimalToWild) != null)
                    return false;

                bool roaming = animal.MentalStateDef == MentalStateDefOf.Roaming;
                if (kind == PenPrefilterKind.TakeRoamingAnimalsToPen && !roaming) return false;
                if (kind == PenPrefilterKind.TakeToPen && !roaming && animal.MentalStateDef != null) return false;
                return true;
            }
            catch
            {
                // Fail open: if any live property behaves unexpectedly, let the original validator decide.
                return true;
            }
        }

        private static bool CanUseTargetedEarly(Predicate<Thing> validator)
        {
            targetedEarlyChecks++;
            WorkGiver_Scanner scanner = TryResolveScanner(validator);
            if (scanner == null) return false;
            if (ResolveTargetedPrefilter(scanner) == TargetedPrefilterKind.None) return false;
            if (!IsTargetedPrefilterAuthoritySafe(scanner))
            {
                targetedEarlyAuthorityBypass++;
                return false;
            }
            targetedEarlyHits++;
            return true;
        }

        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return TargetedPrefilterKind.None;
            Type type = scanner.GetType();
            if (type == typeof(WorkGiver_HaulCorpses)) return TargetedPrefilterKind.HaulCorpses;
            if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform)) return TargetedPrefilterKind.TakeEntityToHoldingPlatform;
            if (scanner.def != null && scanner.def.defName == "FeedHemogen" && type == typeof(Workgiver_AdministerHemogen))
                return TargetedPrefilterKind.FeedHemogen;
            if (scanner.def != null && scanner.def.defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            if (type.FullName == "RimWorld.WorkGiver_FightFires")
                return TargetedPrefilterKind.FightFires;
            return TargetedPrefilterKind.None;
        }

        private static bool IsTargetedPrefilterAuthoritySafe(WorkGiver_Scanner scanner)
        {
            if (scanner == null) return false;
            Type type = scanner.GetType();
            bool cached;
            if (TargetedPrefilterAuthorityCache.TryGetValue(type, out cached)) return cached;

            bool safe = true;
            try
            {
                Type[] args = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
                string[] methodNames = new string[] { "HasJobOnThing", "JobOnThing" };
                for (int ni = 0; ni < methodNames.Length && safe; ni++)
                {
                    Type current = type;
                    while (current != null && typeof(WorkGiver).IsAssignableFrom(current))
                    {
                        MethodInfo method = current.GetMethod(methodNames[ni],
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, args, null);
                        if (method != null)
                        {
                            Patches info = Harmony.GetPatchInfo(method);
                            if (info != null &&
                                (info.Prefixes.Count != 0 || info.Postfixes.Count != 0 ||
                                 info.Transpilers.Count != 0 || info.Finalizers.Count != 0))
                            {
                                safe = false;
                                break;
                            }
                        }
                        current = current.BaseType;
                    }
                }
            }
            catch
            {
                safe = false;
            }

            TargetedPrefilterAuthorityCache[type] = safe;
            return safe;
        }

        private static bool PassTargetedCheapNegative(TargetedPrefilterKind kind, Pawn worker, Thing thing)
        {
            try
            {
                if (kind == TargetedPrefilterKind.HaulCorpses)
                {
                    // Vanilla WorkGiver_HaulCorpses.JobOnThing: non-corpses are rejected before
                    // any general hauling logic. Its global candidate source is the haulables lister,
                    // so this avoids entering PawnCanAutomaticallyHaulFast/HaulToStorageJob for them.
                    if (!(thing is Corpse)) return false;
                    if (worker == null || worker.Map == null) return true;

                    Pawn reserver = worker.Map.physicalInteractionReservationManager.FirstReserverOf(new LocalTargetInfo(thing));
                    if (reserver != null && reserver.RaceProps != null && reserver.RaceProps.Animal &&
                        reserver.Faction != Faction.OfPlayer)
                        return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.TakeEntityToHoldingPlatform)
                {
                    if (thing == null) return false;
                    CompHoldingPlatformTarget comp = thing.TryGetComp<CompHoldingPlatformTarget>();
                    if (comp == null || comp.targetHolder == null) return false;
                    Thing holder = comp.targetHolder;
                    if (holder.Destroyed || holder.MapHeld != thing.MapHeld) return false;

                    // EntityHolder should be present whenever the target comp is valid. If an
                    // unexpected mod state violates that invariant, fail open to Vanilla instead.
                    if (comp.EntityHolder == null) return true;
                    if (comp.EntityHolder.HeldPawn != null) return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.FeedHemogen)
                {
                    Pawn patient = thing as Pawn;
                    if (patient == null || ReferenceEquals(patient, worker)) return false;
                    Gene_Hemogen gene = patient.genes == null ? null : patient.genes.GetFirstGeneOfType<Gene_Hemogen>();
                    if (gene == null || gene.ValuePercent >= 0.95f) return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.VisitSickPawn)
                {
                    Pawn sick = thing as Pawn;
                    if (sick == null || worker == null) return false;
                    if (!sick.IsColonist || sick.IsSlave || worker.IsSlave || worker.RaceProps == null ||
                        !worker.RaceProps.Humanlike || sick.Dead || ReferenceEquals(worker, sick) ||
                        !sick.InBed() || !sick.Awake() || sick.IsForbidden(worker))
                        return false;
                    if (sick.needs == null || sick.needs.joy == null || sick.needs.joy.CurCategory > JoyCategory.VeryLow)
                        return false;
                    if (!InteractionUtility.CanReceiveInteraction(sick)) return false;
                    if (sick.needs.food != null && sick.needs.food.Starving) return false;
                    if (sick.needs.rest != null && sick.needs.rest.CurLevel <= 0.33f) return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.FightFires)
                {
                    Fire fire = thing as Fire;
                    if (fire == null || worker == null || worker.Map == null) return false;
                    if (!fire.Spawned || fire.Map != worker.Map || !fire.Position.IsValid) return false;
                    Pawn burningPawn = fire.parent as Pawn;
                    if (burningPawn != null)
                    {
                        if (ReferenceEquals(burningPawn, worker)) return false;
                        Faction workerFaction = worker.Faction;
                        Faction workerHost = worker.HostFaction;
                        Faction parentFaction = burningPawn.Faction;
                        Faction parentHost = burningPawn.HostFaction;
                        bool related = parentFaction != null && parentFaction == workerFaction;
                        if (!related && parentHost != null)
                            related = parentHost == workerFaction || parentHost == workerHost;
                        if (!related) return false;
                        if (!worker.Map.areaManager.Home[fire.Position])
                        {
                            IntVec3 a = worker.Position;
                            IntVec3 b = burningPawn.Position;
                            int manhattan = Math.Abs(a.x - b.x) + Math.Abs(a.z - b.z);
                            if (manhattan > 15) return false;
                        }
                        return true;
                    }
                    if (worker.WorkTagIsDisabled(WorkTags.Firefighting)) return false;
                    if (!worker.Map.areaManager.Home[fire.Position]) return false;
                    return true;
                }

                return true;
            }
            catch
            {
                // Fail open: original validator/Reservation/Reachability/JobOnThing remain authoritative.
                return true;
            }
        }

        private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
        {
            actualValidatorCalls += validatorCalls;
            validatorRejected += validatorRejects;
            reachRejected += reachRejects;
            switch (route)
            {
                case RescueRoute.StaticLarge:
                    staticLargeValidatorRejected += validatorRejects;
                    staticLargeReachRejected += reachRejects;
                    break;
                case RescueRoute.TailList:
                    tailListValidatorRejected += validatorRejects;
                    tailListReachRejected += reachRejects;
                    break;
                default:
                    customTailValidatorRejected += validatorRejects;
                    customTailReachRejected += reachRejects;
                    break;
            }

            if (validatorRejects < HeavyRejectThreshold || validator == null) return;
            heavyValidatorCalls++;
            heavyValidatorRejects += validatorRejects;
            RecordHeavyValidatorIdentity(validator, validatorRejects);
            RecordHeavyWorkGiverIdentity(validator, validatorRejects);
        }

        private static void RecordHeavyValidatorIdentity(Predicate<Thing> validator, int rejects)
        {
            if (HeavyValidators.Count >= MaxHeavyValidatorKeys) return;
            try
            {
                MethodInfo method = validator.Method;
                string owner = method == null || method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName;
                string name = method == null ? "<unknown>" : method.Name;
                AddHeavyStat(HeavyValidators, owner + "." + name, rejects);
            }
            catch { }
        }

        private static void RecordHeavyWorkGiverIdentity(Predicate<Thing> validator, int rejects)
        {
            try
            {
                object target = validator.Target;
                if (target == null)
                {
                    heavyWorkGiverUnresolved++;
                    return;
                }

                Type targetType = target.GetType();
                FieldInfo scannerField;
                if (!ScannerFieldCache.TryGetValue(targetType, out scannerField))
                {
                    scannerField = ResolveScannerField(targetType);
                    ScannerFieldCache[targetType] = scannerField;
                }

                WorkGiver_Scanner scanner = scannerField == null ? null : scannerField.GetValue(target) as WorkGiver_Scanner;
                if (scanner == null)
                {
                    heavyWorkGiverUnresolved++;
                    return;
                }

                heavyWorkGiverResolved++;
                if (HeavyWorkGivers.Count >= MaxHeavyWorkGiverKeys &&
                    (scanner.def == null || !HeavyWorkGivers.ContainsKey(scanner.def.defName)))
                    return;

                string defName = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                    ? scanner.GetType().FullName
                    : scanner.def.defName;
                if (t8DetermineActive && rejects > t8DetermineTopRejects)
                {
                    t8DetermineTopRejects = rejects;
                    t8DetermineTopWorkGiver = defName;
                }
                AddHeavyStat(HeavyWorkGivers, defName, rejects);
            }
            catch
            {
                heavyWorkGiverUnresolved++;
            }
        }

        private static FieldInfo ResolveScannerField(Type targetType)
        {
            if (targetType == null) return null;
            FieldInfo[] fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo fallback = null;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType))
                    return field;
                if (fallback == null && typeof(WorkGiver).IsAssignableFrom(field.FieldType))
                    fallback = field;
            }
            return fallback;
        }

        internal static void T8BeginDetermineAttribution()
        {
            t8DetermineActive = true;
            t8DetermineTopWorkGiver = null;
            t8DetermineTopRejects = 0;
        }

        internal static void T8EndDetermineAttribution(long startedTicks)
        {
            if (!t8DetermineActive) return;
            t8DetermineActive = false;
            if (startedTicks <= 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - startedTicks;
            if (elapsed <= 0L) return;
            double elapsedMs = elapsed * (1000.0 / Stopwatch.Frequency);
            if (elapsedMs < 20.0) return;
            CarrierMechCheapNegative093T8.RecordSlowDetermine(t8DetermineTopWorkGiver, t8DetermineTopRejects, elapsedMs);
        }

        private static void AddHeavyStat(Dictionary<string, HeavyValidatorStats> table, string key, int rejects)
        {
            if (string.IsNullOrEmpty(key)) key = "<unknown>";
            HeavyValidatorStats stats;
            if (!table.TryGetValue(key, out stats))
            {
                stats = new HeavyValidatorStats();
                table[key] = stats;
            }
            stats.Calls++;
            stats.Rejects += rejects;
        }

        private static Candidate[] EnsureScratch(int required, int preserveCount)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required) return current;
            int capacity = current == null ? 256 : Math.Max(256, current.Length);
            while (capacity < required && capacity < 65536) capacity <<= 1;
            if (capacity < required) capacity = required;
            Candidate[] next = new Candidate[capacity];
            if (current != null && preserveCount > 0) Array.Copy(current, next, Math.Min(preserveCount, current.Length));
            candidateScratch = next;
            return next;
        }

        private static bool Failure(Exception ex)
        {
            failures++;
            if (failureLogs++ < 4)
                Log.Warning("[RimMT] Unified S4 accelerated search failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
            return true;
        }

        internal static string Summary()
        {
            string heavy = BuildTopSummary(HeavyValidators);
            string workGivers = BuildTopSummary(HeavyWorkGivers);
            return "S4 slow-search: patched=" + patched + ", enabled=" + enabled +
                   ", observed=" + observed +
                   ", staticLargeEligible=" + staticLargeEligible +
                   ", tailEligible=" + tailEligible +
                   ", customTailEligible=" + customTailEligible +
                   ", accelerated=" + accelerated +
                   ", acceleratedNull=" + acceleratedNull +
                   ", validatorCallsActual=" + actualValidatorCalls +
                   ", validatorRejectedActual=" + validatorRejected +
                   " [static=" + staticLargeValidatorRejected + ", tailList=" + tailListValidatorRejected + ", custom=" + customTailValidatorRejected + "]" +
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected) +
                   ", reachRejected=" + reachRejected +
                   " [static=" + staticLargeReachRejected + ", tailList=" + tailListReachRejected + ", custom=" + customTailReachRejected + "]" +
                   ", heavyValidatorCalls=" + heavyValidatorCalls +
                   ", heavyValidatorRejects=" + heavyValidatorRejects +
                   ", heavyValidators=" + heavy +
                   ", heavyWorkGivers=" + workGivers +
                   ", heavyWorkGiverResolved=" + heavyWorkGiverResolved +
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
                   ", earlyKnownChecks=" + earlyKnownChecks +
                   ", earlyKnownHits=" + earlyKnownHits +
                   ", earlyKnownAdmissions=0 [policy=OFF]" +
                   ", earlyKnownPolicy=OFF" +
                   ", penPrefilterCalls=" + penPrefilterCalls +
                   ", penPrefilterRejected=" + penPrefilterRejected +
                   " [takeToPen=" + penPrefilterTakeToPenRejected + ", roaming=" + penPrefilterRoamingRejected + "]" +
                   ", targetedPrefilterCalls=" + targetedPrefilterCalls +
                   ", targetedPrefilterRejected=" + targetedPrefilterRejected +
                   " [haulCorpses=" + targetedHaulCorpsesRejected + ", holdingPlatform=" + targetedHoldingPlatformRejected +
                   ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected +
                   ", fightFires=" + targetedFightFiresRejected + "]" +
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", targetedEarly=" + (targetedEarlyListAdmissions + targetedEarlyCustomAdmissions) +
                   " [checks=" + targetedEarlyChecks + ", hits=" + targetedEarlyHits +
                   ", list=" + targetedEarlyListAdmissions + ", custom=" + targetedEarlyCustomAdmissions +
                   ", authorityBypass=" + targetedEarlyAuthorityBypass + "]" +
                   ", failures=" + failures +
                   ", staticThreshold=" + LargeSearchThreshold + ", tailThresholdMs=" + TailRescueThresholdMs +
                   ", tailMinSource=" + TailMinSourceCount + ".";
        }

        private static string BuildTopSummary(Dictionary<string, HeavyValidatorStats> table)
        {
            if (table.Count == 0) return "none";
            string k1 = null, k2 = null, k3 = null;
            HeavyValidatorStats s1 = null, s2 = null, s3 = null;
            foreach (KeyValuePair<string, HeavyValidatorStats> pair in table)
            {
                HeavyValidatorStats s = pair.Value;
                if (s == null) continue;
                if (s1 == null || s.Rejects > s1.Rejects)
                {
                    k3 = k2; s3 = s2;
                    k2 = k1; s2 = s1;
                    k1 = pair.Key; s1 = s;
                }
                else if (s2 == null || s.Rejects > s2.Rejects)
                {
                    k3 = k2; s3 = s2;
                    k2 = pair.Key; s2 = s;
                }
                else if (s3 == null || s.Rejects > s3.Rejects)
                {
                    k3 = pair.Key; s3 = s;
                }
            }
            string result = FormatHeavy(k1, s1);
            if (s2 != null) result += "; " + FormatHeavy(k2, s2);
            if (s3 != null) result += "; " + FormatHeavy(k3, s3);
            return result;
        }

        private static string FormatHeavy(string key, HeavyValidatorStats stats)
        {
            return (key ?? "<unknown>") + "(calls=" + stats.Calls + ", rejects=" + stats.Rejects + ")";
        }

        private enum RescueRoute { StaticLarge, TailList, CustomTail }
        private enum PenPrefilterKind { None, TakeToPen, TakeRoamingAnimalsToPen, DerivedTakeToPen }
        private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn, FightFires }

        private sealed class HeavyValidatorStats
        {
            internal long Calls;
            internal long Rejects;
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;
            internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
            {
                Thing = thing; DistanceSquared = distanceSquared; SourceIndex = sourceIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();
            public int Compare(Candidate a, Candidate b)
            {
                int d = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return d != 0 ? d : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}









