using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T19 long-lived, low-duty-cycle attribution for natural random quest selection.
    /// Stopwatch work is active only while NaturalRandomQuestChooser.ChooseNaturalRandomQuest
    /// is on the current main-thread call stack. It never changes quest eligibility/results.
    /// </summary>
    internal static class QuestDeepAttribution093T19
    {
        private const double SlowThresholdMs = 5.0;
        private const double CatastrophicThresholdMs = 100.0;
        private const int MaxRecentCatastrophes = 12;
        private const int MaxSlowCallsPerCatastrophe = 16;
        private const int MaxSummaryQuests = 24;

        [ThreadStatic] private static ChoiceContext currentChoice;

        private static bool installed;
        private static MethodBaseHolder targets;
        private static long chooserCalls;
        private static long chooserTicks;
        private static long chooserMaxTicks;
        private static long chooserCatastrophic;
        private static long canRunCalls;
        private static long canRunTicks;
        private static long canRunSlow5;
        private static long canRunSlow20;
        private static long canRunSlow100;
        private static long failures;

        private static readonly Dictionary<string, QuestStats> stats = new Dictionary<string, QuestStats>(StringComparer.Ordinal);
        private static readonly List<Catastrophe> recentCatastrophes = new List<Catastrophe>();

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                var chooser = AccessTools.Method(typeof(NaturalRandomQuestChooser), nameof(NaturalRandomQuestChooser.ChooseNaturalRandomQuest),
                    new Type[] { typeof(float), typeof(IIncidentTarget) });
                var canRun = AccessTools.Method(typeof(QuestScriptDef), nameof(QuestScriptDef.CanRun),
                    new Type[] { typeof(Slate) });
                if (chooser == null || canRun == null)
                {
                    Log.Warning("[RimMT] T19 quest deep attribution unavailable: chooser or QuestScriptDef.CanRun(Slate) not found.");
                    return;
                }

                targets = new MethodBaseHolder(chooser, canRun);
                harmony.Patch(chooser,
                    prefix: new HarmonyMethod(typeof(QuestDeepAttribution093T19), nameof(ChooserPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(QuestDeepAttribution093T19), nameof(ChooserPostfix)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(QuestDeepAttribution093T19), nameof(ChooserFinalizer)) { priority = Priority.Last });
                harmony.Patch(canRun,
                    prefix: new HarmonyMethod(typeof(QuestDeepAttribution093T19), nameof(CanRunPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(QuestDeepAttribution093T19), nameof(CanRunPostfix)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(QuestDeepAttribution093T19), nameof(CanRunFinalizer)) { priority = Priority.Last });

                installed = true;
                Log.Message("[RimMT] T19 quest deep attribution installed. Measurement-only; timers are active only inside NaturalRandomQuestChooser.ChooseNaturalRandomQuest().");
            }
            catch (Exception ex)
            {
                installed = false;
                failures++;
                Log.Warning("[RimMT] T19 quest deep attribution install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public sealed class ChooserState
        {
            internal long Started;
            internal ChoiceContext Previous;
            internal ChoiceContext Context;
            internal bool Completed;
        }

        public static void ChooserPrefix(ref ChooserState __state)
        {
            __state = null;
            if (!installed || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            var context = new ChoiceContext();
            __state = new ChooserState
            {
                Started = Stopwatch.GetTimestamp(),
                Previous = currentChoice,
                Context = context,
                Completed = false
            };
            currentChoice = context;
        }

        public static void ChooserPostfix(ChooserState __state)
        {
            CompleteChooser(__state, false);
        }

        public static Exception ChooserFinalizer(Exception __exception, ChooserState __state)
        {
            if (__exception != null)
                CompleteChooser(__state, true);
            return __exception;
        }

        public static void CanRunPrefix(ref long __state)
        {
            __state = currentChoice == null ? 0L : Stopwatch.GetTimestamp();
        }

        public static void CanRunPostfix(QuestScriptDef __instance, bool __result, long __state)
        {
            if (__state == 0L || currentChoice == null || __instance == null) return;
            RecordCanRun(__instance, __result, false, Stopwatch.GetTimestamp() - __state);
        }

        public static Exception CanRunFinalizer(QuestScriptDef __instance, Exception __exception, long __state)
        {
            if (__exception != null && __state != 0L && currentChoice != null && __instance != null)
                RecordCanRun(__instance, false, true, Stopwatch.GetTimestamp() - __state);
            return __exception;
        }

        private static void RecordCanRun(QuestScriptDef def, bool result, bool exception, long elapsedTicks)
        {
            canRunCalls++;
            canRunTicks += elapsedTicks;
            ChoiceContext context = currentChoice;
            context.CanRunCalls++;
            context.CanRunTicks += elapsedTicks;

            double ms = ToMs(elapsedTicks);
            if (ms >= SlowThresholdMs) canRunSlow5++;
            if (ms >= 20.0) canRunSlow20++;
            if (ms >= CatastrophicThresholdMs) canRunSlow100++;

            string defName = def.defName ?? "<unnamed>";
            string modName = def.modContentPack != null ? (def.modContentPack.Name ?? "<unnamed-mod>") : "<no-mod>";
            string packageId = def.modContentPack != null ? (def.modContentPack.PackageId ?? "<no-package>") : "<no-package>";
            string key = packageId + "|" + defName;

            QuestStats s;
            if (!stats.TryGetValue(key, out s))
            {
                s = new QuestStats(defName, modName, packageId);
                stats.Add(key, s);
            }
            s.Calls++;
            s.TotalTicks += elapsedTicks;
            if (elapsedTicks > s.MaxTicks) s.MaxTicks = elapsedTicks;
            if (exception) s.Exceptions++;
            else if (result) s.TrueCount++;
            else s.FalseCount++;

            if (ms >= SlowThresholdMs)
            {
                context.AddSlow(new SlowQuestCall(defName, modName, packageId, elapsedTicks, result, exception));
            }
        }

        private static void CompleteChooser(ChooserState state, bool exception)
        {
            if (state == null || state.Completed) return;
            state.Completed = true;
            long elapsed = Stopwatch.GetTimestamp() - state.Started;
            chooserCalls++;
            chooserTicks += elapsed;
            if (elapsed > chooserMaxTicks) chooserMaxTicks = elapsed;

            ChoiceContext context = state.Context;
            currentChoice = state.Previous;
            if (context == null) return;

            double totalMs = ToMs(elapsed);
            if (totalMs < CatastrophicThresholdMs) return;

            chooserCatastrophic++;
            long tick = -1;
            try { tick = Find.TickManager != null ? Find.TickManager.TicksGame : -1; } catch { }

            context.SlowCalls.Sort((a, b) => b.ElapsedTicks.CompareTo(a.ElapsedTicks));
            if (context.SlowCalls.Count > MaxSlowCallsPerCatastrophe)
                context.SlowCalls.RemoveRange(MaxSlowCallsPerCatastrophe, context.SlowCalls.Count - MaxSlowCallsPerCatastrophe);

            recentCatastrophes.Add(new Catastrophe
            {
                Tick = tick,
                TotalTicks = elapsed,
                CanRunTicks = context.CanRunTicks,
                CanRunCalls = context.CanRunCalls,
                Exception = exception,
                SlowCalls = new List<SlowQuestCall>(context.SlowCalls)
            });
            if (recentCatastrophes.Count > MaxRecentCatastrophes)
                recentCatastrophes.RemoveAt(0);
        }

        internal static string Summary()
        {
            var sb = new System.Text.StringBuilder();
            double chooserTotalMs = ToMs(chooserTicks);
            double chooserMaxMs = ToMs(chooserMaxTicks);
            double canRunTotalMs = ToMs(canRunTicks);
            sb.Append("T19 quest deep attribution: installed=").Append(installed)
              .Append(", chooserCalls=").Append(chooserCalls)
              .Append(", chooserTotalMs=").Append(chooserTotalMs.ToString("F2"))
              .Append(", chooserMaxMs=").Append(chooserMaxMs.ToString("F2"))
              .Append(", chooser>=100ms=").Append(chooserCatastrophic)
              .Append(", canRunCalls=").Append(canRunCalls)
              .Append(", canRunTotalMs=").Append(canRunTotalMs.ToString("F2"))
              .Append(", canRun>=5/20/100ms=").Append(canRunSlow5).Append('/').Append(canRunSlow20).Append('/').Append(canRunSlow100)
              .Append(", failures=").Append(failures)
              .Append(". Measurement-only; active only inside NaturalRandomQuestChooser; results are never altered.");

            var ordered = new List<QuestStats>(stats.Values);
            ordered.Sort((a, b) =>
            {
                int m = b.MaxTicks.CompareTo(a.MaxTicks);
                return m != 0 ? m : b.TotalTicks.CompareTo(a.TotalTicks);
            });
            int top = Math.Min(MaxSummaryQuests, ordered.Count);
            for (int i = 0; i < top; i++)
            {
                QuestStats s = ordered[i];
                double total = ToMs(s.TotalTicks);
                double max = ToMs(s.MaxTicks);
                double avg = s.Calls == 0 ? 0.0 : total / s.Calls;
                sb.AppendLine();
                sb.Append("  #").Append(i + 1).Append(" QuestCanRun:").Append(s.DefName)
                  .Append(" | mod=").Append(s.ModName)
                  .Append(" | package=").Append(s.PackageId)
                  .Append(" | calls=").Append(s.Calls)
                  .Append(" | totalMs=").Append(total.ToString("F2"))
                  .Append(" | avgMs=").Append(avg.ToString("F3"))
                  .Append(" | maxMs=").Append(max.ToString("F3"))
                  .Append(" | true/false/ex=").Append(s.TrueCount).Append('/').Append(s.FalseCount).Append('/').Append(s.Exceptions);
            }

            for (int i = 0; i < recentCatastrophes.Count; i++)
            {
                Catastrophe c = recentCatastrophes[i];
                double total = ToMs(c.TotalTicks);
                double cr = ToMs(c.CanRunTicks);
                double residual = Math.Max(0.0, total - cr);
                sb.AppendLine();
                sb.Append("  CAT#").Append(i + 1)
                  .Append(": tick=").Append(c.Tick)
                  .Append(", totalMs=").Append(total.ToString("F2"))
                  .Append(", canRunMs=").Append(cr.ToString("F2"))
                  .Append(", residualMs=").Append(residual.ToString("F2"))
                  .Append(", canRunCalls=").Append(c.CanRunCalls)
                  .Append(", exception=").Append(c.Exception)
                  .Append(", slow=");
                if (c.SlowCalls == null || c.SlowCalls.Count == 0)
                {
                    sb.Append("<none>=5ms");
                }
                else
                {
                    for (int j = 0; j < c.SlowCalls.Count; j++)
                    {
                        if (j != 0) sb.Append(" ; ");
                        SlowQuestCall q = c.SlowCalls[j];
                        sb.Append(q.DefName).Append('@').Append(q.PackageId)
                          .Append(':').Append(ToMs(q.ElapsedTicks).ToString("F2")).Append("ms")
                          .Append(q.Exception ? "[EX]" : (q.Result ? "[T]" : "[F]"));
                    }
                }
            }
            return sb.ToString();
        }

        private static double ToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private sealed class MethodBaseHolder
        {
            internal readonly System.Reflection.MethodBase Chooser;
            internal readonly System.Reflection.MethodBase CanRun;
            internal MethodBaseHolder(System.Reflection.MethodBase chooser, System.Reflection.MethodBase canRun)
            {
                Chooser = chooser;
                CanRun = canRun;
            }
        }

        public sealed class ChoiceContext
        {
            internal long CanRunTicks;
            internal int CanRunCalls;
            internal readonly List<SlowQuestCall> SlowCalls = new List<SlowQuestCall>();

            internal void AddSlow(SlowQuestCall call)
            {
                SlowCalls.Add(call);
            }
        }

        private sealed class QuestStats
        {
            internal readonly string DefName;
            internal readonly string ModName;
            internal readonly string PackageId;
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long TrueCount;
            internal long FalseCount;
            internal long Exceptions;
            internal QuestStats(string defName, string modName, string packageId)
            {
                DefName = defName;
                ModName = modName;
                PackageId = packageId;
            }
        }

        public struct SlowQuestCall
        {
            internal readonly string DefName;
            internal readonly string ModName;
            internal readonly string PackageId;
            internal readonly long ElapsedTicks;
            internal readonly bool Result;
            internal readonly bool Exception;
            internal SlowQuestCall(string defName, string modName, string packageId, long elapsedTicks, bool result, bool exception)
            {
                DefName = defName;
                ModName = modName;
                PackageId = packageId;
                ElapsedTicks = elapsedTicks;
                Result = result;
                Exception = exception;
            }
        }

        private sealed class Catastrophe
        {
            internal long Tick;
            internal long TotalTicks;
            internal long CanRunTicks;
            internal int CanRunCalls;
            internal bool Exception;
            internal List<SlowQuestCall> SlowCalls;
        }
    }
}
