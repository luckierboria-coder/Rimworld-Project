using System;
using System.Diagnostics;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Lightweight synchronous JobGiver scope shared by the retained search modules.
    /// The former nearest-first plan cache has been removed; this type stores no candidate,
    /// validator, reachability, reservation or Job result.
    /// </summary>
    internal static class JobGiverGlobalNearest04181
    {
        [ThreadStatic] private static int jobGiverDepth;
        [ThreadStatic] private static long jobGiverStartTicks;

        internal static bool InJobGiverScope { get { return jobGiverDepth > 0; } }
        internal static long CurrentScopeStartTicks { get { return jobGiverDepth > 0 ? jobGiverStartTicks : 0L; } }

        public static void JobGiverPrefix(Pawn pawn)
        {
            if (jobGiverDepth == 0)
                jobGiverStartTicks = Stopwatch.GetTimestamp();
            jobGiverDepth++;
        }

        public static Exception JobGiverFinalizer(Exception exception)
        {
            if (jobGiverDepth > 0) jobGiverDepth--;
            if (jobGiverDepth == 0) jobGiverStartTicks = 0L;
            return exception;
        }
    }
}
