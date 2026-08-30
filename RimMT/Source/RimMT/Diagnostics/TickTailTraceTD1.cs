using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal struct TickTailProbeState
    {
        internal long Started;
        internal int Slot;
        internal bool Entered;
    }

    internal sealed class TickTailSlowSample
    {
        internal double TotalMs;
        internal long Frame;
        internal long[] CategoryTicks;
        internal int[] CategoryCalls;
        internal int Gc0;
        internal int Gc1;
        internal int Gc2;
    }

    internal static class TickTailTraceTD1
    {
        private const string TempHarmonyId = "allen.rimmt.td1.ticktail.temp";
        private const double SlowThresholdMs = 32.0;
        private const int WarmupTicks = 120;
        private const int TargetSlowTicks = 12;
        private const int MaxCaptureTicks = 1200;

        private const int TickListSlot = 0;
        private const int PawnSlot = 1;
        private const int JobGiverSlot = 2;
        private const int PawnJobSlot = 3;
        private const int PawnHealthSlot = 4;
        private const int PawnNeedsSlot = 5;
        private const int PawnPathSlot = 6;
        private const int PawnMindSlot = 7;
        private const int PawnStanceSlot = 8;
        private const int MapPreSlot = 9;
        private const int MapPostSlot = 10;
        private const int MapComponentSlot = 11;
        private const int WorldSlot = 12;
        private const int WorldComponentSlot = 13;
        private const int GameComponentSlot = 14;
        private const int StorytellerSlot = 15;
        private const int QuestSlot = 16;
        private const int TaleSlot = 17;
        private const int WorldPawnsSlot = 18;
        private const int PathFinderSlot = 19;
        private const int LettersSlot = 20;

        private static readonly string[] CategoryNames =
        {
            "TickList", "Pawn", "JobGiver", "Pawn.Job", "Pawn.Health", "Pawn.Needs", "Pawn.Path", "Pawn.Mind", "Pawn.Stance",
            "Map.Pre", "Map.Post", "MapComponent", "World", "WorldComponent", "GameComponent", "Storyteller", "Quest", "Tale", "WorldPawns", "PathFinder", "Letters"
        };

        private static readonly Dictionary<MethodBase, int> MethodSlots = new Dictionary<MethodBase, int>();
        private static readonly List<TickTailSlowSample> SlowSamples = new List<TickTailSlowSample>(TargetSlowTicks);
        private static readonly long[] CurrentTicks = new long[CategoryNames.Length];
        private static readonly int[] CurrentCalls = new int[CategoryNames.Length];
        private static readonly long[] SlowAggregateTicks = new long[CategoryNames.Length];
        private static readonly long[] SlowAggregateCalls = new long[CategoryNames.Length];

        [ThreadStatic] private static int[] categoryDepth;

        private static Harmony tempHarmony;
        private static bool captureActive;
        private static bool inCapturedTick;
        private static bool completed;
        private static bool installAttempted;
        private static long seenTicks;
        private static int captureTicks;
        private static int patchedMethods;
        private static int patchFailures;
        private static int resolverMisses;
        private static double triggerMs;
        private static double installMs;
        private static int gc0Start;
        private static int gc1Start;
        private static int gc2Start;

        internal static void TickBegin()
        {
            seenTicks++;
            if (!captureActive)
                return;

            Array.Clear(CurrentTicks, 0, CurrentTicks.Length);
            Array.Clear(CurrentCalls, 0, CurrentCalls.Length);
            if (categoryDepth != null)
                Array.Clear(categoryDepth, 0, categoryDepth.Length);
            gc0Start = GC.CollectionCount(0);
            gc1Start = GC.CollectionCount(1);
            gc2Start = GC.CollectionCount(2);
            inCapturedTick = true;
        }

        internal static void TickEnd(long outerStarted)
        {
            if (outerStarted == 0L || RuntimeCompatibility.ButterPlusPlusActive)
                return;

            long now = Stopwatch.GetTimestamp();
            double totalMs = TicksToMs(now - outerStarted);

            if (captureActive)
            {
                inCapturedTick = false;
                captureTicks++;
                if (totalMs >= SlowThresholdMs)
                    RecordSlowTick(totalMs);

                if (SlowSamples.Count >= TargetSlowTicks || captureTicks >= MaxCaptureTicks)
                    StopCapture();
                return;
            }

            if (completed || installAttempted || seenTicks < WarmupTicks || Current.ProgramState != ProgramState.Playing)
                return;

            if (totalMs >= SlowThresholdMs)
                StartCapture(totalMs);
        }

        public static void ProbePrefix(MethodBase __originalMethod, ref TickTailProbeState __state)
        {
            __state.Started = 0L;
            __state.Slot = -1;
            __state.Entered = false;
            if (!captureActive || !inCapturedTick || __originalMethod == null)
                return;

            int slot;
            if (!MethodSlots.TryGetValue(__originalMethod, out slot) || slot < 0 || slot >= CategoryNames.Length)
                return;

            int[] depths = categoryDepth;
            if (depths == null)
            {
                depths = new int[CategoryNames.Length];
                categoryDepth = depths;
            }

            __state.Slot = slot;
            __state.Entered = true;
            if (depths[slot]++ == 0)
                __state.Started = Stopwatch.GetTimestamp();
        }

        public static void ProbePostfix(TickTailProbeState __state)
        {
            if (!__state.Entered || __state.Slot < 0 || __state.Slot >= CategoryNames.Length)
                return;

            int[] depths = categoryDepth;
            if (depths == null)
                return;

            int slot = __state.Slot;
            if (depths[slot] > 0)
                depths[slot]--;

            if (__state.Started == 0L || depths[slot] != 0 || !inCapturedTick)
                return;

            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            if (elapsed < 0L)
                elapsed = 0L;
            CurrentTicks[slot] += elapsed;
            CurrentCalls[slot]++;
        }

        private static void StartCapture(double firstSlowMs)
        {
            installAttempted = true;
            triggerMs = firstSlowMs;
            Stopwatch install = Stopwatch.StartNew();
            try
            {
                tempHarmony = new Harmony(TempHarmonyId);

                PatchNamedType("Verse.TickList", "Tick", TickListSlot, true, false);
                PatchNamedType("Verse.Pawn", "Tick", PawnSlot, true, false);
                PatchNamedType("RimWorld.JobGiver_Work", "TryIssueJobPackage", JobGiverSlot, false, false);
                PatchNamedType("Verse.AI.Pawn_JobTracker", "JobTrackerTick", PawnJobSlot, true, false);
                PatchNamedType("Verse.Pawn_HealthTracker", "HealthTick", PawnHealthSlot, true, false);
                PatchNamedType("Verse.Pawn_NeedsTracker", "NeedsTrackerTick", PawnNeedsSlot, true, false);
                PatchNamedType("Verse.AI.Pawn_PathFollower", "PatherTick", PawnPathSlot, true, false);
                PatchNamedType("Verse.AI.Pawn_MindState", "MindStateTick", PawnMindSlot, true, false);
                PatchNamedType("Verse.AI.Pawn_StanceTracker", "StanceTrackerTick", PawnStanceSlot, true, false);
                PatchNamedType("Verse.Map", "MapPreTick", MapPreSlot, true, false);
                PatchNamedType("Verse.Map", "MapPostTick", MapPostSlot, true, false);
                PatchNamedType("Verse.MapComponent", "MapComponentTick", MapComponentSlot, true, true);
                PatchNamedType("RimWorld.Planet.World", "WorldTick", WorldSlot, true, false);
                PatchNamedType("RimWorld.Planet.WorldComponent", "WorldComponentTick", WorldComponentSlot, true, true);
                PatchNamedType("Verse.GameComponent", "GameComponentTick", GameComponentSlot, true, true);
                PatchNamedType("RimWorld.Storyteller", "StorytellerTick", StorytellerSlot, true, false);
                PatchNamedType("RimWorld.QuestManager", "QuestManagerTick", QuestSlot, true, false);
                PatchNamedType("RimWorld.TaleManager", "TaleManagerTick", TaleSlot, true, false);
                PatchNamedType("RimWorld.Planet.WorldPawns", "WorldPawnsTick", WorldPawnsSlot, true, false);
                PatchNamedType("Verse.AI.PathFinder", "FindPath", PathFinderSlot, false, false);
                PatchNamedType("Verse.LetterStack", "LettersTick", LettersSlot, true, false);

                captureActive = patchedMethods > 0;
                if (!captureActive)
                {
                    completed = true;
                    Log.Warning("[RimMT] TD1 Tick TailTrace could not install any temporary category probes; campaign aborted.");
                }
            }
            catch (Exception ex)
            {
                patchFailures++;
                captureActive = false;
                completed = true;
                Log.Warning("[RimMT] TD1 Tick TailTrace installation failed; S5.1 gameplay logic remains unchanged. " + ex.GetType().Name + ": " + ex.Message);
                TryUnpatch();
            }
            finally
            {
                install.Stop();
                installMs = install.Elapsed.TotalMilliseconds;
            }

            if (captureActive)
            {
                Log.Message("[RimMT] TD1 TICK TAILTRACE TRIGGERED at " + firstSlowMs.ToString("F3") +
                    "ms after warmupTicks=" + seenTicks + ". Temporary category probes installed=" + patchedMethods +
                    ", resolverMisses=" + resolverMisses + ", patchFailures=" + patchFailures +
                    ", installMs=" + installMs.ToString("F2") + ". Capture stops at " + TargetSlowTicks +
                    " slow ticks or " + MaxCaptureTicks + " observed ticks; only >=32ms ticks are retained.");
            }
        }

        private static void PatchNamedType(string typeName, string methodName, int slot, bool zeroArgsOnly, bool includeSubclasses)
        {
            Type type = null;
            try { type = AccessTools.TypeByName(typeName); }
            catch { type = null; }
            if (type == null)
            {
                resolverMisses++;
                return;
            }

            PatchDeclared(type, methodName, slot, zeroArgsOnly);
            if (!includeSubclasses)
                return;

            try
            {
                List<Type> subclasses = GenTypes.AllSubclasses(type);
                if (subclasses == null)
                    return;
                for (int i = 0; i < subclasses.Count; i++)
                    PatchDeclared(subclasses[i], methodName, slot, zeroArgsOnly);
            }
            catch
            {
                resolverMisses++;
            }
        }

        private static void PatchDeclared(Type type, string methodName, int slot, bool zeroArgsOnly)
        {
            if (type == null)
                return;

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                resolverMisses++;
                return;
            }

            bool found = false;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != methodName)
                    continue;
                if (zeroArgsOnly && method.GetParameters().Length != 0)
                    continue;
                found = true;
                PatchMethod(method, slot);
            }

            if (!found && type.BaseType == null)
                resolverMisses++;
        }

        private static void PatchMethod(MethodBase method, int slot)
        {
            if (method == null || MethodSlots.ContainsKey(method))
                return;

            try
            {
                MethodSlots.Add(method, slot);
                HarmonyMethod prefix = new HarmonyMethod(typeof(TickTailTraceTD1), nameof(ProbePrefix));
                prefix.priority = Priority.First;
                HarmonyMethod postfix = new HarmonyMethod(typeof(TickTailTraceTD1), nameof(ProbePostfix));
                postfix.priority = Priority.Last;
                tempHarmony.Patch(method, prefix: prefix, postfix: postfix);
                patchedMethods++;
            }
            catch
            {
                MethodSlots.Remove(method);
                patchFailures++;
            }
        }

        private static void RecordSlowTick(double totalMs)
        {
            TickTailSlowSample sample = new TickTailSlowSample();
            sample.TotalMs = totalMs;
            sample.Frame = RimMTRuntime.MainThreadFrames;
            sample.CategoryTicks = (long[])CurrentTicks.Clone();
            sample.CategoryCalls = (int[])CurrentCalls.Clone();
            sample.Gc0 = Math.Max(0, GC.CollectionCount(0) - gc0Start);
            sample.Gc1 = Math.Max(0, GC.CollectionCount(1) - gc1Start);
            sample.Gc2 = Math.Max(0, GC.CollectionCount(2) - gc2Start);
            SlowSamples.Add(sample);

            for (int i = 0; i < CategoryNames.Length; i++)
            {
                SlowAggregateTicks[i] += CurrentTicks[i];
                SlowAggregateCalls[i] += CurrentCalls[i];
            }

            if (SlowSamples.Count <= 3)
                Log.Message("[RimMT] TD1 slow tick sample #" + SlowSamples.Count + ": " + FormatSample(sample, 8));
        }

        private static void StopCapture()
        {
            captureActive = false;
            inCapturedTick = false;
            TryUnpatch();
            completed = true;
            Log.Message("[RimMT] TD1 TICK TAILTRACE COMPLETE; temporary detours removed.\n" + Summary());
        }

        private static void TryUnpatch()
        {
            try
            {
                if (tempHarmony != null)
                    tempHarmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                patchFailures++;
                Log.Warning("[RimMT] TD1 temporary unpatch reported " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder();
            string state = completed ? "COMPLETE" : (captureActive ? "CAPTURING" : "ARMED");
            sb.Append("TD1 Tick TailTrace: state=").Append(state)
                .Append(", thresholdMs=").Append(SlowThresholdMs.ToString("F0"))
                .Append(", seenTicks=").Append(seenTicks)
                .Append(", triggerMs=").Append(triggerMs.ToString("F3"))
                .Append(", captureTicks=").Append(captureTicks).Append('/').Append(MaxCaptureTicks)
                .Append(", slowTicks=").Append(SlowSamples.Count).Append('/').Append(TargetSlowTicks)
                .Append(", patchedMethods=").Append(patchedMethods)
                .Append(", resolverMisses=").Append(resolverMisses)
                .Append(", patchFailures=").Append(patchFailures)
                .Append(", installMs=").Append(installMs.ToString("F2"))
                .Append(". Category timings are inclusive; nested categories overlap.");

            if (SlowSamples.Count > 0)
            {
                sb.Append("\nTD1 slow-category avg: ");
                AppendAverageCategories(sb, 12);

                double tickList = AverageMs(TickListSlot);
                double pawn = AverageMs(PawnSlot);
                double pawnChildren = AverageMs(PawnJobSlot) + AverageMs(PawnHealthSlot) + AverageMs(PawnNeedsSlot) + AverageMs(PawnPathSlot) + AverageMs(PawnMindSlot) + AverageMs(PawnStanceSlot);
                sb.Append("\nTD1 derived estimates: nonPawnThing~=").Append(Math.Max(0.0, tickList - pawn).ToString("F3"))
                    .Append("ms/slowTick, pawnOther~=").Append(Math.Max(0.0, pawn - pawnChildren).ToString("F3"))
                    .Append("ms/slowTick. These are approximate subtraction views of inclusive timings.");

                List<TickTailSlowSample> ordered = new List<TickTailSlowSample>(SlowSamples);
                ordered.Sort(delegate(TickTailSlowSample a, TickTailSlowSample b) { return b.TotalMs.CompareTo(a.TotalMs); });
                int show = Math.Min(ordered.Count, 8);
                for (int i = 0; i < show; i++)
                    sb.Append("\nTD1 SLOW#").Append(i + 1).Append(' ').Append(FormatSample(ordered[i], 8));
            }
            return sb.ToString();
        }

        private static void AppendAverageCategories(StringBuilder sb, int maxCategories)
        {
            List<int> slots = new List<int>();
            for (int i = 0; i < CategoryNames.Length; i++)
            {
                if (SlowAggregateTicks[i] > 0L)
                    slots.Add(i);
            }
            slots.Sort(delegate(int a, int b) { return SlowAggregateTicks[b].CompareTo(SlowAggregateTicks[a]); });
            int show = Math.Min(maxCategories, slots.Count);
            for (int i = 0; i < show; i++)
            {
                if (i > 0) sb.Append(" | ");
                int slot = slots[i];
                double avgMs = AverageMs(slot);
                double avgCalls = SlowSamples.Count == 0 ? 0.0 : (double)SlowAggregateCalls[slot] / SlowSamples.Count;
                sb.Append(CategoryNames[slot]).Append('=').Append(avgMs.ToString("F3")).Append("ms/").Append(avgCalls.ToString("F1")).Append("calls");
            }
        }

        private static string FormatSample(TickTailSlowSample sample, int maxCategories)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("total=").Append(sample.TotalMs.ToString("F3")).Append("ms frame=").Append(sample.Frame)
                .Append(" GC=").Append(sample.Gc0).Append('/').Append(sample.Gc1).Append('/').Append(sample.Gc2).Append(" :: ");

            List<int> slots = new List<int>();
            for (int i = 0; i < CategoryNames.Length; i++)
            {
                if (sample.CategoryTicks[i] > 0L)
                    slots.Add(i);
            }
            slots.Sort(delegate(int a, int b) { return sample.CategoryTicks[b].CompareTo(sample.CategoryTicks[a]); });
            int show = Math.Min(maxCategories, slots.Count);
            for (int i = 0; i < show; i++)
            {
                if (i > 0) sb.Append(" | ");
                int slot = slots[i];
                sb.Append(CategoryNames[slot]).Append('=').Append(TicksToMs(sample.CategoryTicks[slot]).ToString("F3"))
                    .Append("ms(").Append(sample.CategoryCalls[slot]).Append(')');
            }
            if (show == 0)
                sb.Append("no category probe activity");
            return sb.ToString();
        }

        private static double AverageMs(int slot)
        {
            if (slot < 0 || slot >= SlowAggregateTicks.Length || SlowSamples.Count == 0)
                return 0.0;
            return TicksToMs(SlowAggregateTicks[slot]) / SlowSamples.Count;
        }

        private static double TicksToMs(long ticks)
        {
            return ticks <= 0L ? 0.0 : (ticks * 1000.0 / Stopwatch.Frequency);
        }
    }
}
