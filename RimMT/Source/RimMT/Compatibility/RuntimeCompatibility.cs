using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class RuntimeCompatibility
    {
        private const string ButterPackageId = "olli.butterplusplus";
        private const string AdaptiveTpsPackageId = "blue.adaptivetps";
        private const string DpaPackageId = "dubwise.dubsperformanceanalyzer";
        private const string DpaSteamPackageId = "dubwise.dubsperformanceanalyzer.steam";

        private static bool initialized;
        private static bool butterPlusPlusActive;
        private static bool adaptiveTpsActive;
        private static bool dubsPerformanceAnalyzerActive;

        // Butter++ 1.5 keeps the manager-level split-tick state here. This is the state
        // RimMT must use to decide whether a worker result may be committed safely.
        private static FieldInfo butterLogicalTickField;
        private static string butterLogicalTickProbeDescription = "not initialized";

        // TickListPatch.MidTick is useful diagnostic information, but it is NOT the
        // TickManager-level logical-tick boundary. V0.4.4 incorrectly looked for this
        // state on TickManagerPatch and could therefore defer the dispatcher forever.
        private static MethodInfo butterTickListMidTickGetter;
        private static FieldInfo butterTickListMidTickField;
        private static string butterTickListProbeDescription = "not initialized";

        internal static bool ButterPlusPlusActive { get { EnsureInitialized(); return butterPlusPlusActive; } }
        internal static bool AdaptiveTPSActive { get { EnsureInitialized(); return adaptiveTpsActive; } }
        internal static bool DubsPerformanceAnalyzerActive { get { EnsureInitialized(); return dubsPerformanceAnalyzerActive; } }
        internal static bool ButterLogicalTickProbeAvailable { get { EnsureInitialized(); return butterLogicalTickField != null; } }
        internal static bool ButterTickListProbeAvailable { get { EnsureInitialized(); return butterTickListMidTickGetter != null || butterTickListMidTickField != null; } }
        internal static string ButterProbeDescription { get { EnsureInitialized(); return butterLogicalTickProbeDescription; } }
        internal static string ButterTickListProbeDescription { get { EnsureInitialized(); return butterTickListProbeDescription; } }

        internal static void Initialize()
        {
            if (initialized)
                return;
            initialized = true;

            butterPlusPlusActive = HasPackage(ButterPackageId) || AccessTools.TypeByName("ButterPlusPlus.TickManagerPatch") != null;
            adaptiveTpsActive = HasPackage(AdaptiveTpsPackageId) || AccessTools.TypeByName("AdaptiveTPS.AdaptiveTickComponent") != null;
            dubsPerformanceAnalyzerActive = HasPackage(DpaPackageId) || HasPackage(DpaSteamPackageId);

            if (!butterPlusPlusActive)
            {
                butterLogicalTickProbeDescription = "Butter++ not loaded";
                butterTickListProbeDescription = "Butter++ not loaded";
                return;
            }

            ProbeButterLogicalTickState();
            ProbeButterTickListState();
        }

        private static void ProbeButterLogicalTickState()
        {
            Type tickManagerPatch = AccessTools.TypeByName("ButterPlusPlus.TickManagerPatch");
            if (tickManagerPatch == null)
            {
                butterLogicalTickProbeDescription = "Butter++ package loaded but TickManagerPatch type was not found";
                return;
            }

            try
            {
                FieldInfo field = AccessTools.Field(tickManagerPatch, "_midTickStarted");
                if (field != null && field.FieldType == typeof(bool) && field.IsStatic)
                {
                    butterLogicalTickField = field;
                    butterLogicalTickProbeDescription = "ButterPlusPlus.TickManagerPatch._midTickStarted";
                    return;
                }

                butterLogicalTickProbeDescription = "Butter++ detected but TickManagerPatch._midTickStarted was not found as a static bool";
            }
            catch (Exception ex)
            {
                butterLogicalTickProbeDescription = "Butter++ logical-tick probe failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void ProbeButterTickListState()
        {
            Type tickListPatch = AccessTools.TypeByName("ButterPlusPlus.TickListPatch");
            if (tickListPatch == null)
            {
                butterTickListProbeDescription = "TickListPatch type was not found";
                return;
            }

            try
            {
                PropertyInfo midTickProperty = AccessTools.Property(tickListPatch, "MidTick");
                if (midTickProperty != null && midTickProperty.PropertyType == typeof(bool))
                {
                    MethodInfo getter = midTickProperty.GetGetMethod(true);
                    if (getter != null && getter.IsStatic)
                    {
                        butterTickListMidTickGetter = getter;
                        butterTickListProbeDescription = "ButterPlusPlus.TickListPatch.MidTick";
                        return;
                    }
                }

                FieldInfo field = AccessTools.Field(tickListPatch, "_midTick");
                if (field != null && field.FieldType == typeof(bool) && field.IsStatic)
                {
                    butterTickListMidTickField = field;
                    butterTickListProbeDescription = "ButterPlusPlus.TickListPatch._midTick";
                    return;
                }

                butterTickListProbeDescription = "TickListPatch detected but no compatible MidTick property/field was found";
            }
            catch (Exception ex)
            {
                butterTickListProbeDescription = "Butter++ TickList diagnostic probe failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        internal static bool TryGetButterLogicalTickInProgress(out bool inProgress)
        {
            EnsureInitialized();
            inProgress = false;
            if (!butterPlusPlusActive)
                return true;
            if (butterLogicalTickField == null)
                return false;

            try
            {
                inProgress = (bool)butterLogicalTickField.GetValue(null);
                return true;
            }
            catch (Exception ex)
            {
                butterLogicalTickField = null;
                butterLogicalTickProbeDescription = "Butter++ logical-tick runtime read failed: " + ex.GetType().Name + ": " + ex.Message;
                inProgress = true;
                return false;
            }
        }

        internal static bool TryGetButterTickListMidTick(out bool midTick)
        {
            EnsureInitialized();
            midTick = false;
            if (!butterPlusPlusActive)
                return true;

            try
            {
                if (butterTickListMidTickGetter != null)
                {
                    midTick = (bool)butterTickListMidTickGetter.Invoke(null, null);
                    return true;
                }
                if (butterTickListMidTickField != null)
                {
                    midTick = (bool)butterTickListMidTickField.GetValue(null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                butterTickListMidTickGetter = null;
                butterTickListMidTickField = null;
                butterTickListProbeDescription = "Butter++ TickList runtime read failed: " + ex.GetType().Name + ": " + ex.Message;
            }
            return false;
        }

        internal static bool IsButterPatch(Patch patch)
        {
            if (patch == null)
                return false;

            MethodInfo method = patch.PatchMethod;
            Type declaringType = method == null ? null : method.DeclaringType;
            string typeName = declaringType == null ? string.Empty : declaringType.FullName;
            string assemblyName = declaringType == null || declaringType.Assembly == null ? string.Empty : declaringType.Assembly.GetName().Name;
            return typeName.StartsWith("ButterPlusPlus.", StringComparison.Ordinal) ||
                   assemblyName.IndexOf("ButterPlusPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (!string.IsNullOrEmpty(patch.owner) && patch.owner.IndexOf("butterplusplus", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static string Summary()
        {
            EnsureInitialized();
            return "Runtime compatibility: Butter++=" + butterPlusPlusActive +
                (butterPlusPlusActive ? " (LogicalTickProbe=" + ButterLogicalTickProbeAvailable + ", source=" + butterLogicalTickProbeDescription +
                    ", TickListProbe=" + ButterTickListProbeAvailable + ", tickListSource=" + butterTickListProbeDescription + ")" : string.Empty) +
                ", AdaptiveTPS=" + adaptiveTpsActive +
                ", DubsPerformanceAnalyzer=" + dubsPerformanceAnalyzerActive;
        }

        private static bool HasPackage(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
                return false;

            var mods = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < mods.Count; i++)
            {
                ModContentPack mod = mods[i];
                if (mod != null && string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void EnsureInitialized()
        {
            if (!initialized)
                Initialize();
        }
    }
}
