using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// V0.9.3-T4: package-local necessary-condition index for WorkGiver_Merge.
    ///
    /// Vanilla ListerMergeables contains every non-full stack in storage, including isolated stacks.
    /// WorkGiver_Merge.JobOnThing then scans the entire storage group for every candidate looking for
    /// a compatible stack with count >= the source stack. On large colonies this can become O(N*M)
    /// inside DetermineNextJob.
    ///
    /// This patch performs only a proven negative test:
    /// - while one synchronous JobGiver_Work package is active, each storage group is indexed once;
    /// - for vanilla stack-semantics Thing types, same-def/same-stuff top-two partial stack counts are kept;
    /// - if no other stack can possibly satisfy Vanilla's required def/stuff/count preconditions,
    ///   JobOnThing is allowed to return null immediately;
    /// - every survivor still executes the original WorkGiver_Merge.JobOnThing in full.
    ///
    /// Fail-open rules:
    /// - forced/manual jobs are untouched;
    /// - any foreign Harmony patch on WorkGiver_Merge.JobOnThing or the supported CanStackWith methods
    ///   disables this optimization;
    /// - any custom Thing subclass overriding stack semantics makes that storage group fail open;
    /// - scope/cache errors fall through to Vanilla.
    /// </summary>
    internal static class WorkGiverMergePartnerIndex093T4
    {
        private const string HarmonyOwner = "allen.rimmt";
        private const int AuthorityRecheckMask = 4095;

        [ThreadStatic] private static long scopeStamp;
        [ThreadStatic] private static Dictionary<ISlotGroup, GroupIndex> groupCache;

        private static MethodInfo target;
        private static MethodInfo thingCanStack;
        private static MethodInfo thingWithCompsCanStack;
        private static MethodInfo minifiedCanStack;
        private static readonly Dictionary<Type, bool> SupportedTypeCache = new Dictionary<Type, bool>();

        // 0 unknown, 1 safe, -1 foreign patch detected.
        private static int authorityState;
        private static bool installed;

        private static long calls;
        private static long inScopeCalls;
        private static long groupBuilds;
        private static long groupHeldScans;
        private static long negativeRejects;
        private static long survivorCalls;
        private static long unsupportedGroupBypass;
        private static long unsupportedCandidateBypass;
        private static long foreignPatchBypass;
        private static long forcedBypass;
        private static long invalidBypass;
        private static long failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                target = AccessTools.Method(typeof(WorkGiver_Merge), "JobOnThing",
                    new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                thingCanStack = AccessTools.Method(typeof(Thing), "CanStackWith", new Type[] { typeof(Thing) });
                thingWithCompsCanStack = AccessTools.Method(typeof(ThingWithComps), "CanStackWith", new Type[] { typeof(Thing) });
                minifiedCanStack = AccessTools.Method(typeof(MinifiedThing), "CanStackWith", new Type[] { typeof(Thing) });

                if (target == null || thingCanStack == null || thingWithCompsCanStack == null || minifiedCanStack == null)
                {
                    Log.Warning("[RimMT] T4 HaulMerge partner index not installed: required Vanilla method missing.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(WorkGiverMergePartnerIndex093T4), nameof(Prefix));
                prefix.priority = Priority.First + 150;
                harmony.Patch(target, prefix: prefix);
                installed = true;
                Log.Message("[RimMT] T4 HaulMerge package-local partner index installed. Proven negatives skip only impossible merge candidates; survivors remain Vanilla-authoritative.");
            }
            catch (Exception ex)
            {
                installed = false;
                Log.Warning("[RimMT] T4 HaulMerge partner index failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool Prefix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            calls++;

            if (forced)
            {
                forcedBypass++;
                return true;
            }
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing ||
                !JobGiverGlobalNearest04181.InJobGiverScope)
            {
                invalidBypass++;
                return true;
            }
            inScopeCalls++;

            if (!AuthoritySafe())
            {
                foreignPatchBypass++;
                return true;
            }

            if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
            {
                invalidBypass++;
                return true;
            }
            if (!UsesSupportedVanillaStackSemantics(t))
            {
                unsupportedCandidateBypass++;
                return true;
            }

            try
            {
                ISlotGroup direct = t.GetSlotGroup();
                if (direct == null)
                {
                    invalidBypass++;
                    return true;
                }
                ISlotGroup group = direct.StorageGroup as ISlotGroup ?? direct;

                long stamp = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
                if (stamp <= 0L) return true;
                EnsureScope(stamp);

                GroupIndex index;
                if (!groupCache.TryGetValue(group, out index))
                {
                    index = BuildGroupIndex(group);
                    groupCache[group] = index;
                }

                if (index == null || index.UnsafeCustomSemantics)
                {
                    unsupportedGroupBypass++;
                    return true;
                }

                StackKey key = new StackKey(t.def, t.Stuff);
                PartnerStats stats;
                if (!index.ByKey.TryGetValue(key, out stats) || stats == null || !stats.HasPartnerFor(t))
                {
                    negativeRejects++;
                    __result = null;
                    return false;
                }

                survivorCalls++;
                return true;
            }
            catch
            {
                failures++;
                return true;
            }
        }

        private static void EnsureScope(long stamp)
        {
            if (groupCache == null)
                groupCache = new Dictionary<ISlotGroup, GroupIndex>();
            if (scopeStamp == stamp) return;
            scopeStamp = stamp;
            groupCache.Clear();
        }

        private static GroupIndex BuildGroupIndex(ISlotGroup group)
        {
            groupBuilds++;
            GroupIndex result = new GroupIndex();
            try
            {
                foreach (Thing held in group.HeldThings)
                {
                    groupHeldScans++;
                    if (held == null || held.Destroyed || held.def == null || held.stackCount <= 0 ||
                        held.stackCount >= held.def.stackLimit)
                        continue;

                    if (!UsesSupportedVanillaStackSemantics(held))
                    {
                        result.UnsafeCustomSemantics = true;
                        return result;
                    }

                    StackKey key = new StackKey(held.def, held.Stuff);
                    PartnerStats stats;
                    if (!result.ByKey.TryGetValue(key, out stats))
                    {
                        stats = new PartnerStats();
                        result.ByKey[key] = stats;
                    }
                    stats.Add(held);
                }
            }
            catch
            {
                result.UnsafeCustomSemantics = true;
                failures++;
            }
            return result;
        }

        private static bool UsesSupportedVanillaStackSemantics(Thing thing)
        {
            if (thing == null) return false;
            Type type = thing.GetType();
            bool cached;
            if (SupportedTypeCache.TryGetValue(type, out cached)) return cached;

            bool supported = false;
            try
            {
                MethodInfo method = AccessTools.Method(type, "CanStackWith", new Type[] { typeof(Thing) });
                Type declaring = method == null ? null : method.DeclaringType;
                supported = declaring == typeof(Thing) || declaring == typeof(ThingWithComps) || declaring == typeof(MinifiedThing);
            }
            catch
            {
                supported = false;
            }
            SupportedTypeCache[type] = supported;
            return supported;
        }

        private static bool AuthoritySafe()
        {
            long c = calls;
            if (authorityState != 0 && (c & AuthorityRecheckMask) != 1)
                return authorityState > 0;

            try
            {
                if (HasForeignPatch(target) || HasForeignPatch(thingCanStack) ||
                    HasForeignPatch(thingWithCompsCanStack) || HasForeignPatch(minifiedCanStack))
                {
                    authorityState = -1;
                    return false;
                }
                authorityState = 1;
                return true;
            }
            catch
            {
                authorityState = -1;
                return false;
            }
        }

        private static bool HasForeignPatch(MethodBase method)
        {
            if (method == null) return true;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) ||
                   HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        internal static string Summary()
        {
            return "T4 HaulMerge partner index: installed=" + installed +
                   ", authoritySafe=" + (authorityState > 0) +
                   ", calls=" + calls +
                   ", inScope=" + inScopeCalls +
                   ", groupBuilds=" + groupBuilds +
                   ", groupHeldScans=" + groupHeldScans +
                   ", negativeRejects=" + negativeRejects +
                   ", survivors=" + survivorCalls +
                   ", rejectRate=" + (inScopeCalls == 0 ? "0.00" : (negativeRejects * 100.0 / inScopeCalls).ToString("F2")) + "%" +
                   ", unsupportedGroupBypass=" + unsupportedGroupBypass +
                   ", unsupportedCandidateBypass=" + unsupportedCandidateBypass +
                   ", foreignPatchBypass=" + foreignPatchBypass +
                   ", forcedBypass=" + forcedBypass +
                   ", invalidBypass=" + invalidBypass +
                   ", failures=" + failures +
                   ". Cache lifetime=one synchronous JobGiver_Work package; negative proof=same def/stuff + other partial stack count >= source; survivors run original JobOnThing.";
        }

        private sealed class GroupIndex
        {
            internal readonly Dictionary<StackKey, PartnerStats> ByKey = new Dictionary<StackKey, PartnerStats>();
            internal bool UnsafeCustomSemantics;
        }

        private sealed class PartnerStats
        {
            private Thing first;
            private Thing second;
            private int firstCount = -1;
            private int secondCount = -1;

            internal void Add(Thing thing)
            {
                int count = thing.stackCount;
                if (count > firstCount)
                {
                    second = first;
                    secondCount = firstCount;
                    first = thing;
                    firstCount = count;
                }
                else if (count > secondCount)
                {
                    second = thing;
                    secondCount = count;
                }
            }

            internal bool HasPartnerFor(Thing source)
            {
                if (source == null) return true;
                int needed = source.stackCount;
                if (!ReferenceEquals(first, source))
                    return first != null && firstCount >= needed;
                return second != null && secondCount >= needed;
            }
        }

        private struct StackKey : IEquatable<StackKey>
        {
            private readonly ThingDef def;
            private readonly ThingDef stuff;

            internal StackKey(ThingDef def, ThingDef stuff)
            {
                this.def = def;
                this.stuff = stuff;
            }

            public bool Equals(StackKey other)
            {
                return ReferenceEquals(def, other.def) && ReferenceEquals(stuff, other.stuff);
            }

            public override bool Equals(object obj)
            {
                return obj is StackKey && Equals((StackKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((def == null ? 0 : def.shortHash) * 397) ^ (stuff == null ? 0 : stuff.shortHash);
                }
            }
        }
    }
}
