using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// V0.9.3-T5: measurement-only Harmony census for the exact methods that gate the
    /// T4 HaulMerge partner index. It never changes patch order, authority, or behavior.
    /// The report is recomputed on demand so late-loaded Harmony patches are visible.
    /// </summary>
    internal static class HaulMergePatchCensus093T5
    {
        private const string RimMTOwner = "allen.rimmt";
        private static bool scheduled;

        internal static void Apply()
        {
            if (scheduled) return;
            scheduled = true;
            try
            {
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    try
                    {
                        Log.Message("[RimMT] T5 HaulMerge Harmony patch census (post-load, measurement-only):\n" + DetailedSummary());
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[RimMT] T5 HaulMerge patch census failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T5 HaulMerge patch census scheduling failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static string DetailedSummary()
        {
            try
            {
                TargetSpec[] targets = ResolveTargets();
                StringBuilder sb = new StringBuilder(2048);
                int totalPatches = 0;
                int foreignPatches = 0;
                int blockedTargets = 0;

                for (int i = 0; i < targets.Length; i++)
                {
                    TargetSpec spec = targets[i];
                    bool blocked;
                    int targetTotal;
                    int targetForeign;
                    string details = DescribeTarget(spec, out blocked, out targetTotal, out targetForeign);
                    totalPatches += targetTotal;
                    foreignPatches += targetForeign;
                    if (blocked) blockedTargets++;
                    if (i != 0) sb.AppendLine();
                    sb.Append(details);
                }

                sb.Insert(0,
                    "T5 HaulMerge patch census: blocked=" + (blockedTargets != 0) +
                    ", blockedTargets=" + blockedTargets + "/" + targets.Length +
                    ", totalPatches=" + totalPatches +
                    ", foreignPatches=" + foreignPatches +
                    ". T4 remains unchanged and still fails open on any foreign patch.\n");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "T5 HaulMerge patch census failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static TargetSpec[] ResolveTargets()
        {
            Type[] pawnThingBool = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
            Type[] thingArg = new Type[] { typeof(Thing) };
            return new TargetSpec[]
            {
                new TargetSpec("WorkGiver_Merge.JobOnThing",
                    AccessTools.Method(typeof(WorkGiver_Merge), "JobOnThing", pawnThingBool)),
                new TargetSpec("Thing.CanStackWith",
                    AccessTools.Method(typeof(Thing), "CanStackWith", thingArg)),
                new TargetSpec("ThingWithComps.CanStackWith",
                    AccessTools.Method(typeof(ThingWithComps), "CanStackWith", thingArg)),
                new TargetSpec("MinifiedThing.CanStackWith",
                    AccessTools.Method(typeof(MinifiedThing), "CanStackWith", thingArg))
            };
        }

        private static string DescribeTarget(TargetSpec spec, out bool blocked, out int total, out int foreign)
        {
            blocked = false;
            total = 0;
            foreign = 0;
            StringBuilder sb = new StringBuilder(512);
            sb.Append(" - ").Append(spec.Name).Append(": ");

            if (spec.Method == null)
            {
                blocked = true;
                sb.Append("MISSING [BLOCKER]");
                return sb.ToString();
            }

            Patches info = Harmony.GetPatchInfo(spec.Method);
            if (info == null)
            {
                sb.Append("no patches; foreign=0; blocker=False");
                return sb.ToString();
            }

            List<string> entries = new List<string>();
            AddEntries(entries, "Prefix", info.Prefixes, ref total, ref foreign);
            AddEntries(entries, "Postfix", info.Postfixes, ref total, ref foreign);
            AddEntries(entries, "Transpiler", info.Transpilers, ref total, ref foreign);
            AddEntries(entries, "Finalizer", info.Finalizers, ref total, ref foreign);
            blocked = foreign != 0;

            sb.Append("patches=").Append(total)
                .Append(", foreign=").Append(foreign)
                .Append(", blocker=").Append(blocked);
            if (entries.Count == 0)
            {
                sb.Append("; none");
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                    sb.Append("\n    ").Append(entries[i]);
            }
            return sb.ToString();
        }

        private static void AddEntries(List<string> entries, string kind, IEnumerable<Patch> patches,
            ref int total, ref int foreign)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                total++;
                bool isForeign = !string.Equals(patch.owner, RimMTOwner, StringComparison.Ordinal);
                if (isForeign) foreign++;

                MethodInfo method = patch.PatchMethod;
                string methodName = method == null
                    ? "<unknown>"
                    : ((method.DeclaringType == null ? "<global>" : method.DeclaringType.FullName) + "." + method.Name);
                entries.Add(kind + " owner=" + (patch.owner ?? "<null>") +
                    ", priority=" + patch.priority +
                    ", method=" + methodName +
                    (isForeign ? " [FOREIGN/BLOCKER]" : " [RimMT]"));
            }
        }

        private sealed class TargetSpec
        {
            internal readonly string Name;
            internal readonly MethodBase Method;

            internal TargetSpec(string name, MethodBase method)
            {
                Name = name;
                Method = method;
            }
        }
    }
}
