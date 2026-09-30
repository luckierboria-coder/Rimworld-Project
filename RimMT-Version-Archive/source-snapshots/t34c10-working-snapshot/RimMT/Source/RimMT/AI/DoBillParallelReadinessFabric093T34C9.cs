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
    /// Builds a reusable classification of simple Bill_Production stacks on a worker.
    /// Workers receive primitive rows only. Consumption always reads the current live bill fields
    /// on the main thread; target-count bills, subclasses and foreign ShouldDoNow patches fall
    /// through to Vanilla.
    /// </summary>
    internal static class DoBillParallelReadinessFabric093T34C9
    {
        internal const string FeatureId = "parallel.doBillReadiness";
        private const byte StableSimpleStack = 1;
        private const byte ModeForever = 1;
        private const byte ModeRepeatCount = 2;

        private static readonly ConditionalWeakTable<List<Thing>, Slot> Slots =
            new ConditionalWeakTable<List<Thing>, Slot>();
        private static int authorityState;
        private static int gameplayAuthorityAudited;
        private static long observed;
        private static long firstDeferred;
        private static long scheduled;
        private static long schedulerRejected;
        private static long built;
        private static long workerFailures;
        private static long planHits;
        private static long planMisses;
        private static long liveEvaluations;
        private static long liveActive;
        private static long liveInactive;
        private static long liveShapeFallback;
        private static long identityMismatch;
        private static long invalidations;
        private static long primitiveBillsCaptured;
        private static long stableStacksClassified;

        internal static void Apply()
        {
            authorityState = AuditAuthority() ? 1 : -1;
            if (authorityState > 0)
                Log.Message("[RimMT] T34-C.9 parallel DoBill readiness fabric active: primitive-only worker classification for exact Forever/RepeatCount Bill_Production stacks; live main-thread fields remain authoritative.");
            else
                Log.Warning("[RimMT] T34-C.9 parallel DoBill readiness fabric disabled: Bill_Production.ShouldDoNow has a foreign Harmony patch or could not be audited.");
        }

        internal static bool TryGetOrSchedule(List<Thing> source, out ReadinessPlan plan)
        {
            plan = null;
            Interlocked.Increment(ref observed);
            if (authorityState <= 0 || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing ||
                source == null || source.Count == 0)
                return false;

            // Static-constructor order is not a compatibility guarantee. Re-audit once after the
            // game reaches Playing so patches installed later during startup cannot be missed.
            if (Interlocked.CompareExchange(ref gameplayAuthorityAudited, 1, 0) == 0 && !AuditAuthority())
            {
                Volatile.Write(ref authorityState, -1);
                Log.Warning("[RimMT] T34-C.9 disabled at first gameplay use because a late Bill_Production.ShouldDoNow patch was found.");
                return false;
            }
            if (Volatile.Read(ref authorityState) <= 0) return false;

            Slot slot = Slots.GetValue(source, delegate(List<Thing> ignored) { return new Slot(); });
            if (Volatile.Read(ref slot.Ready) != 0)
            {
                ReadinessPlan ready = slot.Plan;
                if (ready != null && ready.Count == source.Count)
                {
                    plan = ready;
                    Interlocked.Increment(ref planHits);
                    return true;
                }
                Invalidate(slot);
            }

            Interlocked.Increment(ref planMisses);
            if (Interlocked.Increment(ref slot.Observations) < 2)
            {
                Interlocked.Increment(ref firstDeferred);
                return false;
            }
            if (Interlocked.CompareExchange(ref slot.Scheduled, 1, 0) != 0)
                return false;

            Capture capture;
            try { capture = CapturePrimitive(source); }
            catch
            {
                Volatile.Write(ref slot.Scheduled, 0);
                return false;
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            bool accepted = scheduler != null && scheduler.TryEnqueue(
                FeatureId, JobPriority.High, delegate { Build(slot, capture); });
            if (accepted)
                Interlocked.Increment(ref scheduled);
            else
            {
                Volatile.Write(ref slot.Scheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
            }
            return false;
        }

        internal static bool TryEvaluate(
            List<Thing> source, ReadinessPlan plan, int index, Thing thing,
            BillStack stack, out bool active)
        {
            active = false;
            if (plan == null || source == null || stack == null || index < 0 ||
                index >= plan.Count || index >= source.Count || thing == null)
                return false;
            if (plan.SourceIds[index] != thing.thingIDNumber || !ReferenceEquals(source[index], thing))
            {
                Interlocked.Increment(ref identityMismatch);
                Slot slot;
                if (Slots.TryGetValue(source, out slot) && slot != null) Invalidate(slot);
                return false;
            }
            if (plan.Codes[index] != StableSimpleStack)
            {
                Interlocked.Increment(ref liveShapeFallback);
                return false;
            }

            bool any = false;
            List<Bill> bills = stack.Bills;
            for (int i = 0; i < bills.Count; i++)
            {
                Bill_Production bill = bills[i] as Bill_Production;
                if (bill == null || bill.GetType() != typeof(Bill_Production) ||
                    (bill.repeatMode != BillRepeatModeDefOf.Forever &&
                     bill.repeatMode != BillRepeatModeDefOf.RepeatCount))
                {
                    Interlocked.Increment(ref liveShapeFallback);
                    return false;
                }

                // Vanilla clears the target-count paused flag when this bill is in a simple mode.
                bill.paused = false;
                if (!bill.suspended &&
                    (bill.repeatMode == BillRepeatModeDefOf.Forever || bill.repeatCount > 0))
                    any = true;
            }

            active = any;
            Interlocked.Increment(ref liveEvaluations);
            if (any) Interlocked.Increment(ref liveActive);
            else Interlocked.Increment(ref liveInactive);
            return true;
        }

        private static Capture CapturePrimitive(List<Thing> source)
        {
            int[] ids = new int[source.Count];
            SourceFact[] stacks = new SourceFact[source.Count];
            List<BillFact> bills = new List<BillFact>();
            for (int i = 0; i < source.Count; i++)
            {
                Thing thing = source[i];
                ids[i] = thing == null ? 0 : thing.thingIDNumber;
                IBillGiver giver = thing as IBillGiver;
                BillStack stack = giver == null ? null : giver.BillStack;
                int start = bills.Count;
                if (stack != null)
                {
                    List<Bill> live = stack.Bills;
                    for (int j = 0; j < live.Count; j++)
                    {
                        Bill bill = live[j];
                        Bill_Production production = bill as Bill_Production;
                        byte exact = (byte)(production != null && bill.GetType() == typeof(Bill_Production) ? 1 : 0);
                        byte mode = 0;
                        bool suspended = false;
                        int repeat = 0;
                        if (exact != 0)
                        {
                            suspended = production.suspended;
                            repeat = production.repeatCount;
                            if (production.repeatMode == BillRepeatModeDefOf.Forever) mode = ModeForever;
                            else if (production.repeatMode == BillRepeatModeDefOf.RepeatCount) mode = ModeRepeatCount;
                        }
                        bills.Add(new BillFact(exact, mode, suspended, repeat));
                    }
                }
                stacks[i] = new SourceFact(start, bills.Count - start, stack != null);
            }
            Interlocked.Add(ref primitiveBillsCaptured, bills.Count);
            return new Capture(ids, stacks, bills.ToArray());
        }

        private static void Build(Slot slot, Capture capture)
        {
            try
            {
                byte[] codes = new byte[capture.Stacks.Length];
                long localStable = 0;
                for (int i = 0; i < capture.Stacks.Length; i++)
                {
                    SourceFact stack = capture.Stacks[i];
                    if (!stack.HasStack) continue;
                    bool stable = true;
                    for (int j = 0; j < stack.Count; j++)
                    {
                        BillFact bill = capture.Bills[stack.Start + j];
                        if (bill.ExactProduction == 0 ||
                            (bill.Mode != ModeForever && bill.Mode != ModeRepeatCount))
                        {
                            stable = false;
                            break;
                        }
                        // Read all captured primitive fields on the worker. The result is only a
                        // classification hint; live fields are re-read before every use.
                        bool ignoredActive = !bill.Suspended &&
                            (bill.Mode == ModeForever || bill.RepeatCount > 0);
                        if (ignoredActive) { }
                    }
                    if (!stable) continue;
                    codes[i] = StableSimpleStack;
                    localStable++;
                }
                slot.Plan = new ReadinessPlan(capture.SourceIds, codes);
                Interlocked.Add(ref stableStacksClassified, localStable);
                Interlocked.Increment(ref built);
                Volatile.Write(ref slot.Ready, 1);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerFailures);
                Volatile.Write(ref slot.Scheduled, 0);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
        }

        private static void Invalidate(Slot slot)
        {
            if (slot == null) return;
            slot.Plan = null;
            Volatile.Write(ref slot.Ready, 0);
            Volatile.Write(ref slot.Scheduled, 0);
            Volatile.Write(ref slot.Observations, 1);
            Interlocked.Increment(ref invalidations);
        }

        private static bool AuditAuthority()
        {
            MethodInfo method = AccessTools.Method(typeof(Bill_Production), nameof(Bill_Production.ShouldDoNow));
            if (method == null) return false;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return true;
            return !HasForeign(info.Prefixes) && !HasForeign(info.Postfixes) &&
                   !HasForeign(info.Transpilers) && !HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        internal static string Summary()
        {
            return "T34-C.9 Parallel DoBill Readiness Fabric: authority=" + authorityState +
                ", observed=" + Interlocked.Read(ref observed) +
                ", firstDeferred=" + Interlocked.Read(ref firstDeferred) +
                ", plans[scheduled/rejected/built/hit/miss]=" + Interlocked.Read(ref scheduled) + "/" +
                Interlocked.Read(ref schedulerRejected) + "/" + Interlocked.Read(ref built) + "/" +
                Interlocked.Read(ref planHits) + "/" + Interlocked.Read(ref planMisses) +
                ", primitiveBills=" + Interlocked.Read(ref primitiveBillsCaptured) +
                ", stableStacks=" + Interlocked.Read(ref stableStacksClassified) +
                ", live[eval/active/inactive/shapeFallback]=" + Interlocked.Read(ref liveEvaluations) + "/" +
                Interlocked.Read(ref liveActive) + "/" + Interlocked.Read(ref liveInactive) + "/" +
                Interlocked.Read(ref liveShapeFallback) +
                ", identityMismatch=" + Interlocked.Read(ref identityMismatch) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ". Worker input is primitive-only; no wait; target-count/subclass/foreign-patched bills remain Vanilla.";
        }

        internal sealed class ReadinessPlan
        {
            internal readonly int[] SourceIds;
            internal readonly byte[] Codes;
            internal int Count { get { return Codes.Length; } }
            internal ReadinessPlan(int[] sourceIds, byte[] codes) { SourceIds = sourceIds; Codes = codes; }
        }

        private sealed class Slot
        {
            internal int Observations;
            internal int Scheduled;
            internal int Ready;
            internal ReadinessPlan Plan;
        }

        private sealed class Capture
        {
            internal readonly int[] SourceIds;
            internal readonly SourceFact[] Stacks;
            internal readonly BillFact[] Bills;
            internal Capture(int[] sourceIds, SourceFact[] stacks, BillFact[] bills)
            { SourceIds = sourceIds; Stacks = stacks; Bills = bills; }
        }

        private struct SourceFact
        {
            internal readonly int Start;
            internal readonly int Count;
            internal readonly bool HasStack;
            internal SourceFact(int start, int count, bool hasStack)
            { Start = start; Count = count; HasStack = hasStack; }
        }

        private struct BillFact
        {
            internal readonly byte ExactProduction;
            internal readonly byte Mode;
            internal readonly bool Suspended;
            internal readonly int RepeatCount;
            internal BillFact(byte exactProduction, byte mode, bool suspended, int repeatCount)
            { ExactProduction = exactProduction; Mode = mode; Suspended = suspended; RepeatCount = repeatCount; }
        }
    }
}
