using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimOPT
{
    internal static class PackageScope
    {
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static Context current;
        private static long nextGeneration;

        internal static bool Active { get { return depth > 0 && current != null; } }
        internal static long Generation { get { return Active ? current.Generation : 0L; } }
        internal static Context CurrentContext { get { return Active ? current : null; } }

        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage",
                new Type[] { typeof(Pawn), typeof(JobIssueParams) });
            if (target == null) throw new MissingMethodException("JobGiver_Work.TryIssueJobPackage");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PackageScope), nameof(Prefix)) { priority = Priority.First + 400 },
                finalizer: new HarmonyMethod(typeof(PackageScope), nameof(Finalizer)) { priority = Priority.Last - 400 });
        }

        public static void Prefix(Pawn __0, ref ScopeState __state)
        {
            __state = default(ScopeState);
            if (Current.ProgramState != ProgramState.Playing) return;
            __state.Entered = true;
            __state.Outermost = depth == 0;
            depth++;
            if (__state.Outermost)
            {
                current = new Context(Interlocked.Increment(ref nextGeneration), __0);
                __state.Context = current;
            }
            else
            {
                __state.Context = current;
            }
        }

        public static Exception Finalizer(Exception __exception, ScopeState __state)
        {
            if (!__state.Entered) return __exception;
            if (depth > 0) depth--;
#if RIMWORLD17
            if (__state.Outermost && __state.Context != null && RimOptMod.Settings.PulseDiagnosticsEnabled)
                PulseProfiler.RecordWork(Stopwatch.GetTimestamp() - __state.Context.Started);
#endif
            if (__state.Outermost)
            {
                current = null;
                depth = 0;
            }
            return __exception;
        }

        internal static bool IsInactive(BillStack stack)
        {
            return Active && stack != null && current.InactiveBillStacks.Contains(stack);
        }

        internal static void MarkInactive(BillStack stack)
        {
            if (Active && stack != null) current.InactiveBillStacks.Add(stack);
        }

        internal struct ScopeState
        {
            internal bool Entered;
            internal bool Outermost;
            internal Context Context;
        }

        internal sealed class Context
        {
            internal readonly long Generation;
            internal readonly Pawn Pawn;
            internal readonly long Started;
            internal readonly HashSet<BillStack> InactiveBillStacks = new HashSet<BillStack>();
            internal Context(long generation, Pawn pawn)
            {
                Generation = generation;
                Pawn = pawn;
                Started = Stopwatch.GetTimestamp();
            }
        }
    }
}
