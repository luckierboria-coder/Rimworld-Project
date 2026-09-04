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
    /// V0.9.5 recurring-hot JobGiver tail slicer.
    ///
    /// Scope is deliberately narrow: exact JobGiver_Work validator closures, reachable/non-prioritized
    /// thing-only scanners, and stable IList-backed candidate sources. The first slow calls always run
    /// through Vanilla and are attributed by JobGiverTailTelemetry094. Only scanners that have already
    /// demonstrated recurring tail cost are admitted.
    ///
    /// A slice evaluates only the original validator on the main thread and remembers candidates that
    /// passed. If the slice budget expires, lower-priority WorkGivers are suppressed for the remainder
    /// of that synchronous TryIssueJobPackage call and the same scanner resumes on a later package.
    /// After filtering completes, final target selection is performed live by Vanilla's
    /// ClosestThing_Global_Reachable with the original validator and Reachability authority intact.
    /// No Verse state is read or written from worker threads and no Job is fabricated or cached.
    /// </summary>
    internal static class ResumableJobGiver095
    {
        private const int MinSourceCount = 16;
        private const int MaxSourceCount = 8192;
        private const int MaxStates = 128;
        private const int MaxStateAgeTicks = 300;
        private const int BudgetCheckMask = 3;

        private static readonly Dictionary<Pawn, ResumeState> States = new Dictionary<Pawn, ResumeState>();
        private static readonly Dictionary<MethodBase, bool> AuthorityCache = new Dictionary<MethodBase, bool>();

        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static bool suspendedThisPackage;

        private static bool patched;
        private static long observed;
        private static long hotAdmissions;
        private static long statesCreated;
        private static long stateReplacements;
        private static long resumes;
        private static long suspensions;
        private static long completed;
        private static long completedNull;
        private static long candidatesChecked;
        private static long validatorRejected;
        private static long sourceInvalidations;
        private static long staleInvalidations;
        private static long capacityBypass;
        private static long customEnumerableBypass;
        private static long shapeBypass;
        private static long authorityBypass;
        private static long priorityBlocks;
        private static long failures;
        private static long totalSlices;
        private static long maxSliceTicks;
        private static long finalSearchCalls;
        private static long finalSearchOver5;
        private static long finalSearchOver10;
        private static long finalSearchOver20;
        private static long maxFinalSearchTicks;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase package = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                MethodBase canUse = AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver");
                if (package == null || canUse == null)
                    throw new MissingMethodException("JobGiver_Work package/control methods were not found");

                harmony.Patch(package,
                    prefix: new HarmonyMethod(typeof(ResumableJobGiver095), nameof(PackagePrefix)) { priority = Priority.First + 50 },
                    finalizer: new HarmonyMethod(typeof(ResumableJobGiver095), nameof(PackageFinalizer)) { priority = Priority.Last });
                harmony.Patch(canUse,
                    postfix: new HarmonyMethod(typeof(ResumableJobGiver095), nameof(PawnCanUsePostfix)) { priority = Priority.Last });

                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int count = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedOverload(method)) continue;
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(ResumableJobGiver095), nameof(ClosestPrefix)) { priority = Priority.First + 200 });
                    count++;
                }

                patched = count > 0;
                Log.Message("[RimMT] V0.9.5 Resumable JobGiver installed on " + count +
                    " ClosestThingReachable overload(s): recurring-hot exact JobGiver_Work validators are main-thread sliced; final Reachability/validator/Job authority remains live.");
            }
            catch (Exception ex)
            {
                patched = false;
                Log.Warning("[RimMT] V0.9.5 Resumable JobGiver failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsSupportedOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                   p[2].ParameterType == typeof(ThingRequest) && p[3].ParameterType == typeof(PathEndMode) &&
                   p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) &&
                   p[6].ParameterType == typeof(Predicate<Thing>) && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static void PackagePrefix(Pawn __0)
        {
            currentPawn = __0;
            suspendedThisPackage = false;
            if (States.Count > MaxStates)
                PurgeInvalidStates();
        }

        public static Exception PackageFinalizer(Exception __exception)
        {
            currentPawn = null;
            suspendedThisPackage = false;
            return __exception;
        }

        public static void PawnCanUsePostfix(Pawn __0, ref bool __result)
        {
            if (!suspendedThisPackage || currentPawn == null || !ReferenceEquals(__0, currentPawn)) return;
            __result = false;
            priorityBlocks++;
        }

        public static bool ClosestPrefix(MethodBase __originalMethod, IntVec3 __0, Map __1, ThingRequest __2,
            PathEndMode __3, TraverseParms __4, float __5, Predicate<Thing> __6,
            IEnumerable<Thing> __7, ref Thing __result)
        {
            if (!patched || suspendedThisPackage || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            observed++;
            Pawn pawn = __4.pawn;
            if (pawn == null || currentPawn == null || !ReferenceEquals(pawn, currentPawn) ||
                __1 == null || __1.Disposed || !pawn.Spawned || pawn.Map != __1 ||
                !__0.IsValid || !__0.InBounds(__1) || __5 <= 0f || __6 == null)
            {
                shapeBypass++;
                return true;
            }

            WorkGiver_Scanner scanner = ResolveExactJobGiverScanner(__6);
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                return true;
            }

            if (!JobGiverTailTelemetry094.IsRecurringHot(scanner))
                return true;

            hotAdmissions++;
            if (!IsAuthoritySafe(__originalMethod))
            {
                authorityBypass++;
                return true;
            }

            IList<Thing> source;
            if (!TryGetStableSource(__1, __2, __7, out source))
            {
                if (__7 != null) customEnumerableBypass++;
                else shapeBypass++;
                return true;
            }

            int count;
            try { count = source.Count; }
            catch
            {
                sourceInvalidations++;
                return true;
            }
            if (count < MinSourceCount) return true;
            if (count > MaxSourceCount)
            {
                capacityBypass++;
                return true;
            }

            ResumeState existing;
            States.TryGetValue(pawn, out existing);
            ResumeState state = null;
            if (existing != null && ReferenceEquals(existing.Scanner, scanner))
            {
                if (!ValidateState(existing, source, pawn, __1, __0))
                {
                    States.Remove(pawn);
                    sourceInvalidations++;
                }
                else
                {
                    state = existing;
                    resumes++;
                }
            }

            if (state == null)
            {
                state = CreateState(pawn, scanner, source, __1, __0, __5);
                if (state == null) return true;
            }

            try
            {
                long sliceStart = Stopwatch.GetTimestamp();
                long budgetTicks = SliceBudgetTicks();
                int processedSinceCheck = 0;

                while (state.NextIndex < state.Members.Length)
                {
                    Thing thing = state.Members[state.NextIndex++];
                    if (thing != null && thing.Spawned && thing.Map == __1)
                    {
                        IntVec3 pos = thing.Position;
                        if (pos.IsValid)
                        {
                            long dx = (long)pos.x - __0.x;
                            long dz = (long)pos.z - __0.z;
                            double maxSq = (double)__5 * __5;
                            if ((double)(dx * dx + dz * dz) <= maxSq)
                            {
                                candidatesChecked++;
                                if (__6(thing)) state.Passed.Add(thing);
                                else validatorRejected++;
                            }
                        }
                    }

                    processedSinceCheck++;
                    if ((processedSinceCheck & BudgetCheckMask) == 0 && state.NextIndex < state.Members.Length &&
                        Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
                    {
                        state.Slices++;
                        totalSlices++;
                        long sliceTicks = Stopwatch.GetTimestamp() - sliceStart;
                        if (sliceTicks > maxSliceTicks) maxSliceTicks = sliceTicks;
                        StoreState(pawn, state, existing);
                        suspendedThisPackage = true;
                        suspensions++;
                        __result = null;
                        return false;
                    }
                }

                state.Slices++;
                totalSlices++;
                long completedSliceTicks = Stopwatch.GetTimestamp() - sliceStart;
                if (completedSliceTicks > maxSliceTicks) maxSliceTicks = completedSliceTicks;

                States.Remove(pawn);
                completed++;

                if (state.Passed.Count == 0)
                {
                    completedNull++;
                    __result = null;
                    return false;
                }

                // Final authority remains live and synchronous. The original validator is deliberately
                // supplied again so candidates that became invalid during earlier slices are rejected.
                long finalStart = Stopwatch.GetTimestamp();
                Thing final = GenClosest.ClosestThing_Global_Reachable(__0, __1, state.Passed, __3, __4, __5, __6, null);
                long finalTicks = Stopwatch.GetTimestamp() - finalStart;
                finalSearchCalls++;
                if (finalTicks > maxFinalSearchTicks) maxFinalSearchTicks = finalTicks;
                if (finalTicks >= Stopwatch.Frequency * 5L / 1000L) finalSearchOver5++;
                if (finalTicks >= Stopwatch.Frequency * 10L / 1000L) finalSearchOver10++;
                if (finalTicks >= Stopwatch.Frequency * 20L / 1000L) finalSearchOver20++;
                if (final == null) completedNull++;
                __result = final;
                return false;
            }
            catch (Exception ex)
            {
                failures++;
                States.Remove(pawn);
                if (failures <= 4)
                    Log.Warning("[RimMT] V0.9.5 resumable slice failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static WorkGiver_Scanner ResolveExactJobGiverScanner(Predicate<Thing> validator)
        {
            if (validator == null) return null;
            try
            {
                MethodInfo method = validator.Method;
                Type closure = method == null ? null : method.DeclaringType;
                if (closure == null || closure.DeclaringType != typeof(JobGiver_Work)) return null;
                return JobGiverTailTelemetry094.TryResolveScanner(validator);
            }
            catch { return null; }
        }

        private static bool IsSupportedScanner(WorkGiver_Scanner scanner)
        {
            if (scanner == null || scanner.def == null) return false;
            try
            {
                if (!scanner.def.scanThings || scanner.def.scanCells) return false;
                if (scanner.Prioritized || scanner.AllowUnreachable) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetStableSource(Map map, ThingRequest request, IEnumerable<Thing> custom,
            out IList<Thing> source)
        {
            source = null;
            try
            {
                if (custom != null)
                {
                    source = custom as IList<Thing>;
                    return source != null;
                }
                if (request.IsUndefined) return false;
                source = map.listerThings.ThingsMatching(request);
                return source != null;
            }
            catch
            {
                source = null;
                return false;
            }
        }

        private static ResumeState CreateState(Pawn pawn, WorkGiver_Scanner scanner, IList<Thing> source,
            Map map, IntVec3 root, float maxDistance)
        {
            try
            {
                int count = source.Count;
                Thing[] members = new Thing[count];
                for (int i = 0; i < count; i++) members[i] = source[i];
                ResumeState state = new ResumeState
                {
                    Pawn = pawn,
                    Scanner = scanner,
                    Map = map,
                    Root = root,
                    MaxDistance = maxDistance,
                    CreatedTick = CurrentGameTick(),
                    Members = members,
                    Passed = new List<Thing>(Math.Min(count, 256)),
                    NextIndex = 0,
                    Slices = 0
                };
                statesCreated++;
                return state;
            }
            catch
            {
                return null;
            }
        }

        private static bool ValidateState(ResumeState state, IList<Thing> source, Pawn pawn, Map map, IntVec3 root)
        {
            if (state == null || !ReferenceEquals(state.Pawn, pawn) || !ReferenceEquals(state.Map, map) ||
                state.Root != root || state.Members == null)
                return false;

            int now = CurrentGameTick();
            if (now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
            {
                staleInvalidations++;
                return false;
            }

            int count;
            try { count = source.Count; }
            catch { return false; }
            if (count != state.Members.Length) return false;
            for (int i = 0; i < count; i++)
                if (!ReferenceEquals(source[i], state.Members[i])) return false;
            return true;
        }

        private static void StoreState(Pawn pawn, ResumeState state, ResumeState prior)
        {
            ResumeState current;
            if (States.TryGetValue(pawn, out current) && !ReferenceEquals(current, state))
                stateReplacements++;
            else if (prior != null && !ReferenceEquals(prior, state))
                stateReplacements++;
            States[pawn] = state;
        }

        private static bool IsAuthoritySafe(MethodBase method)
        {
            if (method == null) return false;
            bool cached;
            if (AuthorityCache.TryGetValue(method, out cached)) return cached;

            bool safe = true;
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info != null)
                {
                    safe = OwnedByRimMT(info.Prefixes) && OwnedByRimMT(info.Postfixes) &&
                           OwnedByRimMT(info.Transpilers) && OwnedByRimMT(info.Finalizers);
                }
            }
            catch { safe = false; }
            AuthorityCache[method] = safe;
            return safe;
        }

        private static bool OwnedByRimMT(IEnumerable<Patch> patches)
        {
            if (patches == null) return true;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static long SliceBudgetTicks()
        {
            double ms;
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: ms = 5.0; break;
                case LoadPressure.Normal: ms = 4.0; break;
                case LoadPressure.High: ms = 2.5; break;
                default: ms = 1.5; break;
            }
            return Math.Max(1L, (long)(Stopwatch.Frequency * ms / 1000.0));
        }

        private static int CurrentGameTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private static void PurgeInvalidStates()
        {
            List<Pawn> remove = null;
            int now = CurrentGameTick();
            foreach (KeyValuePair<Pawn, ResumeState> pair in States)
            {
                Pawn pawn = pair.Key;
                ResumeState state = pair.Value;
                bool invalid = pawn == null || state == null || pawn.Destroyed || !pawn.Spawned ||
                    state.Map == null || state.Map.Disposed || pawn.Map != state.Map;
                if (!invalid && now >= 0 && state.CreatedTick >= 0 && now - state.CreatedTick > MaxStateAgeTicks)
                    invalid = true;
                if (!invalid) continue;
                if (remove == null) remove = new List<Pawn>();
                remove.Add(pawn);
            }
            if (remove == null) return;
            for (int i = 0; i < remove.Count; i++) States.Remove(remove[i]);
        }

        internal static string Summary()
        {
            double maxSliceUs = maxSliceTicks * 1000000.0 / Stopwatch.Frequency;
            double maxFinalUs = maxFinalSearchTicks * 1000000.0 / Stopwatch.Frequency;
            double avgSlices = completed <= 0 ? 0.0 : totalSlices / (double)completed;
            return "Resumable JobGiver V0.9.5: patched=" + patched +
                   ", observed=" + observed +
                   ", hotAdmissions=" + hotAdmissions +
                   ", activeStates=" + States.Count +
                   ", statesCreated=" + statesCreated +
                   ", replacements=" + stateReplacements +
                   ", resumes=" + resumes +
                   ", suspensions=" + suspensions +
                   ", completed=" + completed +
                   ", completedNull=" + completedNull +
                   ", candidatesChecked=" + candidatesChecked +
                   ", validatorRejected=" + validatorRejected +
                   ", sourceInvalidations=" + sourceInvalidations +
                   ", staleInvalidations=" + staleInvalidations +
                   ", capacityBypass=" + capacityBypass +
                   ", customEnumerableBypass=" + customEnumerableBypass +
                   ", shapeBypass=" + shapeBypass +
                   ", authorityBypass=" + authorityBypass +
                   ", priorityBlocks=" + priorityBlocks +
                   ", avgSlicesPerComplete=" + avgSlices.ToString("F2") +
                   ", maxSliceUs=" + maxSliceUs.ToString("F1") +
                   ", finalSearchCalls=" + finalSearchCalls +
                   " [>5ms=" + finalSearchOver5 + ", >10ms=" + finalSearchOver10 + ", >20ms=" + finalSearchOver20 + "]" +
                   ", maxFinalSearchUs=" + maxFinalUs.ToString("F1") +
                   ", failures=" + failures +
                   ", budgetsMs=[Low=5.0,Normal=4.0,High=2.5,Critical=1.5]";
        }

        private sealed class ResumeState
        {
            internal Pawn Pawn;
            internal WorkGiver_Scanner Scanner;
            internal Map Map;
            internal IntVec3 Root;
            internal float MaxDistance;
            internal int CreatedTick;
            internal Thing[] Members;
            internal List<Thing> Passed;
            internal int NextIndex;
            internal int Slices;
        }
    }
}
