using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T22 root attribution closes the T1 blind spot around World.WorldTick by patching the
    /// actual runtime boundaries instead of relying on DoSingleTick IL call-site probes.
    /// It also contains a guarded WorldTechLevel v1.1.6 compatibility accelerator: when only
    /// TraitDef cache length is stale, rebuild only TechLevelDatabase&lt;TraitDef&gt; and its
    /// overrides instead of WorldTechLevel's global DefTechLevels.Initialize(). Any reflection,
    /// Harmony-authority or postcondition failure fails open to the original global rebuild.
    /// </summary>
    internal static class WorldRootAttribution093T22
    {
        private const double TickToMs = 1000.0 / Stopwatch.Frequency;
        private const double CatastropheMs = 50.0;
        private const int RecentCapacity = 12;
        private const int WarningKeyCapacity = 32;

        private static readonly object sync = new object();
        private static readonly Dictionary<string, ComponentStat> componentStats = new Dictionary<string, ComponentStat>();
        private static readonly Dictionary<string, WarningStat> warningStats = new Dictionary<string, WarningStat>();
        private static readonly Queue<string> recentWorldTails = new Queue<string>();

        private static bool installed;
        private static int installFailures;
        private static int componentMethodsPatched;
        private static int pawnGeneratorMethodsPatched;

        private static long worldTickCalls;
        private static long worldTickTotalTicks;
        private static long worldTickMaxTicks;
        private static long worldComponentUtilityCalls;
        private static long worldComponentUtilityTotalTicks;
        private static long worldComponentUtilityMaxTicks;
        private static long pawnGeneratorCalls;
        private static long pawnGeneratorTotalTicks;
        private static long pawnGeneratorMaxTicks;

        private static long warningCalls;
        private static long warningTotalTicks;
        private static long warningMaxTicks;
        private static int warningCurrentTick = int.MinValue;
        private static int warningCurrentTickCount;
        private static int warningMaxPerTick;
        private static int warningMaxTick = -1;

        private static long traitDbAdds;
        private static long traitDbRemoves;
        private static long traitDbSetIndicesCalls;
        private static long traitDbSetIndicesTicks;

        private static bool wtlDetected;
        private static bool wtlEnsurePatched;
        private static bool wtlNarrowAuthoritySafe;
        private static bool wtlGlobalInitPatched;
        private static int wtlForeignPatches;
        private static MethodInfo wtlTraitInitialize;
        private static MethodInfo wtlTraitApplyOverrides;
        private static FieldInfo wtlTraitLevels;
        private static long wtlEnsureCalls;
        private static long wtlMismatchEntries;
        private static long wtlNarrowRebuilds;
        private static long wtlNarrowFailures;
        private static long wtlPostconditionFailures;
        private static long wtlGlobalInitCalls;
        private static long wtlGlobalInitTicks;
        private static long wtlGlobalInitMaxTicks;
        private static int wtlLastDefsCount;
        private static int wtlLastLevelsCount;

        [ThreadStatic] private static WorldTickContext currentWorldTick;
        [ThreadStatic] private static int pawnGenerationDepth;

        internal static void Apply(Harmony harmony)
        {
            if (installed) return;
            installed = true;

            try
            {
                MethodBase worldTick = AccessTools.Method(typeof(World), "WorldTick");
                if (worldTick != null)
                    harmony.Patch(worldTick,
                        prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldTickPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldTickPostfix)) { priority = Priority.Last },
                        finalizer: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldTickFinalizer)) { priority = Priority.Last });
                else
                    installFailures++;

                MethodBase worldComponents = AccessTools.Method(typeof(WorldComponentUtility), "WorldComponentTick", new Type[] { typeof(World) });
                if (worldComponents != null)
                    harmony.Patch(worldComponents,
                        prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldComponentUtilityPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldComponentUtilityPostfix)));
                else
                    installFailures++;

                PatchWorldComponentOverrides(harmony);
                PatchPawnGenerator(harmony);
                PatchWarningAggregator(harmony);
                PatchTraitDefDatabaseSignals(harmony);
                PatchWorldTechLevel(harmony);

                Log.Message("[RimMT] T22 world-root attribution active. Direct WorldTick/world-component timing installed; " +
                    "WorldTechLevel TraitDef narrow-rebuild guard detected=" + wtlDetected +
                    ", authoritySafe=" + wtlNarrowAuthoritySafe + ".");
            }
            catch (Exception ex)
            {
                installFailures++;
                Log.Warning("[RimMT] T22 world-root attribution failed partially and will fail open: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchWorldComponentOverrides(Harmony harmony)
        {
            HashSet<MethodBase> seen = new HashSet<MethodBase>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try { types = assemblies[a].GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type == null || type.IsAbstract || !typeof(WorldComponent).IsAssignableFrom(type)) continue;
                    MethodInfo method;
                    try
                    {
                        method = type.GetMethod("WorldComponentTick",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, Type.EmptyTypes, null);
                    }
                    catch { continue; }
                    if (method == null || method.DeclaringType == typeof(WorldComponent) || !seen.Add(method)) continue;
                    try
                    {
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldComponentPrefix)),
                            postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WorldComponentPostfix)));
                        componentMethodsPatched++;
                    }
                    catch { installFailures++; }
                }
            }
        }

        private static void PatchPawnGenerator(Harmony harmony)
        {
            MethodInfo[] methods = typeof(PawnGenerator).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "GeneratePawn" || method.ReturnType != typeof(Pawn)) continue;
                try
                {
                    harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(PawnGeneratePrefix)),
                        postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(PawnGeneratePostfix)),
                        finalizer: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(PawnGenerateFinalizer)));
                    pawnGeneratorMethodsPatched++;
                }
                catch { installFailures++; }
            }
        }

        private static void PatchWarningAggregator(Harmony harmony)
        {
            MethodBase warning = AccessTools.Method(typeof(Log), "Warning", new Type[] { typeof(string) });
            if (warning == null) { installFailures++; return; }
            harmony.Patch(warning,
                prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WarningPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WarningPostfix)) { priority = Priority.Last });
        }

        private static void PatchTraitDefDatabaseSignals(Harmony harmony)
        {
            Type db = typeof(DefDatabase<TraitDef>);
            PatchOptionalNoArgOrAny(harmony, AccessTools.Method(db, "SetIndices"), nameof(TraitSetIndicesPrefix), nameof(TraitSetIndicesPostfix));
            PatchOptionalNoArgOrAny(harmony, AccessTools.Method(db, "Add"), nameof(TraitAddPrefix), null);
            PatchOptionalNoArgOrAny(harmony, AccessTools.Method(db, "Remove"), nameof(TraitRemovePrefix), null);
        }

        private static void PatchOptionalNoArgOrAny(Harmony harmony, MethodBase method, string prefixName, string postfixName)
        {
            if (method == null) return;
            try
            {
                HarmonyMethod prefix = prefixName == null ? null : new HarmonyMethod(typeof(WorldRootAttribution093T22), prefixName);
                HarmonyMethod postfix = postfixName == null ? null : new HarmonyMethod(typeof(WorldRootAttribution093T22), postfixName);
                harmony.Patch(method, prefix: prefix, postfix: postfix);
            }
            catch { installFailures++; }
        }

        private static void PatchWorldTechLevel(Harmony harmony)
        {
            try
            {
                Type genericDb = AccessTools.TypeByName("WorldTechLevel.TechLevelDatabase`1");
                Type global = AccessTools.TypeByName("WorldTechLevel.DefTechLevels");
                if (genericDb == null || global == null) return;
                wtlDetected = true;

                Type traitDb = genericDb.MakeGenericType(typeof(TraitDef));
                MethodInfo ensure = AccessTools.Method(traitDb, "EnsureInitialized");
                wtlTraitInitialize = AccessTools.Method(traitDb, "Initialize");
                wtlTraitApplyOverrides = AccessTools.Method(traitDb, "ApplyOverrides");
                wtlTraitLevels = AccessTools.Field(traitDb, "Levels");
                MethodInfo globalInit = AccessTools.Method(global, "Initialize");

                if (ensure != null && wtlTraitInitialize != null && wtlTraitApplyOverrides != null && wtlTraitLevels != null)
                {
                    wtlNarrowAuthoritySafe = IsAuthoritySafeForNarrowWtl(ensure);
                    harmony.Patch(ensure,
                        prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WtlTraitEnsurePrefix)) { priority = Priority.First });
                    wtlEnsurePatched = true;
                }

                if (globalInit != null)
                {
                    harmony.Patch(globalInit,
                        prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WtlGlobalInitPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WtlGlobalInitPostfix)));
                    wtlGlobalInitPatched = true;
                }
            }
            catch
            {
                wtlNarrowAuthoritySafe = false;
                installFailures++;
            }
        }

        private static bool IsAuthoritySafeForNarrowWtl(MethodBase ensure)
        {
            try
            {
                Patches patches = Harmony.GetPatchInfo(ensure);
                if (patches == null) return true;
                int foreign = 0;
                foreach (Patch p in patches.Prefixes) if (p != null && p.owner != RimMTBootstrap.HarmonyId) foreign++;
                foreach (Patch p in patches.Postfixes) if (p != null && p.owner != RimMTBootstrap.HarmonyId) foreign++;
                foreach (Patch p in patches.Transpilers) if (p != null && p.owner != RimMTBootstrap.HarmonyId) foreign++;
                foreach (Patch p in patches.Finalizers) if (p != null && p.owner != RimMTBootstrap.HarmonyId) foreign++;
                wtlForeignPatches = foreign;
                return foreign == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void WorldTickPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
            currentWorldTick = new WorldTickContext
            {
                StartWarnings = Interlocked.Read(ref warningCalls),
                StartPawnGen = Interlocked.Read(ref pawnGeneratorCalls),
                StartWtlGlobal = Interlocked.Read(ref wtlGlobalInitCalls),
                StartWtlNarrow = Interlocked.Read(ref wtlNarrowRebuilds)
            };
        }

        public static void WorldTickPostfix(long __state)
        {
            FinishWorldTick(__state, null);
        }

        public static Exception WorldTickFinalizer(Exception __exception, long __state)
        {
            if (__exception != null) FinishWorldTick(__state, __exception);
            return __exception;
        }

        private static void FinishWorldTick(long start, Exception exception)
        {
            if (start == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            Interlocked.Increment(ref worldTickCalls);
            Interlocked.Add(ref worldTickTotalTicks, elapsed);
            Max(ref worldTickMaxTicks, elapsed);

            WorldTickContext context = currentWorldTick;
            currentWorldTick = null;
            if (context == null) return;
            double ms = elapsed * TickToMs;
            if (ms < CatastropheMs && exception == null) return;

            long warnings = Interlocked.Read(ref warningCalls) - context.StartWarnings;
            long pawns = Interlocked.Read(ref pawnGeneratorCalls) - context.StartPawnGen;
            long globals = Interlocked.Read(ref wtlGlobalInitCalls) - context.StartWtlGlobal;
            long narrows = Interlocked.Read(ref wtlNarrowRebuilds) - context.StartWtlNarrow;
            int tick = SafeTick();
            string text = "tick=" + tick + ", worldMs=" + ms.ToString("F2") +
                ", topComponent=" + (context.TopComponent ?? "none") + ":" + context.TopComponentMs.ToString("F2") + "ms" +
                ", warnings=" + warnings + ", pawnGen=" + pawns +
                ", wtlGlobalInit=" + globals + ", wtlNarrow=" + narrows +
                (exception == null ? "" : ", exception=" + exception.GetType().Name);
            lock (sync)
            {
                while (recentWorldTails.Count >= RecentCapacity) recentWorldTails.Dequeue();
                recentWorldTails.Enqueue(text);
            }
        }

        public static void WorldComponentUtilityPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void WorldComponentUtilityPostfix(long __state)
        {
            if (__state == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            Interlocked.Increment(ref worldComponentUtilityCalls);
            Interlocked.Add(ref worldComponentUtilityTotalTicks, elapsed);
            Max(ref worldComponentUtilityMaxTicks, elapsed);
        }

        public static void WorldComponentPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void WorldComponentPostfix(MethodBase __originalMethod, long __state)
        {
            if (__state == 0L || __originalMethod == null) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            string name = (__originalMethod.DeclaringType == null ? "<unknown>" : __originalMethod.DeclaringType.FullName);
            lock (sync)
            {
                ComponentStat stat;
                if (!componentStats.TryGetValue(name, out stat))
                {
                    stat = new ComponentStat(name);
                    componentStats.Add(name, stat);
                }
                stat.Calls++;
                stat.TotalTicks += elapsed;
                if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
            }
            WorldTickContext context = currentWorldTick;
            if (context != null)
            {
                double ms = elapsed * TickToMs;
                if (ms > context.TopComponentMs)
                {
                    context.TopComponentMs = ms;
                    context.TopComponent = name;
                }
            }
        }

        public static void PawnGeneratePrefix(ref long __state)
        {
            pawnGenerationDepth++;
            __state = pawnGenerationDepth == 1 ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void PawnGeneratePostfix(long __state)
        {
            FinishPawnGenerate(__state);
        }

        public static Exception PawnGenerateFinalizer(Exception __exception, long __state)
        {
            if (__exception != null) FinishPawnGenerate(__state);
            return __exception;
        }

        private static void FinishPawnGenerate(long start)
        {
            try
            {
                if (start != 0L)
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    Interlocked.Increment(ref pawnGeneratorCalls);
                    Interlocked.Add(ref pawnGeneratorTotalTicks, elapsed);
                    Max(ref pawnGeneratorMaxTicks, elapsed);
                }
            }
            finally
            {
                if (pawnGenerationDepth > 0) pawnGenerationDepth--;
            }
        }

        public static void WarningPrefix(string text, ref WarningCallState __state)
        {
            __state = new WarningCallState { Start = Stopwatch.GetTimestamp(), Text = text };
        }

        public static void WarningPostfix(WarningCallState __state)
        {
            if (__state.Start == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Start;
            Interlocked.Increment(ref warningCalls);
            Interlocked.Add(ref warningTotalTicks, elapsed);
            Max(ref warningMaxTicks, elapsed);
            int tick = SafeTick();
            lock (sync)
            {
                if (tick != warningCurrentTick)
                {
                    if (warningCurrentTickCount > warningMaxPerTick)
                    {
                        warningMaxPerTick = warningCurrentTickCount;
                        warningMaxTick = warningCurrentTick;
                    }
                    warningCurrentTick = tick;
                    warningCurrentTickCount = 0;
                }
                warningCurrentTickCount++;

                string key = NormalizeWarning(__state.Text);
                WarningStat stat;
                if (warningStats.TryGetValue(key, out stat))
                {
                    stat.Calls++;
                    stat.TotalTicks += elapsed;
                    if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
                }
                else if (warningStats.Count < WarningKeyCapacity)
                {
                    stat = new WarningStat(key) { Calls = 1, TotalTicks = elapsed, MaxTicks = elapsed };
                    warningStats.Add(key, stat);
                }
            }
        }

        public static void TraitSetIndicesPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void TraitSetIndicesPostfix(long __state)
        {
            Interlocked.Increment(ref traitDbSetIndicesCalls);
            if (__state != 0L) Interlocked.Add(ref traitDbSetIndicesTicks, Stopwatch.GetTimestamp() - __state);
        }

        public static void TraitAddPrefix() { Interlocked.Increment(ref traitDbAdds); }
        public static void TraitRemovePrefix() { Interlocked.Increment(ref traitDbRemoves); }

        public static bool WtlTraitEnsurePrefix()
        {
            Interlocked.Increment(ref wtlEnsureCalls);
            if (!wtlNarrowAuthoritySafe || wtlTraitLevels == null || wtlTraitInitialize == null || wtlTraitApplyOverrides == null)
                return true;

            try
            {
                int defsCount = DefDatabase<TraitDef>.AllDefsListForReading.Count;
                Array levels = wtlTraitLevels.GetValue(null) as Array;
                int levelsCount = levels == null ? 0 : levels.Length;
                wtlLastDefsCount = defsCount;
                wtlLastLevelsCount = levelsCount;
                if (levelsCount <= 0 || defsCount == levelsCount) return true;

                Interlocked.Increment(ref wtlMismatchEntries);

                ParameterInfo[] pars = wtlTraitInitialize.GetParameters();
                if (pars.Length == 0) wtlTraitInitialize.Invoke(null, null);
                else wtlTraitInitialize.Invoke(null, new object[] { null });
                wtlTraitApplyOverrides.Invoke(null, null);

                Array after = wtlTraitLevels.GetValue(null) as Array;
                int afterCount = after == null ? 0 : after.Length;
                wtlLastLevelsCount = afterCount;
                if (afterCount != defsCount)
                {
                    Interlocked.Increment(ref wtlPostconditionFailures);
                    return true;
                }

                Interlocked.Increment(ref wtlNarrowRebuilds);
                return false;
            }
            catch
            {
                Interlocked.Increment(ref wtlNarrowFailures);
                return true;
            }
        }

        public static void WtlGlobalInitPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void WtlGlobalInitPostfix(long __state)
        {
            if (__state == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            Interlocked.Increment(ref wtlGlobalInitCalls);
            Interlocked.Add(ref wtlGlobalInitTicks, elapsed);
            Max(ref wtlGlobalInitMaxTicks, elapsed);
        }

        internal static string Summary()
        {
            List<ComponentStat> components;
            List<WarningStat> warnings;
            string[] tails;
            lock (sync)
            {
                if (warningCurrentTickCount > warningMaxPerTick)
                {
                    warningMaxPerTick = warningCurrentTickCount;
                    warningMaxTick = warningCurrentTick;
                }
                components = new List<ComponentStat>(componentStats.Values);
                warnings = new List<WarningStat>(warningStats.Values);
                tails = recentWorldTails.ToArray();
            }
            components.Sort(delegate(ComponentStat a, ComponentStat b) { return b.MaxTicks.CompareTo(a.MaxTicks); });
            warnings.Sort(delegate(WarningStat a, WarningStat b) { return b.Calls.CompareTo(a.Calls); });

            System.Text.StringBuilder sb = new System.Text.StringBuilder(4096);
            sb.Append("T22 world-root attribution: installed=").Append(installed)
                .Append(", installFailures=").Append(installFailures)
                .Append(", componentMethods=").Append(componentMethodsPatched)
                .Append(", pawnGeneratorMethods=").Append(pawnGeneratorMethodsPatched)
                .Append(", WorldTick[calls=").Append(Interlocked.Read(ref worldTickCalls))
                .Append(",totalMs=").Append((Interlocked.Read(ref worldTickTotalTicks) * TickToMs).ToString("F2"))
                .Append(",maxMs=").Append((Interlocked.Read(ref worldTickMaxTicks) * TickToMs).ToString("F2")).Append(']')
                .Append(", WorldComponentUtility[calls=").Append(Interlocked.Read(ref worldComponentUtilityCalls))
                .Append(",totalMs=").Append((Interlocked.Read(ref worldComponentUtilityTotalTicks) * TickToMs).ToString("F2"))
                .Append(",maxMs=").Append((Interlocked.Read(ref worldComponentUtilityMaxTicks) * TickToMs).ToString("F2")).Append(']')
                .Append(", PawnGenerator[calls=").Append(Interlocked.Read(ref pawnGeneratorCalls))
                .Append(",totalMs=").Append((Interlocked.Read(ref pawnGeneratorTotalTicks) * TickToMs).ToString("F2"))
                .Append(",maxMs=").Append((Interlocked.Read(ref pawnGeneratorMaxTicks) * TickToMs).ToString("F2")).Append(']')
                .Append(", warnings[calls=").Append(Interlocked.Read(ref warningCalls))
                .Append(",totalMs=").Append((Interlocked.Read(ref warningTotalTicks) * TickToMs).ToString("F2"))
                .Append(",maxMs=").Append((Interlocked.Read(ref warningMaxTicks) * TickToMs).ToString("F2"))
                .Append(",maxPerTick=").Append(warningMaxPerTick).Append("@tick=").Append(warningMaxTick).Append(']')
                .Append(", TraitDefDB[add=").Append(Interlocked.Read(ref traitDbAdds))
                .Append(",remove=").Append(Interlocked.Read(ref traitDbRemoves))
                .Append(",setIndices=").Append(Interlocked.Read(ref traitDbSetIndicesCalls))
                .Append(",setIndicesMs=").Append((Interlocked.Read(ref traitDbSetIndicesTicks) * TickToMs).ToString("F2")).Append(']')
                .AppendLine();

            sb.Append("T22 WorldTechLevel bridge: detected=").Append(wtlDetected)
                .Append(", ensurePatched=").Append(wtlEnsurePatched)
                .Append(", narrowAuthoritySafe=").Append(wtlNarrowAuthoritySafe)
                .Append(", foreignPatches=").Append(wtlForeignPatches)
                .Append(", globalInitPatched=").Append(wtlGlobalInitPatched)
                .Append(", ensureCalls=").Append(Interlocked.Read(ref wtlEnsureCalls))
                .Append(", mismatchEntries=").Append(Interlocked.Read(ref wtlMismatchEntries))
                .Append(", narrowRebuilds=").Append(Interlocked.Read(ref wtlNarrowRebuilds))
                .Append(", narrowFailures=").Append(Interlocked.Read(ref wtlNarrowFailures))
                .Append(", postconditionFailures=").Append(Interlocked.Read(ref wtlPostconditionFailures))
                .Append(", globalInitCalls=").Append(Interlocked.Read(ref wtlGlobalInitCalls))
                .Append(", globalInitTotalMs=").Append((Interlocked.Read(ref wtlGlobalInitTicks) * TickToMs).ToString("F2"))
                .Append(", globalInitMaxMs=").Append((Interlocked.Read(ref wtlGlobalInitMaxTicks) * TickToMs).ToString("F2"))
                .Append(", lastDefs/levels=").Append(wtlLastDefsCount).Append('/').Append(wtlLastLevelsCount)
                .AppendLine(". Narrow rebuild touches only TechLevelDatabase<TraitDef>.Initialize + ApplyOverrides; any failure falls back to WorldTechLevel original.");

            int componentLimit = Math.Min(8, components.Count);
            for (int i = 0; i < componentLimit; i++)
            {
                ComponentStat s = components[i];
                sb.Append("  WorldComp#").Append(i + 1).Append(' ').Append(s.Name)
                    .Append(": calls=").Append(s.Calls)
                    .Append(", totalMs=").Append((s.TotalTicks * TickToMs).ToString("F2"))
                    .Append(", maxMs=").Append((s.MaxTicks * TickToMs).ToString("F2")).AppendLine();
            }

            int warningLimit = Math.Min(6, warnings.Count);
            for (int i = 0; i < warningLimit; i++)
            {
                WarningStat s = warnings[i];
                sb.Append("  Warning#").Append(i + 1).Append(": calls=").Append(s.Calls)
                    .Append(", totalMs=").Append((s.TotalTicks * TickToMs).ToString("F2"))
                    .Append(", maxMs=").Append((s.MaxTicks * TickToMs).ToString("F2"))
                    .Append(", text=").Append(s.Text).AppendLine();
            }

            for (int i = 0; i < tails.Length; i++)
                sb.Append("  WORLDTAIL#").Append(i + 1).Append(": ").AppendLine(tails[i]);
            return sb.ToString().TrimEnd();
        }

        private static string NormalizeWarning(string text)
        {
            if (string.IsNullOrEmpty(text)) return "<empty>";
            string oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
            return oneLine.Length <= 180 ? oneLine : oneLine.Substring(0, 180);
        }

        private static int SafeTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }

        private static void Max(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current) return;
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        internal sealed class WorldTickContext
        {
            internal long StartWarnings;
            internal long StartPawnGen;
            internal long StartWtlGlobal;
            internal long StartWtlNarrow;
            internal string TopComponent;
            internal double TopComponentMs;
        }

        internal sealed class ComponentStat
        {
            internal readonly string Name;
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal ComponentStat(string name) { Name = name; }
        }

        internal sealed class WarningStat
        {
            internal readonly string Text;
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal WarningStat(string text) { Text = text; }
        }

        internal struct WarningCallState
        {
            internal long Start;
            internal string Text;
        }
    }
}
