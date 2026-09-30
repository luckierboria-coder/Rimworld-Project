using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T17 production optimization for AllowTool.WorkGiver_HaulUrgently.PotentialWorkThingsGlobal.
    /// The first enumeration inside one synchronous JobGiver_Work package is recorded while Vanilla
    /// consumes it normally. Later calls in the same package may reuse that exact ordered Thing list.
    /// No job, validator result, Reachability result or state survives the package boundary.
    /// Every 64th reuse performs a fresh reference/order parity sample; any mismatch quarantines the
    /// optimization for the rest of the runtime. Foreign patches on the source method/iterator disable it.
    /// </summary>
    internal static class HaulUrgentlyDynamicMemo093T17
    {
        internal const string FeatureId = "ai.haulUrgentlyDynamicMemo";
        private const int ShadowSampleMask = 63;
        private const string TargetDeclaringType = "AllowTool.WorkGiver_HaulUrgently";
        private const string TargetIteratorMarker = "PotentialWorkThingsGlobal";

        [ThreadStatic] private static long currentScopeStart;
        [ThreadStatic] private static PackageMemo currentMemo;

        private static readonly Dictionary<Type, bool> AuthorityCache = new Dictionary<Type, bool>();
        private static volatile bool installed;
        private static volatile bool enabled = true;
        private static volatile bool runtimeQuarantined;
        private static int patchedMethods;
        private static long observed;
        private static long targetDetected;
        private static long authorityBypass;
        private static long wrappedFirstPass;
        private static long completedBuilds;
        private static long incompleteBuilds;
        private static long cacheHits;
        private static long wrapperReplayHits;
        private static long shadowSamples;
        private static long shadowMatches;
        private static long shadowMismatches;
        private static long shadowItems;
        private static long recordedItems;
        private static int maxRecordedItems;
        private static long reentrantBypass;
        private static long failures;

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
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(HaulUrgentlyDynamicMemo093T17), nameof(Prefix))
                        { priority = Priority.First + 200 });
                    count++;
                }
                patchedMethods = count;
                installed = count > 0;
                if (installed)
                    Log.Message("[RimMT] T17 HaulUrgently package-local dynamic-source memo active on " + count + " ClosestThingReachable overload(s); Vanilla search/validator/reachability remain authoritative.");
            }
            catch (Exception ex)
            {
                installed = false;
                failures++;
                Log.Warning("[RimMT] T17 HaulUrgently dynamic-source memo install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value) { enabled = value; }

        private static bool IsSupportedOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static void Prefix(ref IEnumerable<Thing> __7)
        {
            observed++;
            if (!enabled || runtimeQuarantined || __7 == null ||
                !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing)
                return;

            Type sourceType = __7.GetType();
            if (!IsTargetSource(sourceType)) return;
            targetDetected++;

            if (!IsAuthoritySafe(sourceType))
            {
                authorityBypass++;
                return;
            }

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L) return;
            if (scopeStart != currentScopeStart)
            {
                currentScopeStart = scopeStart;
                currentMemo = null;
            }

            PackageMemo memo = currentMemo;
            if (memo == null || memo.SourceType != sourceType)
            {
                memo = new PackageMemo(sourceType);
                currentMemo = memo;
            }

            if (memo.Complete)
            {
                memo.ReuseSerial++;
                if ((memo.ReuseSerial & ShadowSampleMask) == 0)
                {
                    List<Thing> fresh = MaterializeFresh(__7);
                    shadowSamples++;
                    shadowItems += fresh.Count;
                    if (!SameReferenceOrder(memo.Items, fresh))
                    {
                        shadowMismatches++;
                        runtimeQuarantined = true;
                        __7 = fresh;
                        return;
                    }
                    shadowMatches++;
                    // Use the freshly materialized sequence for the sampled call itself. This pays
                    // Vanilla-equivalent source enumeration cost while validating the memo exactly.
                    __7 = fresh;
                    return;
                }

                cacheHits++;
                __7 = memo.Items;
                return;
            }

            if (memo.Building)
            {
                reentrantBypass++;
                return;
            }

            wrappedFirstPass++;
            __7 = new RecordingEnumerable(__7, memo);
        }

        private static bool IsTargetSource(Type sourceType)
        {
            if (sourceType == null) return false;
            string full = sourceType.FullName ?? sourceType.Name;
            return full.StartsWith(TargetDeclaringType + "+", StringComparison.Ordinal) &&
                   full.IndexOf(TargetIteratorMarker, StringComparison.Ordinal) >= 0;
        }

        private static bool IsAuthoritySafe(Type iteratorType)
        {
            bool cached;
            if (AuthorityCache.TryGetValue(iteratorType, out cached)) return cached;

            bool safe = false;
            try
            {
                Type declaring = iteratorType.DeclaringType;
                if (declaring == null || !string.Equals(declaring.FullName, TargetDeclaringType, StringComparison.Ordinal))
                {
                    AuthorityCache[iteratorType] = false;
                    return false;
                }

                MethodInfo sourceMethod = null;
                MethodInfo[] methods = declaring.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != TargetIteratorMarker) continue;
                    if (!typeof(IEnumerable<Thing>).IsAssignableFrom(method.ReturnType)) continue;
                    sourceMethod = method;
                    break;
                }
                if (sourceMethod == null)
                {
                    AuthorityCache[iteratorType] = false;
                    return false;
                }

                MethodInfo moveNext = iteratorType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                safe = !HasAnyHarmonyPatch(sourceMethod) && (moveNext == null || !HasAnyHarmonyPatch(moveNext));
            }
            catch
            {
                safe = false;
            }

            AuthorityCache[iteratorType] = safe;
            return safe;
        }

        private static bool HasAnyHarmonyPatch(MethodBase method)
        {
            if (method == null) return false;
            Patches info = Harmony.GetPatchInfo(method);
            return info != null &&
                   (info.Prefixes.Count != 0 || info.Postfixes.Count != 0 ||
                    info.Transpilers.Count != 0 || info.Finalizers.Count != 0);
        }

        private static List<Thing> MaterializeFresh(IEnumerable<Thing> source)
        {
            List<Thing> result = new List<Thing>();
            try
            {
                foreach (Thing thing in source) result.Add(thing);
                return result;
            }
            catch
            {
                failures++;
                runtimeQuarantined = true;
                throw;
            }
        }

        private static bool SameReferenceOrder(List<Thing> a, List<Thing> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        internal static string Summary()
        {
            return "T17 HaulUrgently dynamic-source memo: installed=" + installed +
                   ", patchedMethods=" + patchedMethods +
                   ", enabled=" + enabled +
                   ", runtimeQuarantined=" + runtimeQuarantined +
                   ", observed=" + observed +
                   ", targetDetected=" + targetDetected +
                   ", authorityBypass=" + authorityBypass +
                   ", wrappedFirstPass=" + wrappedFirstPass +
                   ", completedBuilds=" + completedBuilds +
                   ", incompleteBuilds=" + incompleteBuilds +
                   ", cacheHits=" + cacheHits +
                   ", wrapperReplayHits=" + wrapperReplayHits +
                   ", shadowSamples/matches/mismatches=" + shadowSamples + "/" + shadowMatches + "/" + shadowMismatches +
                   ", shadowItems=" + shadowItems +
                   ", recordedItems=" + recordedItems +
                   ", maxRecordedItems=" + maxRecordedItems +
                   ", reentrantBypass=" + reentrantBypass +
                   ", failures=" + failures +
                   ". Cache lifetime=one synchronous JobGiver_Work package; first pass preserves source order; sampled parity compares exact Thing references/order; final Vanilla validator/Reachability/JobOnThing remain authoritative.";
        }

        private sealed class PackageMemo
        {
            internal readonly Type SourceType;
            internal readonly List<Thing> Items = new List<Thing>();
            internal bool Building;
            internal bool Complete;
            internal int ReuseSerial;

            internal PackageMemo(Type sourceType)
            {
                SourceType = sourceType;
            }
        }

        private sealed class RecordingEnumerable : IEnumerable<Thing>
        {
            private readonly IEnumerable<Thing> source;
            private readonly PackageMemo memo;

            internal RecordingEnumerable(IEnumerable<Thing> source, PackageMemo memo)
            {
                this.source = source;
                this.memo = memo;
            }

            public IEnumerator<Thing> GetEnumerator()
            {
                if (memo.Complete)
                {
                    wrapperReplayHits++;
                    return memo.Items.GetEnumerator();
                }
                if (memo.Building)
                {
                    reentrantBypass++;
                    return source.GetEnumerator();
                }
                return Record().GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

            private IEnumerable<Thing> Record()
            {
                memo.Building = true;
                memo.Complete = false;
                memo.Items.Clear();
                bool completed = false;
                try
                {
                    foreach (Thing thing in source)
                    {
                        memo.Items.Add(thing);
                        yield return thing;
                    }
                    completed = true;
                }
                finally
                {
                    memo.Building = false;
                    if (completed)
                    {
                        memo.Complete = true;
                        completedBuilds++;
                        recordedItems += memo.Items.Count;
                        if (memo.Items.Count > maxRecordedItems) maxRecordedItems = memo.Items.Count;
                    }
                    else
                    {
                        memo.Complete = false;
                        memo.Items.Clear();
                        incompleteBuilds++;
                    }
                }
            }
        }
    }
}
