using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T18 one-shot Storyteller deep burst. It profiles the next 64 completed storyteller
    /// interval enumerations, then removes all temporary detours. It never changes Storyteller
    /// decisions. T15's existing >=100ms top-level timer remains the catastrophic-event authority.
    /// </summary>
    internal static class StorytellerDeepAttribution093T18
    {
        private const string DeepHarmonyId = "allen.rimmt.storyteller-t18";
        private const int CaptureIntervals = 64;
        private const int MaxStats = 32;
        private static readonly long Threshold5 = Math.Max(1L, Stopwatch.Frequency * 5L / 1000L);
        private static readonly long Threshold20 = Math.Max(1L, Stopwatch.Frequency * 20L / 1000L);
        private static readonly long Threshold100 = Math.Max(1L, Stopwatch.Frequency * 100L / 1000L);

        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static readonly List<MethodBase> Patched = new List<MethodBase>();
        private static readonly Dictionary<MethodBase, bool> TopIterator = new Dictionary<MethodBase, bool>();
        private static readonly Dictionary<Type, FieldInfo> CompFieldCache = new Dictionary<Type, FieldInfo>();
        private static Harmony deepHarmony;
        private static int initialized;
        private static int requested;
        private static int started;
        private static int active;
        private static int completed;
        private static int stopRequested;
        private static int startFailures;
        private static int patchedMethods;
        private static int iteratorMethods;
        private static int intervalsCompleted;
        private static long queueCalls;
        private static long queueTicks;
        private static long queueMaxTicks;
        private static long queue5;
        private static long queue20;
        private static long queue100;
        private static long deepCatastrophicEvents;
        private static long deepCatastrophicMaxUs;
        private static long startFrame = -1L;
        private static long completionFrame = -1L;
        private static int startTick = -1;
        private static int completionTick = -1;

        internal struct TimedState
        {
            internal long Started;
            internal string Label;
        }

        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            Interlocked.Exchange(ref requested, 1);
        }

        internal static void ObserveCatastrophic(long us)
        {
            if (us < 100000L || Volatile.Read(ref active) == 0 || !RimMTThreadGuard.IsMainThread) return;
            deepCatastrophicEvents++;
            if (us > deepCatastrophicMaxUs) deepCatastrophicMaxUs = us;
        }

        internal static void OnMainThreadFrame()
        {
            if (!RimMTThreadGuard.IsMainThread) return;

            if (Volatile.Read(ref stopRequested) != 0 && Volatile.Read(ref active) != 0)
            {
                StopCapture();
                return;
            }

            if (Volatile.Read(ref started) != 0 || Volatile.Read(ref requested) == 0)
                return;
            if (Current.ProgramState != ProgramState.Playing || RimMTRuntime.MainThreadFrames <= 1)
                return;

            Interlocked.Exchange(ref requested, 0);
            try
            {
                if (StartCapture())
                {
                    Interlocked.Exchange(ref started, 1);
                    Interlocked.Exchange(ref active, 1);
                    startFrame = RimMTRuntime.MainThreadFrames;
                    startTick = CurrentTick();
                    Log.Message("[RimMT] T18 Storyteller deep burst started for the next " + CaptureIntervals + " completed storyteller intervals; temporary detours auto-remove afterward.");
                }
                else
                {
                    startFailures++;
                }
            }
            catch (Exception ex)
            {
                startFailures++;
                FailClosed(ex);
            }
        }

        private static bool StartCapture()
        {
            deepHarmony = new Harmony(DeepHarmonyId);
            Patched.Clear();
            TopIterator.Clear();
            CompFieldCache.Clear();
            Stats.Clear();
            intervalsCompleted = 0;
            patchedMethods = 0;
            iteratorMethods = 0;

            MethodBase queue = AccessTools.Method(typeof(IncidentQueue), "IncidentQueueTick");
            if (queue != null)
            {
                deepHarmony.Patch(queue,
                    prefix: new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), nameof(QueuePrefix)),
                    postfix: new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), nameof(QueuePostfix)));
                Patched.Add(queue);
                patchedMethods++;
            }

            MethodBase tryFire = AccessTools.Method(typeof(Storyteller), "TryFire", new Type[] { typeof(FiringIncident), typeof(bool) });
            if (tryFire != null)
            {
                deepHarmony.Patch(tryFire,
                    prefix: new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), nameof(TryFirePrefix)),
                    postfix: new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), nameof(TryFirePostfix)));
                Patched.Add(tryFire);
                patchedMethods++;
            }

            Type[] nested = typeof(Storyteller).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nested.Length; i++)
            {
                Type type = nested[i];
                string name = type == null ? string.Empty : type.Name;
                if (name.IndexOf("MakeIncidentsForInterval", StringComparison.Ordinal) < 0) continue;
                MethodInfo moveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext == null || moveNext.ReturnType != typeof(bool)) continue;

                bool isTop = FindCompField(type) == null;
                deepHarmony.Patch(moveNext,
                    prefix: new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), nameof(IteratorPrefix)),
                    postfix: new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), nameof(IteratorPostfix)));
                Patched.Add(moveNext);
                TopIterator[moveNext] = isTop;
                patchedMethods++;
                iteratorMethods++;
            }

            if (iteratorMethods == 0)
            {
                StopAllPatches();
                return false;
            }
            return true;
        }

        private static void StopCapture()
        {
            StopAllPatches();
            Interlocked.Exchange(ref active, 0);
            Interlocked.Exchange(ref completed, 1);
            Interlocked.Exchange(ref stopRequested, 0);
            completionFrame = RimMTRuntime.MainThreadFrames;
            completionTick = CurrentTick();
            Log.Message("[RimMT] T18 Storyteller deep burst completed and temporary detours were removed. intervals=" + intervalsCompleted + ".");
        }

        private static void StopAllPatches()
        {
            Harmony h = deepHarmony;
            if (h != null)
            {
                for (int i = 0; i < Patched.Count; i++)
                {
                    try { h.Unpatch(Patched[i], HarmonyPatchType.All, DeepHarmonyId); }
                    catch { }
                }
            }
            Patched.Clear();
        }

        private static void FailClosed(Exception ex)
        {
            try { StopAllPatches(); } catch { }
            Interlocked.Exchange(ref active, 0);
            Interlocked.Exchange(ref stopRequested, 0);
            Log.Warning("[RimMT] T18 Storyteller deep burst failed closed: " + ex.GetType().Name + ": " + ex.Message);
        }

        public static void QueuePrefix(ref long __state)
        {
            __state = Volatile.Read(ref active) != 0 ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void QueuePostfix(long __state)
        {
            if (__state == 0L || Volatile.Read(ref active) == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            queueCalls++;
            queueTicks += elapsed;
            if (elapsed > queueMaxTicks) queueMaxTicks = elapsed;
            if (elapsed >= Threshold5) queue5++;
            if (elapsed >= Threshold20) queue20++;
            if (elapsed >= Threshold100) queue100++;
        }

        public static void TryFirePrefix(object[] __args, ref TimedState __state)
        {
            if (Volatile.Read(ref active) == 0) return;
            __state.Started = Stopwatch.GetTimestamp();
            __state.Label = "TryFire:<unknown>";
            try
            {
                FiringIncident fi = __args != null && __args.Length > 0 ? __args[0] as FiringIncident : null;
                if (fi != null && fi.def != null)
                {
                    string worker = fi.def.Worker == null ? "<no-worker>" : fi.def.Worker.GetType().FullName;
                    __state.Label = "TryFire:" + fi.def.defName + "|" + worker;
                }
            }
            catch { }
        }

        public static void TryFirePostfix(TimedState __state)
        {
            Record(__state);
        }

        public static void IteratorPrefix(MethodBase __originalMethod, object __instance, ref TimedState __state)
        {
            if (Volatile.Read(ref active) == 0) return;
            __state.Started = Stopwatch.GetTimestamp();
            bool top = false;
            TopIterator.TryGetValue(__originalMethod, out top);
            if (top)
            {
                __state.Label = "Iterator:Storyteller.MakeIncidentsForInterval(top)";
                return;
            }

            string comp = "<unknown-comp>";
            try
            {
                FieldInfo field = FindCompField(__instance == null ? null : __instance.GetType());
                StorytellerComp value = field == null || __instance == null ? null : field.GetValue(__instance) as StorytellerComp;
                if (value != null) comp = value.GetType().FullName;
            }
            catch { }
            __state.Label = "Iterator:StorytellerComp:" + comp;
        }

        public static void IteratorPostfix(MethodBase __originalMethod, bool __result, TimedState __state)
        {
            Record(__state);
            bool top = false;
            if (TopIterator.TryGetValue(__originalMethod, out top) && top && !__result)
            {
                int done = Interlocked.Increment(ref intervalsCompleted);
                if (done >= CaptureIntervals)
                    Interlocked.Exchange(ref stopRequested, 1);
            }
        }

        private static void Record(TimedState state)
        {
            if (state.Started == 0L || Volatile.Read(ref active) == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - state.Started;
            string label = string.IsNullOrEmpty(state.Label) ? "<unknown>" : state.Label;
            Stat stat;
            if (!Stats.TryGetValue(label, out stat))
            {
                if (Stats.Count >= MaxStats) label = "<other>";
                if (!Stats.TryGetValue(label, out stat))
                {
                    stat = new Stat { Label = label };
                    Stats[label] = stat;
                }
            }
            stat.Calls++;
            stat.TotalTicks += elapsed;
            if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
            if (elapsed >= Threshold5) stat.Over5++;
            if (elapsed >= Threshold20) stat.Over20++;
            if (elapsed >= Threshold100) stat.Over100++;
        }

        private static FieldInfo FindCompField(Type type)
        {
            if (type == null) return null;
            FieldInfo cached;
            if (CompFieldCache.TryGetValue(type, out cached)) return cached;
            FieldInfo found = null;
            try
            {
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (typeof(StorytellerComp).IsAssignableFrom(fields[i].FieldType))
                    {
                        found = fields[i];
                        break;
                    }
                }
            }
            catch { }
            CompFieldCache[type] = found;
            return found;
        }

        internal static string Summary()
        {
            List<Stat> list = new List<Stat>(Stats.Values);
            list.Sort(delegate(Stat a, Stat b) { return b.TotalTicks.CompareTo(a.TotalTicks); });
            StringBuilder sb = new StringBuilder(8192);
            double queueTotalMs = queueTicks * 1000.0 / Stopwatch.Frequency;
            double queueAvgUs = queueCalls == 0 ? 0.0 : queueTicks * 1000000.0 / Stopwatch.Frequency / queueCalls;
            double queueMaxMs = queueMaxTicks * 1000.0 / Stopwatch.Frequency;
            sb.Append("T18 Storyteller deep burst: requested=").Append(Volatile.Read(ref requested) != 0)
              .Append(", started=").Append(Volatile.Read(ref started) != 0)
              .Append(", active=").Append(Volatile.Read(ref active) != 0)
              .Append(", completed=").Append(Volatile.Read(ref completed) != 0)
              .Append(", intervals=").Append(intervalsCompleted).Append('/').Append(CaptureIntervals)
              .Append(", patchedMethods=").Append(patchedMethods)
              .Append(", iteratorMethods=").Append(iteratorMethods)
              .Append(", startFailures=").Append(startFailures)
              .Append(", deepCatastrophicEvents=").Append(deepCatastrophicEvents)
              .Append(", deepCatastrophicMaxMs=").Append((deepCatastrophicMaxUs / 1000.0).ToString("F2"))
              .Append(", startFrame/tick=").Append(startFrame).Append('/').Append(startTick)
              .Append(", completionFrame/tick=").Append(completionFrame).Append('/').Append(completionTick)
              .Append("\n  IncidentQueueTick: calls=").Append(queueCalls)
              .Append(", totalMs=").Append(queueTotalMs.ToString("F2"))
              .Append(", avgUs=").Append(queueAvgUs.ToString("F2"))
              .Append(", maxMs=").Append(queueMaxMs.ToString("F3"))
              .Append(", >=5/20/100ms=").Append(queue5).Append('/').Append(queue20).Append('/').Append(queue100);

            int top = Math.Min(20, list.Count);
            for (int i = 0; i < top; i++)
            {
                Stat s = list[i];
                double totalMs = s.TotalTicks * 1000.0 / Stopwatch.Frequency;
                double avgMs = s.Calls == 0 ? 0.0 : totalMs / s.Calls;
                double maxMs = s.MaxTicks * 1000.0 / Stopwatch.Frequency;
                sb.Append("\n  #").Append(i + 1).Append(' ').Append(s.Label)
                  .Append(": calls=").Append(s.Calls)
                  .Append(", totalMs=").Append(totalMs.ToString("F2"))
                  .Append(", avgMs=").Append(avgMs.ToString("F3"))
                  .Append(", maxMs=").Append(maxMs.ToString("F3"))
                  .Append(", >=5/20/100ms=").Append(s.Over5).Append('/').Append(s.Over20).Append('/').Append(s.Over100);
            }
            sb.Append(". Temporary Harmony detours only; measurement-only; auto-unpatch after bounded interval window.");
            return sb.ToString();
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private sealed class Stat
        {
            internal string Label;
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long Over5;
            internal long Over20;
            internal long Over100;
        }
    }
}
