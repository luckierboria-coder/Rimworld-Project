using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// Diagnostics-only regression probe for the retired T34-D aggressive scanner route.
    /// T34-D worker validators were observed entering main-thread-only Reachability from worker
    /// context; T35-A production must not contain or register that route.
    /// </summary>
    internal static class AggressiveScannerRegressionProbeT35A
    {
        private const string RetiredFeatureId = "parallel.aggressiveScanner";

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(2048);
            Assembly rimmt = FindRimMT();
            if (rimmt == null)
            {
                sb.AppendLine("T35-A aggressiveScanner regression probe: RimMT assembly not found.");
                return sb.ToString();
            }

            List<string> suspiciousTypes = new List<string>();
            List<string> featureOwners = new List<string>();
            string productionVersion = "unknown";

            Type[] types;
            try { types = rimmt.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch
            {
                sb.AppendLine("T35-A aggressiveScanner regression probe: type enumeration failed.");
                return sb.ToString();
            }

            if (types != null)
            {
                for (int i = 0; i < types.Length; i++)
                {
                    Type type = types[i];
                    if (type == null) continue;
                    string name = type.FullName ?? type.Name ?? string.Empty;
                    if (name.IndexOf("AggressiveParallelScanner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("T34D", StringComparison.OrdinalIgnoreCase) >= 0)
                        suspiciousTypes.Add(name);

                    FieldInfo[] fields;
                    try { fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }
                    for (int f = 0; f < fields.Length; f++)
                    {
                        FieldInfo field = fields[f];
                        if (field == null || field.FieldType != typeof(string) || !field.IsLiteral) continue;
                        string value = null;
                        try { value = field.GetRawConstantValue() as string; }
                        catch { }
                        if (string.Equals(value, RetiredFeatureId, StringComparison.Ordinal))
                            featureOwners.Add(name + "." + field.Name);
                        if (string.Equals(field.Name, "Version", StringComparison.Ordinal) &&
                            name.EndsWith("RimMTBootstrap", StringComparison.Ordinal) &&
                            !string.IsNullOrEmpty(value))
                            productionVersion = value;
                    }
                }
            }

            bool detected = suspiciousTypes.Count != 0 || featureOwners.Count != 0;
            sb.Append("T35-A aggressiveScanner regression probe: productionVersion=").Append(productionVersion)
              .Append(", retiredFeatureDetected=").Append(detected)
              .Append(", suspiciousTypes=").Append(suspiciousTypes.Count)
              .Append(", featureLiterals=").Append(featureOwners.Count).AppendLine();

            if (suspiciousTypes.Count != 0)
                sb.Append(" aggressiveScanner types: ").Append(string.Join("; ", suspiciousTypes.ToArray())).AppendLine();
            if (featureOwners.Count != 0)
                sb.Append(" aggressiveScanner feature owners: ").Append(string.Join("; ", featureOwners.ToArray())).AppendLine();

            if (detected)
                sb.AppendLine(" WARNING: retired worker-validator scanner route is present. This route previously allowed a WorkGiver validator to enter Reachability.CanReach from a worker and could abort job search before Vanilla fallback.");
            else
                sb.AppendLine(" aggressiveScanner status: ABSENT. T35-A keeps arbitrary WorkGiver validators on the main thread; workers are restricted to primitive/snapshot preprocessing.");

            return sb.ToString();
        }

        private static Assembly FindRimMT()
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly != null && string.Equals(assembly.GetName().Name, "RimMT", StringComparison.Ordinal))
                        return assembly;
                }
            }
            catch { }
            return null;
        }
    }
}
