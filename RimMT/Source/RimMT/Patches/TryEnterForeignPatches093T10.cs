using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T10 diagnostic-only probes for the exact foreign TryEnterNextPathCell postfix methods seen in T9.
    /// The foreign methods are timed directly; no unpatch/repatch ordering, transpiler, result mutation or skip is used.
    /// </summary>
    internal static class TryEnterForeignPatches093T10
    {
        private const string PfOwner = "pathfinding.framework";
        private const string VfeOwner = "OskarPotocki.VFECore";

        internal static void Apply(Harmony harmony)
        {
            bool pfFound = false, pfTimed = false, vfeFound = false, vfeTimed = false;
            bool terrainUpdatedHook = false, graphicsDirtyHook = false, phasingHook = false, floodHook = false;

            if (harmony == null)
            {
                TryEnterForeignAttribution093T10.SetInstallState(false, false, false, false, false, false, false, false);
                return;
            }

            try
            {
                MethodBase tryEnter = AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell");
                Patches info = tryEnter == null ? null : Harmony.GetPatchInfo(tryEnter);
                Patch pfPatch = null;
                Patch vfePatch = null;
                if (info != null && info.Postfixes != null)
                {
                    foreach (Patch patch in info.Postfixes)
                    {
                        if (patch == null || patch.PatchMethod == null) continue;
                        string owner = patch.owner ?? string.Empty;
                        MethodInfo pm = patch.PatchMethod;
                        string typeName = pm.DeclaringType == null ? string.Empty : pm.DeclaringType.FullName ?? string.Empty;
                        if (pfPatch == null && string.Equals(owner, PfOwner, StringComparison.Ordinal) &&
                            string.Equals(pm.Name, "Postfix", StringComparison.Ordinal) &&
                            typeName.IndexOf("TerrainTag", StringComparison.OrdinalIgnoreCase) >= 0)
                            pfPatch = patch;
                        if (vfePatch == null && string.Equals(owner, VfeOwner, StringComparison.Ordinal) &&
                            string.Equals(pm.Name, "UnfogEnteredCells", StringComparison.Ordinal))
                            vfePatch = patch;
                    }
                }

                pfFound = pfPatch != null && pfPatch.PatchMethod != null;
                vfeFound = vfePatch != null && vfePatch.PatchMethod != null;

                if (pfFound)
                {
                    pfTimed = PatchDirect(harmony, pfPatch.PatchMethod, nameof(PfPrefix), nameof(PfPostfix));
                    Assembly asm = pfPatch.PatchMethod.DeclaringType == null ? null : pfPatch.PatchMethod.DeclaringType.Assembly;
                    if (asm != null)
                    {
                        Type graphicContext = asm.GetType("PathfindingFramework.PawnGraphic.GraphicContext", false);
                        if (graphicContext != null)
                        {
                            MethodBase terrainUpdated = AccessTools.Method(graphicContext, "TerrainUpdated");
                            MethodBase setDirty = AccessTools.Method(graphicContext, "SetAllGraphicsDirty");
                            terrainUpdatedHook = PatchPrefixOnly(harmony, terrainUpdated, nameof(TerrainUpdatedPrefix));
                            graphicsDirtyHook = PatchPrefixOnly(harmony, setDirty, nameof(GraphicsDirtyPrefix));
                        }
                    }
                }

                if (vfeFound)
                {
                    vfeTimed = PatchDirect(harmony, vfePatch.PatchMethod, nameof(VfePrefix), nameof(VfePostfix));
                    Assembly asm = vfePatch.PatchMethod.DeclaringType == null ? null : vfePatch.PatchMethod.DeclaringType.Assembly;
                    MethodBase isPhasing = FindIsPhasing(asm);
                    if (isPhasing != null)
                    {
                        try
                        {
                            harmony.Patch(isPhasing,
                                postfix: new HarmonyMethod(typeof(TryEnterForeignPatches093T10), nameof(IsPhasingPostfix)) { priority = Priority.Last });
                            phasingHook = true;
                        }
                        catch { phasingHook = false; }
                    }

                    Type fogGrid = AccessTools.TypeByName("Verse.FogGrid");
                    MethodBase flood = fogGrid == null ? null :
                        (AccessTools.Method(fogGrid, "FloodUnfogAdjacent", new Type[] { typeof(IntVec3) }) ?? AccessTools.Method(fogGrid, "FloodUnfogAdjacent"));
                    floodHook = PatchPrefixOnly(harmony, flood, nameof(FloodPrefix));
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T10 TryEnter foreign attribution setup failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }

            TryEnterForeignAttribution093T10.SetInstallState(pfFound, pfTimed, vfeFound, vfeTimed,
                terrainUpdatedHook, graphicsDirtyHook, phasingHook, floodHook);
            Log.Message("[RimMT] T10 TryEnter foreign-postfix attribution installed: PF=" + pfFound + "/" + pfTimed +
                ", VFE=" + vfeFound + "/" + vfeTimed + ", PFhelpers=" + terrainUpdatedHook + "/" + graphicsDirtyHook +
                ", VFEhelpers=" + phasingHook + "/" + floodHook + ". Diagnostic-only; owner order/results unchanged.");
        }

        private static bool PatchDirect(Harmony harmony, MethodBase target, string prefixName, string postfixName)
        {
            if (target == null) return default(bool);
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(TryEnterForeignPatches093T10), prefixName) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(TryEnterForeignPatches093T10), postfixName) { priority = Priority.Last });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T10 direct foreign timer failed closed for " + target.DeclaringType + "." + target.Name + ": " + ex.GetType().Name);
                return default(bool);
            }
        }

        private static bool PatchPrefixOnly(Harmony harmony, MethodBase target, string prefixName)
        {
            if (target == null) return default(bool);
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(TryEnterForeignPatches093T10), prefixName) { priority = Priority.First });
                return true;
            }
            catch { return default(bool); }
        }

        private static MethodBase FindIsPhasing(Assembly asm)
        {
            if (asm == null) return null;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
            catch { return null; }

            foreach (Type t in types)
            {
                if (t == null || (t.FullName ?? string.Empty).IndexOf("Phasing", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!string.Equals(m.Name, "IsPhasing", StringComparison.Ordinal) || m.ReturnType != typeof(bool)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Pawn)) return m;
                }
            }
            return null;
        }

        public static void PfPrefix(Pawn __0, ref long __state)
        {
            __state = TryEnterForeignAttribution093T10.BeginPathfindingFramework(__0);
        }

        public static void PfPostfix(long __state)
        {
            TryEnterForeignAttribution093T10.EndPathfindingFramework(__state);
        }

        public static void VfePrefix(Pawn_PathFollower __0, Pawn __1, ref long __state)
        {
            __state = TryEnterForeignAttribution093T10.BeginVfe(__0, __1);
        }

        public static void VfePostfix(long __state)
        {
            TryEnterForeignAttribution093T10.EndVfe(__state);
        }

        public static void TerrainUpdatedPrefix(TerrainDef __0, TerrainDef __1)
        {
            TryEnterForeignAttribution093T10.NoteTerrainUpdated(__0, __1);
        }

        public static void GraphicsDirtyPrefix()
        {
            TryEnterForeignAttribution093T10.NoteGraphicsDirty();
        }

        public static void IsPhasingPostfix(bool __result)
        {
            TryEnterForeignAttribution093T10.NotePhasingResult(__result);
        }

        public static void FloodPrefix()
        {
            TryEnterForeignAttribution093T10.NoteFloodUnfog();
        }
    }
}
