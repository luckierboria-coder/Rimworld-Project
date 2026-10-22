using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimOPT
{
    internal static class HaulMergeIndex
    {
        private const int RecheckMask = 4095;
        [ThreadStatic] private static long generation;
        [ThreadStatic] private static Dictionary<ISlotGroup, GroupIndex> groups;
        private static readonly object typeGate = new object();
        private static readonly Dictionary<Type, bool> supportedTypes = new Dictionary<Type, bool>();
        private static MethodInfo target, thingCanStack, compsCanStack, minifiedCanStack;
        private static int authority;
        private static long calls;
        private static bool installed;

        internal static string Status { get { return "HaulMerge[index=" + installed + ", authority=runtime-gated]"; } }

        internal static void Install(Harmony harmony)
        {
            target = AccessTools.Method(typeof(WorkGiver_Merge), "JobOnThing", new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            thingCanStack = AccessTools.Method(typeof(Thing), "CanStackWith", new Type[] { typeof(Thing) });
            compsCanStack = AccessTools.Method(typeof(ThingWithComps), "CanStackWith", new Type[] { typeof(Thing) });
            minifiedCanStack = AccessTools.Method(typeof(MinifiedThing), "CanStackWith", new Type[] { typeof(Thing) });
            if (target == null || thingCanStack == null || compsCanStack == null || minifiedCanStack == null) return;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HaulMergeIndex), nameof(Prefix)) { priority = Priority.First + 150 });
            installed = true;
        }

        public static bool Prefix(Thing t, bool forced, ref Job __result)
        {
            calls++;
            if (forced || !PackageScope.Active || Current.ProgramState != ProgramState.Playing) return true;
            int mode = AuthorityMode();
            if (mode < 0) return true;
            if (mode == 2 && t != null && t.def != null && t.def.IsIngestible) return true;
            if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit) return true;
            if (!SupportedStackSemantics(t)) return true;
            try
            {
                ISlotGroup direct = t.GetSlotGroup();
                if (direct == null) return true;
                ISlotGroup group = direct.StorageGroup as ISlotGroup ?? direct;
                EnsureScope();
                GroupIndex index;
                if (!groups.TryGetValue(group, out index))
                {
                    index = Build(group);
                    groups[group] = index;
                }
                if (index == null || index.Unsafe) return true;
                PartnerStats stats;
                if (!index.ByKey.TryGetValue(new StackKey(t.def, t.Stuff), out stats) || !stats.HasPartnerFor(t))
                {
                    __result = null;
                    return false;
                }
            }
            catch { return true; }
            return true;
        }

        private static void EnsureScope()
        {
            if (groups == null) groups = new Dictionary<ISlotGroup, GroupIndex>();
            long current = PackageScope.Generation;
            if (generation == current) return;
            generation = current;
            groups.Clear();
        }

        private static GroupIndex Build(ISlotGroup group)
        {
            GroupIndex result = new GroupIndex();
            try
            {
                foreach (Thing held in group.HeldThings)
                {
                    if (held == null || held.Destroyed || held.def == null || held.stackCount <= 0 || held.stackCount >= held.def.stackLimit) continue;
                    if (!SupportedStackSemantics(held)) { result.Unsafe = true; return result; }
                    StackKey key = new StackKey(held.def, held.Stuff);
                    PartnerStats stats;
                    if (!result.ByKey.TryGetValue(key, out stats)) result.ByKey[key] = stats = new PartnerStats();
                    stats.Add(held);
                }
            }
            catch { result.Unsafe = true; }
            return result;
        }

        private static bool SupportedStackSemantics(Thing thing)
        {
            Type type = thing.GetType();
            lock (typeGate)
            {
                bool value;
                if (supportedTypes.TryGetValue(type, out value)) return value;
                MethodInfo method = AccessTools.Method(type, "CanStackWith", new Type[] { typeof(Thing) });
                Type owner = method == null ? null : method.DeclaringType;
                value = owner == typeof(Thing) || owner == typeof(ThingWithComps) || owner == typeof(MinifiedThing);
                supportedTypes[type] = value;
                return value;
            }
        }

        private static int AuthorityMode()
        {
            if (authority != 0 && (calls & RecheckMask) != 1) return authority;
            if (HasForeign(target) || HasForeign(compsCanStack) || HasForeign(minifiedCanStack)) return authority = -1;
            Patches info = Harmony.GetPatchInfo(thingCanStack);
            if (info == null) return authority = 1;
            if (HasForeign(info.Prefixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers)) return authority = -1;
            bool commonSense = false;
            foreach (Patch patch in info.Postfixes)
            {
                if (patch == null || patch.owner == Bootstrap.HarmonyId) continue;
                MethodInfo method = patch.PatchMethod;
                string owner = method == null || method.DeclaringType == null ? null : method.DeclaringType.FullName;
                if (patch.owner == "net.avilmask.rimworld.mod.CommonSense" && owner == "CommonSense.CompIngredients_CanStackWith_CommonSensePatch" && method.Name == "Postfix")
                    commonSense = true;
                else return authority = -1;
            }
            return authority = commonSense ? 2 : 1;
        }

        private static bool HasForeign(MethodBase method)
        {
            if (method == null) return true;
            Patches info = Harmony.GetPatchInfo(method);
            return info != null && (HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers));
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
                if (patch != null && patch.owner != Bootstrap.HarmonyId) return true;
            return false;
        }

        private sealed class GroupIndex { internal readonly Dictionary<StackKey, PartnerStats> ByKey = new Dictionary<StackKey, PartnerStats>(); internal bool Unsafe; }
        private sealed class PartnerStats
        {
            private Thing first, second;
            private int firstCount = -1, secondCount = -1;
            internal void Add(Thing thing)
            {
                int count = thing.stackCount;
                if (count > firstCount) { second = first; secondCount = firstCount; first = thing; firstCount = count; }
                else if (count > secondCount) { second = thing; secondCount = count; }
            }
            internal bool HasPartnerFor(Thing source)
            {
                if (!ReferenceEquals(first, source)) return first != null && firstCount >= source.stackCount;
                return second != null && secondCount >= source.stackCount;
            }
        }
        private struct StackKey : IEquatable<StackKey>
        {
            private readonly ThingDef def, stuff;
            internal StackKey(ThingDef def, ThingDef stuff) { this.def = def; this.stuff = stuff; }
            public bool Equals(StackKey other) { return ReferenceEquals(def, other.def) && ReferenceEquals(stuff, other.stuff); }
            public override bool Equals(object obj) { return obj is StackKey && Equals((StackKey)obj); }
            public override int GetHashCode() { unchecked { return ((def == null ? 0 : def.shortHash) * 397) ^ (stuff == null ? 0 : stuff.shortHash); } }
        }
    }
}
