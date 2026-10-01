using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimThreadedTTR
{
    /// <summary>
    /// Vanilla 1.5 can draw selected fleck systems in parallel (FleckSystemBase.Draw),
    /// but still simulates them serially (Update/Tick walk every fleck on the main
    /// thread). During rain, snow, big fights or big fires, fleck counts reach the
    /// thousands, so this fork parallelizes the simulation loop with the same
    /// GenThreading slicing vanilla itself uses for drawing.
    ///
    /// Safety model:
    /// - Fleck structs are updated in place at disjoint indices (no cross-element
    ///   access), so slicing the list across threads is data-race free.
    /// - Removal indices are collected per-slice and merged in globally descending
    ///   order, matching what RemoveBatchUnordered expects.
    /// - Rand calls inside fleck code hit the thread-local Rand layer (RandPatches).
    /// - Landing sounds are marshaled to the main thread (SoundPatches).
    /// - Only fleck systems that do NOT override Update/Tick are patched, and by
    ///   default only systems whose class lives in the vanilla game assembly are
    ///   run in parallel (modded systems opt in via mod settings).
    /// </summary>
    public class ModdedFleckSystemEntry
    {
        public Type type;
        public string settingsKey; // type full name, used in the settings dictionary
        public string label;       // "Mod Name - TypeName" shown in settings
    }

    public static class FleckRegistry
    {
        // System types eligible for parallel simulation, with a flag for
        // whether the type is from the vanilla assembly.
        public static readonly Dictionary<Type, bool> eligibleSystems = new Dictionary<Type, bool>();

        // Non-vanilla systems found at startup; each gets its own opt-in
        // checkbox in mod settings (all OFF by default).
        public static readonly List<ModdedFleckSystemEntry> moddedSystems = new List<ModdedFleckSystemEntry>();

        // Runtime kill switches flipped if a parallel run ever throws.
        public static bool runtimeDisabled;
        public static bool drawRuntimeDisabled;

        // Diagnostic counters (main thread only); read by the self-test and
        // shown in mod settings.
        public static int parallelRunCount;
        public static int drawParallelRunCount;

        // Per-type cache: does this system already use vanilla's own parallel
        // drawing (ParallelizedDrawing == true, e.g. FleckSystemThrown)?
        private static readonly Dictionary<Type, bool> vanillaParallelDraw = new Dictionary<Type, bool>();

        public static void RegisterAndPatchAll(Harmony harmony)
        {
            Assembly vanillaAssembly = typeof(FleckSystem).Assembly;
            HashSet<Type> seenSystems = new HashSet<Type>();
            HashSet<MethodBase> patchedMethods = new HashSet<MethodBase>();
            int patched = 0;

            List<FleckDef> defs = DefDatabase<FleckDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                Type sysType = defs[i].fleckSystemClass;
                if (sysType == null || !seenSystems.Add(sysType))
                {
                    continue;
                }

                // Find the closed FleckSystemBase<T> ancestor.
                Type closedBase = null;
                Type walk = sysType;
                while (walk != null && walk != typeof(object))
                {
                    if (walk.IsGenericType && walk.GetGenericTypeDefinition() == typeof(FleckSystemBase<>))
                    {
                        closedBase = walk;
                        break;
                    }
                    walk = walk.BaseType;
                }
                if (closedBase == null)
                {
                    continue; // Fully custom system; leave it alone.
                }

                // If the concrete system overrides Update or Tick, its behavior is
                // unknown - skip it entirely (the base-method patch would not fire
                // for it anyway, but do not mark it eligible either).
                MethodInfo update = AccessTools.Method(sysType, "Update", Type.EmptyTypes);
                MethodInfo tick = AccessTools.Method(sysType, "Tick", Type.EmptyTypes);
                MethodInfo draw = AccessTools.Method(sysType, "Draw", new Type[] { typeof(DrawBatch) });
                if (update == null || tick == null || update.DeclaringType != closedBase || tick.DeclaringType != closedBase)
                {
                    continue;
                }
                bool drawSafe = draw != null && draw.DeclaringType == closedBase;

                bool isVanilla = sysType.Assembly == vanillaAssembly;
                eligibleSystems[sysType] = isVanilla;
                if (!isVanilla)
                {
                    ModdedFleckSystemEntry entry = new ModdedFleckSystemEntry();
                    entry.type = sysType;
                    entry.settingsKey = sysType.FullName;
                    entry.label = FindModName(sysType.Assembly) + " - " + sysType.Name;
                    moddedSystems.Add(entry);
                }

                // Patch each closed generic base method once. TFleck is a struct,
                // so every instantiation has its own method body under Mono and
                // must be patched individually.
                Type fleckType = closedBase.GetGenericArguments()[0];
                Type patchClass = typeof(FleckPatch<>).MakeGenericType(fleckType);
                MethodInfo baseUpdate = AccessTools.DeclaredMethod(closedBase, "Update", Type.EmptyTypes);
                MethodInfo baseTick = AccessTools.DeclaredMethod(closedBase, "Tick", Type.EmptyTypes);

                if (baseUpdate != null && patchedMethods.Add(baseUpdate))
                {
                    harmony.Patch(baseUpdate, new HarmonyMethod(patchClass.GetMethod("UpdatePrefix")), null, null, null);
                    patched++;
                }
                if (baseTick != null && patchedMethods.Add(baseTick))
                {
                    harmony.Patch(baseTick, new HarmonyMethod(patchClass.GetMethod("TickPrefix")), null, null, null);
                    patched++;
                }
                if (drawSafe)
                {
                    MethodInfo baseDraw = AccessTools.DeclaredMethod(closedBase, "Draw", new Type[] { typeof(DrawBatch) });
                    if (baseDraw != null && patchedMethods.Add(baseDraw))
                    {
                        harmony.Patch(baseDraw, new HarmonyMethod(patchClass.GetMethod("DrawPrefix")), null, null, null);
                        patched++;
                    }
                }
            }

            Log.Message("[RimThreadedTTR] Parallel fleck simulation: " + eligibleSystems.Count + " eligible system types ("
                + moddedSystems.Count + " from other mods, individually opt-in), " + patched + " methods patched.");
        }

        private static string FindModName(Assembly assembly)
        {
            try
            {
                List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
                for (int i = 0; i < mods.Count; i++)
                {
                    ModAssemblyHandler handler = mods[i].assemblies;
                    if (handler == null || handler.loadedAssemblies == null)
                    {
                        continue;
                    }
                    if (handler.loadedAssemblies.Contains(assembly))
                    {
                        return mods[i].Name;
                    }
                }
            }
            catch (Exception)
            {
            }
            return assembly.GetName().Name;
        }

        // Vanilla systems: always allowed. Modded systems: only if the user
        // enabled that specific system in mod settings.
        private static bool IsSystemAllowed(Type sysType, bool isVanilla)
        {
            if (isVanilla)
            {
                return true;
            }
            return TTRMod.Instance.settings.IsModdedSystemEnabled(sysType.FullName);
        }

        public static bool ShouldRunParallel(FleckSystem system, int fleckCount)
        {
            TTRSettings settings = TTRMod.Instance.settings;
            if (runtimeDisabled || !settings.parallelFlecks)
            {
                return false;
            }
            if (fleckCount < settings.fleckThreshold)
            {
                return false;
            }
            bool isVanilla;
            if (!eligibleSystems.TryGetValue(system.GetType(), out isVanilla))
            {
                return false;
            }
            return IsSystemAllowed(system.GetType(), isVanilla);
        }

        public static bool ShouldRunParallelDraw(FleckSystem system, int fleckCount)
        {
            TTRSettings settings = TTRMod.Instance.settings;
            if (drawRuntimeDisabled || !settings.parallelFleckDraw)
            {
                return false;
            }
            if (fleckCount < settings.fleckThreshold)
            {
                return false;
            }
            Type sysType = system.GetType();
            bool isVanilla;
            if (!eligibleSystems.TryGetValue(sysType, out isVanilla))
            {
                return false;
            }
            if (!IsSystemAllowed(sysType, isVanilla))
            {
                return false;
            }
            // Leave systems that vanilla already draws in parallel
            // (e.g. FleckSystemThrown) to vanilla's own code.
            bool alreadyParallel;
            if (!vanillaParallelDraw.TryGetValue(sysType, out alreadyParallel))
            {
                System.Reflection.PropertyInfo prop = AccessTools.Property(sysType, "ParallelizedDrawing");
                alreadyParallel = prop != null && (bool)prop.GetValue(system, null);
                vanillaParallelDraw[sysType] = alreadyParallel;
            }
            return !alreadyParallel;
        }
    }

    public static class FleckPatch<T> where T : struct, IFleck
    {
        private static readonly FieldInfo dataRealtimeField = AccessTools.DeclaredField(typeof(FleckSystemBase<T>), "dataRealtime");
        private static readonly FieldInfo dataGametimeField = AccessTools.DeclaredField(typeof(FleckSystemBase<T>), "dataGametime");

        // Replaces FleckSystemBase<T>.Update() - real-time flecks on RimWorld 1.5.
        public static bool UpdatePrefix(FleckSystemBase<T> __instance)
        {
            List<T> data = (List<T>)dataRealtimeField.GetValue(__instance);
            return RunSerialInstead(__instance, data, UnityEngine.Time.deltaTime);
        }

        // Replaces FleckSystemBase<T>.Tick() - game-time flecks (vanilla uses a
        // fixed 1/60s step here).
        public static bool TickPrefix(FleckSystemBase<T> __instance)
        {
            List<T> data = (List<T>)dataGametimeField.GetValue(__instance);
            return RunSerialInstead(__instance, data, 1f / 60f);
        }

        // Returns true to fall through to vanilla serial code, false when the
        // parallel path already did the work.
        private static bool RunSerialInstead(FleckSystemBase<T> system, List<T> data, float deltaTime)
        {
            if (data == null || !FleckRegistry.ShouldRunParallel(system, data.Count))
            {
                return true;
            }
            try
            {
                RunParallel(system, data, deltaTime);
                return false;
            }
            catch (Exception ex)
            {
                FleckRegistry.runtimeDisabled = true;
                Log.Warning("[RimThreadedTTR] Parallel fleck simulation failed and has been disabled for this session: " + ex);
                return true;
            }
        }

        private static void RunParallel(FleckSystemBase<T> system, List<T> data, float deltaTime)
        {
            FleckRegistry.parallelRunCount++;
            Map map = (system.parent != null) ? system.parent.parent : null;
            int workers = TTRMod.Instance.settings.MaxThreadsClamped;
            List<GenThreading.Slice> slices = GenThreading.SliceWork(0, data.Count, workers);
            List<int>[] removeLists = new List<int>[slices.Count];

            GenThreading.ParallelFor(0, slices.Count, delegate(int sliceIndex)
            {
                GenThreading.Slice slice = slices[sliceIndex];
                List<int> removes = null;
                // Iterate backward inside the slice so collected indices are
                // descending, matching vanilla's removal order.
                for (int i = slice.toExclusive - 1; i >= slice.fromInclusive; i--)
                {
                    T value = data[i];
                    if (value.TimeInterval(deltaTime, map))
                    {
                        if (removes == null)
                        {
                            removes = new List<int>();
                        }
                        removes.Add(i);
                    }
                    else
                    {
                        data[i] = value;
                    }
                }
                removeLists[sliceIndex] = removes;
            }, workers);

            // Merge per-slice descending lists from the highest slice down so the
            // combined list is globally descending, as RemoveBatchUnordered expects.
            List<int> merged = null;
            for (int s = slices.Count - 1; s >= 0; s--)
            {
                List<int> part = removeLists[s];
                if (part != null && part.Count > 0)
                {
                    if (merged == null)
                    {
                        merged = new List<int>();
                    }
                    merged.AddRange(part);
                }
            }
            if (merged != null)
            {
                data.RemoveBatchUnordered(merged);
            }

            // Anything workers queued (e.g. landing sounds) plays now, this frame.
            MainThreadQueue.Drain();
        }

        // ---------------------------------------------------------------
        // Parallel drawing (v1.1). Vanilla parallel-draws only systems with
        // ParallelizedDrawing == true (FleckSystemThrown); everything else -
        // notably FleckSystemStatic (rain splashes, impacts) - draws on one
        // core. This replicates vanilla's own DrawParallel pattern (pooled
        // per-worker DrawBatch, merged on the main thread) for those systems.
        // ---------------------------------------------------------------

        private static readonly WaitCallback drawWorkerCallback = DrawWorker;

        // Replaces FleckSystemBase<T>.Draw(DrawBatch) when eligible on RimWorld 1.5.
        public static bool DrawPrefix(FleckSystemBase<T> __instance, DrawBatch drawBatch)
        {
            List<T> realtime = (List<T>)dataRealtimeField.GetValue(__instance);
            List<T> gametime = (List<T>)dataGametimeField.GetValue(__instance);
            int total = ((realtime != null) ? realtime.Count : 0) + ((gametime != null) ? gametime.Count : 0);
            if (!FleckRegistry.ShouldRunParallelDraw(__instance, total))
            {
                return true;
            }
            try
            {
                // Graphics must be initialized on the main thread before any
                // worker touches them (mirrors vanilla Draw).
                List<FleckDef> defs = __instance.handledDefs;
                for (int i = 0; i < defs.Count; i++)
                {
                    if (defs[i].graphicData != null)
                    {
                        defs[i].graphicData.ExplicitlyInitCachedGraphic();
                    }
                    if (defs[i].randomGraphics != null)
                    {
                        for (int j = 0; j < defs[i].randomGraphics.Count; j++)
                        {
                            defs[i].randomGraphics[j].ExplicitlyInitCachedGraphic();
                        }
                    }
                }
                FleckRegistry.drawParallelRunCount++;
                DrawListParallel(realtime, drawBatch);
                DrawListParallel(gametime, drawBatch);
                return false;
            }
            catch (Exception ex)
            {
                FleckRegistry.drawRuntimeDisabled = true;
                Log.Warning("[RimThreadedTTR] Parallel fleck drawing failed and has been disabled for this session: " + ex);
                return true;
            }
        }

        private static void DrawListParallel(List<T> data, DrawBatch outerBatch)
        {
            if (data == null || data.Count == 0)
            {
                return;
            }
            int workers = TTRMod.Instance.settings.MaxThreadsClamped;
            List<GenThreading.Slice> slices = GenThreading.SliceWork(0, data.Count, workers);
            List<FleckParallelizationInfo> infos = new List<FleckParallelizationInfo>(slices.Count);
            try
            {
                for (int i = 0; i < slices.Count; i++)
                {
                    FleckParallelizationInfo info = FleckUtility.GetParallelizationInfo();
                    info.startIndex = slices[i].fromInclusive;
                    info.endIndex = slices[i].toExclusive;
                    info.data = data;
                    ThreadPool.QueueUserWorkItem(drawWorkerCallback, info);
                    infos.Add(info);
                }
                for (int i = 0; i < infos.Count; i++)
                {
                    infos[i].doneEvent.WaitOne();
                    outerBatch.MergeWith(infos[i].drawBatch);
                }
            }
            finally
            {
                for (int i = 0; i < infos.Count; i++)
                {
                    FleckUtility.ReturnParallelizationInfo(infos[i]);
                }
            }
        }

        private static void DrawWorker(object state)
        {
            FleckParallelizationInfo info = (FleckParallelizationInfo)state;
            try
            {
                List<T> list = (List<T>)info.data;
                for (int i = info.startIndex; i < info.endIndex; i++)
                {
                    T value = list[i];
                    value.Draw(info.drawBatch);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[RimThreadedTTR] Error in parallel fleck draw worker: " + ex);
            }
            finally
            {
                info.doneEvent.Set();
            }
        }
    }
}
