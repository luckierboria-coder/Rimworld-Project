using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T33-A Candidate Admission / Rejection Fabric shadow.
    ///
    /// Purpose:
    /// - measure whether an exact negative JobGiver_Work validator result can remain stable
    ///   across later Job Search packages for the same pawn + validator method + scanner + Thing;
    /// - estimate the replayable population under a deliberately conservative mutable-state
    ///   envelope before any cross-package result is ever allowed to skip live HasJobOnThing.
    ///
    /// T33-A is measurement-only. It never writes __result, never skips the original validator,
    /// never creates Jobs/reservations and never changes candidate order.
    ///
    /// The first live false stores cheap pawn/Thing fingerprints only. A later cross-package
    /// exact repeat with the same cheap envelope is still live and lazily primes the outer
    /// IsForbidden state. Only later repeats with a stable cheap envelope + stable Forbidden
    /// state are counted as replayable shadow candidates. Every such candidate remains fully
    /// live; false=>false counts as a shadow match and false=>true counts as a mismatch.
    /// </summary>
    internal static class CandidateAdmissionFabric093T33A
    {
        internal const string FeatureId = "ai.candidateAdmissionFabric";

        private const int Capacity = 65536;
        private const int MaxScannerStats = 256;

        private static readonly Dictionary<PersistentKey, NegativeEntry> Entries =
            new Dictionary<PersistentKey, NegativeEntry>();
        private static readonly Dictionary<Type, ScannerAccessor> ScannerAccessors =
            new Dictionary<Type, ScannerAccessor>();
        private static readonly object ScannerAccessorLock = new object();
        private static readonly Dictionary<WorkGiver_Scanner, ScannerStats> ScannerStatistics =
            new Dictionary<WorkGiver_Scanner, ScannerStats>();

        private static bool installed;
        private static int validatorMethodsPatched;
        private static int validatorMethodsSkippedForeign;
        private static int installFailures;

        private static long observed;
        private static long liveEligible;
        private static long stores;
        private static long samePackageBypass;
        private static long crossPackageRepeats;
        private static long cheapStableRepeats;
        private static long cheapFingerprintBypass;
        private static long scannerResolveBypass;
        private static long runOriginalBypass;

        private static long lazyPrimeAttempts;
        private static long lazyPrimeSuccess;
        private static long lazyPrimePositive;
        private static long lazyPrimeUnstable;
        private static long forbiddenReads;
        private static long forbiddenReadFailures;
        private static long forbiddenStateBypass;

        private static long replayableCandidates;
        private static long shadowMatches;
        private static long shadowMismatches;

        private static long capacityClears;
        private static long entriesCleared;

        private static long packageGap1;
        private static long packageGap2To4;
        private static long packageGap5To16;
        private static long packageGap17To64;
        private static long packageGap65Plus;

        private static long tickAgeLe60;
        private static long tickAge61To250;
        private static long tickAge251To1000;
        private static long tickAgeGt1000;
        private static long tickAgeUnknown;

        internal static bool Installed { get { return installed; } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            try
            {
                List<Type> nested = new List<Type>();
                CollectNestedTypes(typeof(JobGiver_Work), nested);
                HashSet<MethodBase> unique = new HashSet<MethodBase>();

                for (int i = 0; i < nested.Count; i++)
                {
                    Type type = nested[i];
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(
                            BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int m = 0; m < methods.Length; m++)
                    {
                        MethodInfo method = methods[m];
                        if (method == null || method.ReturnType != typeof(bool) ||
                            method.Name.IndexOf("Validator", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        ParameterInfo[] p = method.GetParameters();
                        if (p.Length != 1 || !typeof(Thing).IsAssignableFrom(p[0].ParameterType))
                            continue;
                        if (!unique.Add(method))
                            continue;

                        if (HasForeignPatches(method))
                        {
                            validatorMethodsSkippedForeign++;
                            continue;
                        }

                        try
                        {
                            // T21 runs at First+250. T33-A intentionally runs after it so
                            // package-local authoritative negatives remain T21's responsibility.
                            harmony.Patch(
                                method,
                                prefix: new HarmonyMethod(
                                    typeof(CandidateAdmissionFabric093T33A),
                                    nameof(ValidatorPrefix))
                                { priority = Priority.First + 200 },
                                postfix: new HarmonyMethod(
                                    typeof(CandidateAdmissionFabric093T33A),
                                    nameof(ValidatorPostfix))
                                { priority = Priority.Last - 300 });
                            validatorMethodsPatched++;
                        }
                        catch
                        {
                            installFailures++;
                        }
                    }
                }

                installed = validatorMethodsPatched > 0;
                Log.Message(
                    "[RimMT] T33-A Candidate Admission Shadow installed=" + installed +
                    ", validatorMethods=" + validatorMethodsPatched +
                    ", skippedForeign=" + validatorMethodsSkippedForeign +
                    ". Measurement-only cross-package exact negative stability census; " +
                    "no candidate/result/order mutation.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning(
                    "[RimMT] T33-A Candidate Admission Shadow failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void ValidatorPrefix(
            object __instance,
            MethodBase __originalMethod,
            object[] __args,
            bool __runOriginal,
            ref CallState __state)
        {
            __state = default(CallState);
            Interlocked.Increment(ref observed);

            if (!__runOriginal)
            {
                Interlocked.Increment(ref runOriginalBypass);
                return;
            }

            if (!JobSearchPackageContext093T28.InScope ||
                !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing ||
                __originalMethod == null ||
                __args == null ||
                __args.Length != 1)
                return;

            Pawn pawn = JobSearchPackageContext093T28.CurrentPawn;
            Thing thing = __args[0] as Thing;
            if (pawn == null || thing == null)
                return;

            WorkGiver_Scanner scanner = ResolveScanner(__instance);
            if (scanner == null)
            {
                Interlocked.Increment(ref scannerResolveBypass);
                return;
            }

            Interlocked.Increment(ref liveEligible);

            long generation = JobSearchPackageContext093T28.CurrentGeneration;
            int tick = CurrentGameTick();
            PersistentKey key = new PersistentKey(
                pawn, __originalMethod, scanner, thing);

            NegativeEntry entry;
            if (!Entries.TryGetValue(key, out entry))
            {
                __state = CallState.ForStore(
                    key, pawn, scanner, thing, generation, tick);
                return;
            }

            if (entry.LastGeneration == generation)
            {
                Interlocked.Increment(ref samePackageBypass);
                return;
            }

            Interlocked.Increment(ref crossPackageRepeats);
            RecordAge(entry, generation, tick);

            PawnFingerprint pawnFingerprint = PawnFingerprint.Capture(pawn);
            ThingFingerprint thingFingerprint = ThingFingerprint.CaptureCheap(thing);

            if (!entry.PawnFingerprint.Equals(pawnFingerprint) ||
                !entry.ThingFingerprint.Equals(thingFingerprint))
            {
                Entries.Remove(key);
                Interlocked.Increment(ref cheapFingerprintBypass);
                __state = CallState.ForStoreWithFingerprints(
                    key, pawn, scanner, thing, generation, tick,
                    pawnFingerprint, thingFingerprint);
                return;
            }

            Interlocked.Increment(ref cheapStableRepeats);

            if (!entry.Primed)
            {
                bool forbidden;
                if (!TryReadForbidden(pawn, thing, out forbidden))
                {
                    Entries.Remove(key);
                    Interlocked.Increment(ref forbiddenReadFailures);
                    __state = CallState.ForStoreWithFingerprints(
                        key, pawn, scanner, thing, generation, tick,
                        pawnFingerprint, thingFingerprint);
                    return;
                }

                Interlocked.Increment(ref lazyPrimeAttempts);
                __state = CallState.ForPrime(
                    key, entry, pawn, scanner, thing, generation, tick,
                    pawnFingerprint, thingFingerprint, forbidden);
                return;
            }

            bool currentForbidden;
            if (!TryReadForbidden(pawn, thing, out currentForbidden))
            {
                Entries.Remove(key);
                Interlocked.Increment(ref forbiddenReadFailures);
                __state = CallState.ForStoreWithFingerprints(
                    key, pawn, scanner, thing, generation, tick,
                    pawnFingerprint, thingFingerprint);
                return;
            }

            if (currentForbidden != entry.Forbidden)
            {
                Entries.Remove(key);
                Interlocked.Increment(ref forbiddenStateBypass);
                __state = CallState.ForStoreWithFingerprints(
                    key, pawn, scanner, thing, generation, tick,
                    pawnFingerprint, thingFingerprint);
                return;
            }

            Interlocked.Increment(ref replayableCandidates);
            RecordScannerCandidate(scanner);
            __state = CallState.ForShadow(
                key, entry, pawn, scanner, thing, generation, tick,
                pawnFingerprint, thingFingerprint);
        }

        public static void ValidatorPostfix(bool __result, CallState __state)
        {
            if (!__state.Active)
                return;

            if (!JobSearchPackageContext093T28.InScope)
                return;

            long generation = JobSearchPackageContext093T28.CurrentGeneration;
            if (generation == 0L || generation != __state.Generation)
                return;

            if (__state.Prime)
            {
                HandlePrime(__result, __state);
                return;
            }

            if (__state.Shadow)
            {
                if (!__result)
                {
                    Interlocked.Increment(ref shadowMatches);
                    RecordScannerMatch(__state.Scanner);

                    NegativeEntry entry = __state.Entry;
                    if (entry != null)
                    {
                        entry.LastGeneration = __state.Generation;
                        entry.LastGameTick = __state.GameTick;
                        entry.PawnFingerprint = __state.PawnFingerprint;
                        entry.ThingFingerprint = __state.ThingFingerprint;
                    }
                }
                else
                {
                    Entries.Remove(__state.Key);
                    Interlocked.Increment(ref shadowMismatches);
                    RecordScannerMismatch(__state.Scanner);
                }
                return;
            }

            if (__state.Store)
            {
                if (!__result)
                {
                    StoreNegative(
                        __state.Key,
                        __state.Pawn,
                        __state.Scanner,
                        __state.Thing,
                        __state.Generation,
                        __state.GameTick,
                        __state.HasCapturedFingerprints
                            ? __state.PawnFingerprint
                            : PawnFingerprint.Capture(__state.Pawn),
                        __state.HasCapturedFingerprints
                            ? __state.ThingFingerprint
                            : ThingFingerprint.CaptureCheap(__state.Thing));
                }
                else
                {
                    Entries.Remove(__state.Key);
                }
            }
        }

        private static void HandlePrime(bool result, CallState state)
        {
            NegativeEntry entry = state.Entry;
            if (entry == null)
                return;

            if (result)
            {
                Entries.Remove(state.Key);
                Interlocked.Increment(ref lazyPrimePositive);
                RecordScannerMismatch(state.Scanner);
                return;
            }

            PawnFingerprint pawnAfter = PawnFingerprint.Capture(state.Pawn);
            ThingFingerprint thingAfter = ThingFingerprint.CaptureCheap(state.Thing);

            if (!state.PawnFingerprint.Equals(pawnAfter) ||
                !state.ThingFingerprint.Equals(thingAfter))
            {
                Entries.Remove(state.Key);
                Interlocked.Increment(ref lazyPrimeUnstable);
                return;
            }

            bool forbiddenAfter;
            if (!TryReadForbidden(state.Pawn, state.Thing, out forbiddenAfter))
            {
                Entries.Remove(state.Key);
                Interlocked.Increment(ref forbiddenReadFailures);
                return;
            }

            if (forbiddenAfter != state.ForbiddenBefore)
            {
                Entries.Remove(state.Key);
                Interlocked.Increment(ref lazyPrimeUnstable);
                return;
            }

            entry.Primed = true;
            entry.Forbidden = forbiddenAfter;
            entry.LastGeneration = state.Generation;
            entry.LastGameTick = state.GameTick;
            entry.PawnFingerprint = pawnAfter;
            entry.ThingFingerprint = thingAfter;
            Interlocked.Increment(ref lazyPrimeSuccess);
        }

        private static void StoreNegative(
            PersistentKey key,
            Pawn pawn,
            WorkGiver_Scanner scanner,
            Thing thing,
            long generation,
            int tick,
            PawnFingerprint pawnFingerprint,
            ThingFingerprint thingFingerprint)
        {
            if (pawn == null || scanner == null || thing == null || generation == 0L)
                return;

            if (Entries.Count >= Capacity)
            {
                int count = Entries.Count;
                Entries.Clear();
                Interlocked.Increment(ref capacityClears);
                Interlocked.Add(ref entriesCleared, count);
            }

            NegativeEntry entry;
            if (!Entries.TryGetValue(key, out entry))
            {
                entry = new NegativeEntry();
                Entries.Add(key, entry);
                Interlocked.Increment(ref stores);
            }

            entry.LastGeneration = generation;
            entry.LastGameTick = tick;
            entry.PawnFingerprint = pawnFingerprint;
            entry.ThingFingerprint = thingFingerprint;
            entry.Primed = false;
            entry.Forbidden = false;
        }

        private static bool TryReadForbidden(Pawn pawn, Thing thing, out bool forbidden)
        {
            forbidden = false;
            try
            {
                forbidden = pawn != null && thing != null && thing.IsForbidden(pawn);
                Interlocked.Increment(ref forbiddenReads);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int CurrentGameTick()
        {
            try
            {
                return Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
            }
            catch
            {
                return -1;
            }
        }

        private static void RecordAge(NegativeEntry entry, long generation, int tick)
        {
            long gap = generation - entry.LastGeneration;
            if (gap <= 1L) Interlocked.Increment(ref packageGap1);
            else if (gap <= 4L) Interlocked.Increment(ref packageGap2To4);
            else if (gap <= 16L) Interlocked.Increment(ref packageGap5To16);
            else if (gap <= 64L) Interlocked.Increment(ref packageGap17To64);
            else Interlocked.Increment(ref packageGap65Plus);

            if (tick < 0 || entry.LastGameTick < 0)
            {
                Interlocked.Increment(ref tickAgeUnknown);
                return;
            }

            int age = tick - entry.LastGameTick;
            if (age <= 60) Interlocked.Increment(ref tickAgeLe60);
            else if (age <= 250) Interlocked.Increment(ref tickAge61To250);
            else if (age <= 1000) Interlocked.Increment(ref tickAge251To1000);
            else Interlocked.Increment(ref tickAgeGt1000);
        }

        private static void RecordScannerCandidate(WorkGiver_Scanner scanner)
        {
            ScannerStats stats = GetScannerStats(scanner);
            if (stats != null) stats.Candidates++;
        }

        private static void RecordScannerMatch(WorkGiver_Scanner scanner)
        {
            ScannerStats stats = GetScannerStats(scanner);
            if (stats != null) stats.Matches++;
        }

        private static void RecordScannerMismatch(WorkGiver_Scanner scanner)
        {
            ScannerStats stats = GetScannerStats(scanner);
            if (stats != null) stats.Mismatches++;
        }

        private static ScannerStats GetScannerStats(WorkGiver_Scanner scanner)
        {
            if (scanner == null)
                return null;

            ScannerStats stats;
            if (ScannerStatistics.TryGetValue(scanner, out stats))
                return stats;

            if (ScannerStatistics.Count >= MaxScannerStats)
                return null;

            stats = new ScannerStats();
            ScannerStatistics.Add(scanner, stats);
            return stats;
        }

        private static string BuildTopScannerSummary()
        {
            WorkGiver_Scanner first = null, second = null, third = null;
            ScannerStats firstStats = null, secondStats = null, thirdStats = null;

            foreach (KeyValuePair<WorkGiver_Scanner, ScannerStats> pair in ScannerStatistics)
            {
                ScannerStats stats = pair.Value;
                if (stats == null) continue;

                if (firstStats == null || stats.Candidates > firstStats.Candidates)
                {
                    third = second; thirdStats = secondStats;
                    second = first; secondStats = firstStats;
                    first = pair.Key; firstStats = stats;
                }
                else if (secondStats == null || stats.Candidates > secondStats.Candidates)
                {
                    third = second; thirdStats = secondStats;
                    second = pair.Key; secondStats = stats;
                }
                else if (thirdStats == null || stats.Candidates > thirdStats.Candidates)
                {
                    third = pair.Key; thirdStats = stats;
                }
            }

            string result = FormatScanner(first, firstStats);
            if (secondStats != null) result += "; " + FormatScanner(second, secondStats);
            if (thirdStats != null) result += "; " + FormatScanner(third, thirdStats);
            return string.IsNullOrEmpty(result) ? "none" : result;
        }

        private static string FormatScanner(WorkGiver_Scanner scanner, ScannerStats stats)
        {
            if (scanner == null || stats == null) return null;
            string name;
            try
            {
                name = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                    ? scanner.GetType().FullName
                    : scanner.def.defName;
            }
            catch
            {
                name = scanner.GetType().FullName;
            }

            return name + "(candidates=" + stats.Candidates +
                ", matches=" + stats.Matches +
                ", mismatches=" + stats.Mismatches + ")";
        }

        private static WorkGiver_Scanner ResolveScanner(object closure)
        {
            if (closure == null) return null;
            Type type = closure.GetType();
            ScannerAccessor accessor;

            lock (ScannerAccessorLock)
            {
                if (!ScannerAccessors.TryGetValue(type, out accessor))
                {
                    accessor = ScannerAccessor.Build(type);
                    ScannerAccessors[type] = accessor;
                }
            }

            return accessor == null ? null : accessor.Read(closure);
        }

        private static void CollectNestedTypes(Type parent, List<Type> output)
        {
            Type[] nested;
            try
            {
                nested = parent.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return;
            }

            for (int i = 0; i < nested.Length; i++)
            {
                Type type = nested[i];
                if (type == null) continue;
                output.Add(type);
                CollectNestedTypes(type, output);
            }
        }

        private static bool HasForeignPatches(MethodBase method)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return false;
                return HasForeign(info.Prefixes) ||
                    HasForeign(info.Postfixes) ||
                    HasForeign(info.Transpilers) ||
                    HasForeign(info.Finalizers);
            }
            catch
            {
                return true;
            }
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(
                    patch.owner,
                    RimMTBootstrap.HarmonyId,
                    StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        internal static string Summary()
        {
            return "T33-A candidate admission shadow: installed=" + installed +
                ", validatorMethods=" + validatorMethodsPatched +
                ", validatorForeignSkipped=" + validatorMethodsSkippedForeign +
                ", observed=" + Interlocked.Read(ref observed) +
                ", liveEligible=" + Interlocked.Read(ref liveEligible) +
                ", entries=" + Entries.Count + "/" + Capacity +
                ", stores=" + Interlocked.Read(ref stores) +
                ", samePackageBypass=" + Interlocked.Read(ref samePackageBypass) +
                ", crossPackageRepeats=" + Interlocked.Read(ref crossPackageRepeats) +
                ", cheapStable=" + Interlocked.Read(ref cheapStableRepeats) +
                ", cheapFingerprintBypass=" + Interlocked.Read(ref cheapFingerprintBypass) +
                ", lazyPrime[attempts/success/positive/unstable]=" +
                Interlocked.Read(ref lazyPrimeAttempts) + "/" +
                Interlocked.Read(ref lazyPrimeSuccess) + "/" +
                Interlocked.Read(ref lazyPrimePositive) + "/" +
                Interlocked.Read(ref lazyPrimeUnstable) +
                ", forbidden[reads/readFailures/stateBypass]=" +
                Interlocked.Read(ref forbiddenReads) + "/" +
                Interlocked.Read(ref forbiddenReadFailures) + "/" +
                Interlocked.Read(ref forbiddenStateBypass) +
                ", replayable[candidates/matches/mismatches]=" +
                Interlocked.Read(ref replayableCandidates) + "/" +
                Interlocked.Read(ref shadowMatches) + "/" +
                Interlocked.Read(ref shadowMismatches) +
                ", packageGap[1/2-4/5-16/17-64/65+]=" +
                Interlocked.Read(ref packageGap1) + "/" +
                Interlocked.Read(ref packageGap2To4) + "/" +
                Interlocked.Read(ref packageGap5To16) + "/" +
                Interlocked.Read(ref packageGap17To64) + "/" +
                Interlocked.Read(ref packageGap65Plus) +
                ", tickAge[<=60/61-250/251-1000/>1000/unknown]=" +
                Interlocked.Read(ref tickAgeLe60) + "/" +
                Interlocked.Read(ref tickAge61To250) + "/" +
                Interlocked.Read(ref tickAge251To1000) + "/" +
                Interlocked.Read(ref tickAgeGt1000) + "/" +
                Interlocked.Read(ref tickAgeUnknown) +
                ", capacity[clears/entriesCleared]=" +
                Interlocked.Read(ref capacityClears) + "/" +
                Interlocked.Read(ref entriesCleared) +
                ", bypass[runOriginal/scannerResolve]=" +
                Interlocked.Read(ref runOriginalBypass) + "/" +
                Interlocked.Read(ref scannerResolveBypass) +
                ", topScanners=" + BuildTopScannerSummary() +
                ", installFailures=" + installFailures +
                ". Measurement-only: every cross-package candidate still executes the live " +
                "JobGiver_Work validator; no __result write, no skip-original, no Job/reservation/" +
                "priority/reachability/candidate-order mutation.";
        }

        internal struct CallState
        {
            internal bool Active;
            internal bool Store;
            internal bool Prime;
            internal bool Shadow;
            internal bool HasCapturedFingerprints;
            internal PersistentKey Key;
            internal NegativeEntry Entry;
            internal Pawn Pawn;
            internal WorkGiver_Scanner Scanner;
            internal Thing Thing;
            internal long Generation;
            internal int GameTick;
            internal PawnFingerprint PawnFingerprint;
            internal ThingFingerprint ThingFingerprint;
            internal bool ForbiddenBefore;

            internal static CallState ForStore(
                PersistentKey key,
                Pawn pawn,
                WorkGiver_Scanner scanner,
                Thing thing,
                long generation,
                int tick)
            {
                return new CallState
                {
                    Active = true,
                    Store = true,
                    Key = key,
                    Pawn = pawn,
                    Scanner = scanner,
                    Thing = thing,
                    Generation = generation,
                    GameTick = tick
                };
            }

            internal static CallState ForStoreWithFingerprints(
                PersistentKey key,
                Pawn pawn,
                WorkGiver_Scanner scanner,
                Thing thing,
                long generation,
                int tick,
                PawnFingerprint pawnFingerprint,
                ThingFingerprint thingFingerprint)
            {
                return new CallState
                {
                    Active = true,
                    Store = true,
                    HasCapturedFingerprints = true,
                    Key = key,
                    Pawn = pawn,
                    Scanner = scanner,
                    Thing = thing,
                    Generation = generation,
                    GameTick = tick,
                    PawnFingerprint = pawnFingerprint,
                    ThingFingerprint = thingFingerprint
                };
            }

            internal static CallState ForPrime(
                PersistentKey key,
                NegativeEntry entry,
                Pawn pawn,
                WorkGiver_Scanner scanner,
                Thing thing,
                long generation,
                int tick,
                PawnFingerprint pawnFingerprint,
                ThingFingerprint thingFingerprint,
                bool forbiddenBefore)
            {
                return new CallState
                {
                    Active = true,
                    Prime = true,
                    Key = key,
                    Entry = entry,
                    Pawn = pawn,
                    Scanner = scanner,
                    Thing = thing,
                    Generation = generation,
                    GameTick = tick,
                    PawnFingerprint = pawnFingerprint,
                    ThingFingerprint = thingFingerprint,
                    ForbiddenBefore = forbiddenBefore
                };
            }

            internal static CallState ForShadow(
                PersistentKey key,
                NegativeEntry entry,
                Pawn pawn,
                WorkGiver_Scanner scanner,
                Thing thing,
                long generation,
                int tick,
                PawnFingerprint pawnFingerprint,
                ThingFingerprint thingFingerprint)
            {
                return new CallState
                {
                    Active = true,
                    Shadow = true,
                    Key = key,
                    Entry = entry,
                    Pawn = pawn,
                    Scanner = scanner,
                    Thing = thing,
                    Generation = generation,
                    GameTick = tick,
                    PawnFingerprint = pawnFingerprint,
                    ThingFingerprint = thingFingerprint
                };
            }
        }

        internal sealed class NegativeEntry
        {
            internal long LastGeneration;
            internal int LastGameTick;
            internal PawnFingerprint PawnFingerprint;
            internal ThingFingerprint ThingFingerprint;
            internal bool Primed;
            internal bool Forbidden;
        }

        internal sealed class ScannerStats
        {
            internal long Candidates;
            internal long Matches;
            internal long Mismatches;
        }

        internal struct PersistentKey : IEquatable<PersistentKey>
        {
            internal readonly Pawn Pawn;
            internal readonly MethodBase Method;
            internal readonly WorkGiver_Scanner Scanner;
            internal readonly Thing Thing;

            internal PersistentKey(
                Pawn pawn,
                MethodBase method,
                WorkGiver_Scanner scanner,
                Thing thing)
            {
                Pawn = pawn;
                Method = method;
                Scanner = scanner;
                Thing = thing;
            }

            public bool Equals(PersistentKey other)
            {
                return ReferenceEquals(Pawn, other.Pawn) &&
                    ReferenceEquals(Method, other.Method) &&
                    ReferenceEquals(Scanner, other.Scanner) &&
                    ReferenceEquals(Thing, other.Thing);
            }

            public override bool Equals(object obj)
            {
                return obj is PersistentKey && Equals((PersistentKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Pawn == null ? 0 : RuntimeHelpers.GetHashCode(Pawn);
                    h = h * 397 ^ (Method == null ? 0 : RuntimeHelpers.GetHashCode(Method));
                    h = h * 397 ^ (Scanner == null ? 0 : RuntimeHelpers.GetHashCode(Scanner));
                    h = h * 397 ^ (Thing == null ? 0 : RuntimeHelpers.GetHashCode(Thing));
                    return h;
                }
            }
        }

        internal struct PawnFingerprint : IEquatable<PawnFingerprint>
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly bool Dead;
            internal readonly bool Downed;
            internal readonly bool Drafted;
            internal readonly Faction Faction;

            internal PawnFingerprint(
                Map mapHeld,
                IntVec3 positionHeld,
                bool spawned,
                bool dead,
                bool downed,
                bool drafted,
                Faction faction)
            {
                MapHeld = mapHeld;
                PositionHeld = positionHeld;
                Spawned = spawned;
                Dead = dead;
                Downed = downed;
                Drafted = drafted;
                Faction = faction;
            }

            internal static PawnFingerprint Capture(Pawn pawn)
            {
                if (pawn == null)
                    return default(PawnFingerprint);

                return new PawnFingerprint(
                    pawn.MapHeld,
                    pawn.PositionHeld,
                    pawn.Spawned,
                    pawn.Dead,
                    pawn.Downed,
                    pawn.Drafted,
                    pawn.Faction);
            }

            public bool Equals(PawnFingerprint other)
            {
                return ReferenceEquals(MapHeld, other.MapHeld) &&
                    PositionHeld == other.PositionHeld &&
                    Spawned == other.Spawned &&
                    Dead == other.Dead &&
                    Downed == other.Downed &&
                    Drafted == other.Drafted &&
                    ReferenceEquals(Faction, other.Faction);
            }

            public override bool Equals(object obj)
            {
                return obj is PawnFingerprint && Equals((PawnFingerprint)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = MapHeld == null ? 0 : RuntimeHelpers.GetHashCode(MapHeld);
                    h = h * 397 ^ PositionHeld.GetHashCode();
                    h = h * 397 ^ (Spawned ? 1 : 0);
                    h = h * 397 ^ (Dead ? 1 : 0);
                    h = h * 397 ^ (Downed ? 1 : 0);
                    h = h * 397 ^ (Drafted ? 1 : 0);
                    h = h * 397 ^ (Faction == null ? 0 : RuntimeHelpers.GetHashCode(Faction));
                    return h;
                }
            }
        }

        internal struct ThingFingerprint : IEquatable<ThingFingerprint>
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;
            internal readonly int HitPoints;
            internal readonly Faction Faction;

            internal ThingFingerprint(
                Map mapHeld,
                IntVec3 positionHeld,
                bool spawned,
                int stackCount,
                int hitPoints,
                Faction faction)
            {
                MapHeld = mapHeld;
                PositionHeld = positionHeld;
                Spawned = spawned;
                StackCount = stackCount;
                HitPoints = hitPoints;
                Faction = faction;
            }

            internal static ThingFingerprint CaptureCheap(Thing thing)
            {
                if (thing == null)
                    return default(ThingFingerprint);

                return new ThingFingerprint(
                    thing.MapHeld,
                    thing.PositionHeld,
                    thing.Spawned,
                    thing.stackCount,
                    thing.HitPoints,
                    thing.Faction);
            }

            public bool Equals(ThingFingerprint other)
            {
                return ReferenceEquals(MapHeld, other.MapHeld) &&
                    PositionHeld == other.PositionHeld &&
                    Spawned == other.Spawned &&
                    StackCount == other.StackCount &&
                    HitPoints == other.HitPoints &&
                    ReferenceEquals(Faction, other.Faction);
            }

            public override bool Equals(object obj)
            {
                return obj is ThingFingerprint && Equals((ThingFingerprint)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = MapHeld == null ? 0 : RuntimeHelpers.GetHashCode(MapHeld);
                    h = h * 397 ^ PositionHeld.GetHashCode();
                    h = h * 397 ^ (Spawned ? 1 : 0);
                    h = h * 397 ^ StackCount;
                    h = h * 397 ^ HitPoints;
                    h = h * 397 ^ (Faction == null ? 0 : RuntimeHelpers.GetHashCode(Faction));
                    return h;
                }
            }
        }

        private sealed class ScannerAccessor
        {
            private readonly FieldInfo[] path;

            private ScannerAccessor(FieldInfo[] path)
            {
                this.path = path;
            }

            internal WorkGiver_Scanner Read(object root)
            {
                object value = root;
                try
                {
                    for (int i = 0; i < path.Length; i++)
                    {
                        if (value == null) return null;
                        value = path[i].GetValue(value);
                    }
                    return value as WorkGiver_Scanner;
                }
                catch
                {
                    return null;
                }
            }

            internal static ScannerAccessor Build(Type root)
            {
                List<FieldInfo> path = new List<FieldInfo>();
                HashSet<Type> visited = new HashSet<Type>();
                if (Find(root, 0, path, visited))
                    return new ScannerAccessor(path.ToArray());
                return null;
            }

            private static bool Find(
                Type type,
                int depth,
                List<FieldInfo> path,
                HashSet<Type> visited)
            {
                if (type == null || depth > 3 || visited.Contains(type))
                    return false;

                visited.Add(type);
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);
                }
                catch
                {
                    return false;
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field == null) continue;
                    if (typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType))
                    {
                        path.Add(field);
                        return true;
                    }
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field == null ||
                        field.FieldType.IsPrimitive ||
                        field.FieldType == typeof(string) ||
                        field.FieldType.IsEnum ||
                        field.FieldType.IsPointer)
                        continue;

                    path.Add(field);
                    if (Find(field.FieldType, depth + 1, path, visited))
                        return true;
                    path.RemoveAt(path.Count - 1);
                }

                return false;
            }
        }
    }
}
