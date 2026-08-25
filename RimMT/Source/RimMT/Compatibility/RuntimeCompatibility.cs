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
        private static MethodInfo butterMidTickGetter;
        private static FieldInfo butterMidTickField;
        private static string butterProbeDescription = "not initialized";

        internal static bool ButterPlusPlusActive { get { EnsureInitialized(); return butterPlusPlusActive; } }
        internal static bool AdaptiveTPSActive { get { EnsureInitialized(); return adaptiveTpsActive; } }
        internal static bool DubsPerformanceAnalyzerActive { get { EnsureInitialized(); return dubsPerformanceAnalyzerActive; } }
        internal static bool ButterMidTickProbeAvailable { get { EnsureInitialized(); return butterMidTickGetter != null || butterMidTickField != null; } }
        internal static string ButterProbeDescription { get { EnsureInitialized(); return butterProbeDescription; } }

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
                butterProbeDescription = "Butter++ not loaded";
                return;
            }

            Type tickManagerPatch = AccessTools.TypeByName("ButterPlusPlus.TickManagerPatch");
            if (tickManagerPatch == null)
            {
                butterProbeDescription = "Butter++ package loaded but TickManagerPatch type was not found";
                return;
            }

            try
            {
                PropertyInfo midTickProperty = AccessTools.Property(tickManagerPatch, "MidTick");
                if (midTickProperty != null && midTickProperty.PropertyType == typeof(bool))
                {
                    MethodInfo getter = midTickProperty.GetGetMethod(true);
                    if (getter != null && getter.IsStatic)
                    {
                        butterMidTickGetter = getter;
                        butterProbeDescription = "ButterPlusPlus.TickManagerPatch.MidTick";
                        return;
                    }
                }

                FieldInfo midTickField = AccessTools.Field(tickManagerPatch, "_midTick");
                if (midTickField != null && midTickField.FieldType == typeof(bool) && midTickField.IsStatic)
                {
                    butterMidTickField = midTickField;
                    butterProbeDescription = "ButterPlusPlus.TickManagerPatch._midTick";
                    return;
                }

                butterProbeDescription = "Butter++ detected but no compatible MidTick property/field was found";
            }
            catch (Exception ex)
            {
                butterProbeDescription = "Butter++ MidTick probe failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        internal static bool IsButterMidTick()
        {
            EnsureInitialized();
            if (!butterPlusPlusActive)
                return false;

            try
            {
                if (butterMidTickGetter != null)
                    return (bool)butterMidTickGetter.Invoke(null, null);
                if (butterMidTickField != null)
                    return (bool)butterMidTickField.GetValue(null);
            }
            catch (Exception ex)
            {
                butterMidTickGetter = null;
                butterMidTickField = null;
                butterProbeDescription = "Butter++ MidTick runtime read failed: " + ex.GetType().Name + ": " + ex.Message;
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
                (butterPlusPlusActive ? " (MidTickProbe=" + ButterMidTickProbeAvailable + ", source=" + butterProbeDescription + ")" : string.Empty) +
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
