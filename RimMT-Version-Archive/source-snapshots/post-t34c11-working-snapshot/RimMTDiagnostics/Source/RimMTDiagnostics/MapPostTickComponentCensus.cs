using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT.Diagnostics
{
    /// <summary>Optional attribution for the multi-second MapPostTick envelope.</summary>
    internal static class MapPostTickComponentCensus
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static int patchedComponents;
        private static int patchedForeignPostfixes;
        private static int failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            HashSet<MethodBase> seen = new HashSet<MethodBase>();
            try
            {
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
                        if (type == null || type.IsAbstract || !typeof(MapComponent).IsAssignableFrom(type)) continue;
                        MethodInfo method;
                        try
                        {
                            method = type.GetMethod("MapComponentTick",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, Type.EmptyTypes, null);
                        }
                        catch { continue; }
                        if (method == null || method.IsAbstract || method.DeclaringType != type || !seen.Add(method)) continue;
                        Patch(harmony, method);
                        patchedComponents++;
                    }
                }

                Type staggered = AccessTools.TypeByName("StaggeredRaids.Map_MapPostTick_Patch");
                MethodInfo postfix = staggered == null ? null : AccessTools.Method(staggered, "Postfix");
                if (postfix != null && seen.Add(postfix))
                {
                    Patch(harmony, postfix);
                    patchedForeignPostfixes++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warning("[RimMT Diagnostics] MapPostTick component census partial install: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Patch(Harmony harmony, MethodBase target)
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(MapPostTickComponentCensus), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(MapPostTickComponentCensus), nameof(Postfix)) { priority = Priority.Last });
        }

        public static void Prefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Postfix(MethodBase __originalMethod, long __state)
        {
            if (__state == 0L || __originalMethod == null) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            if (elapsed <= 0L) return;
            long us = (long)(elapsed * (1000000.0 / Stopwatch.Frequency));
            string owner = __originalMethod.DeclaringType == null ? "<unknown>" : __originalMethod.DeclaringType.FullName;
            string key = owner + "." + __originalMethod.Name;
            lock (Sync)
            {
                Stat stat;
                if (!Stats.TryGetValue(key, out stat))
                {
                    stat = new Stat();
                    Stats.Add(key, stat);
                }
                stat.Calls++;
                stat.TotalUs += us;
                if (us > stat.MaxUs) stat.MaxUs = us;
                if (us >= 5000L) stat.Over5++;
                if (us >= 20000L) stat.Over20++;
                if (us >= 50000L) stat.Over50++;
            }
        }

        internal static string Summary()
        {
            if (patchedComponents == 0 && patchedForeignPostfixes == 0)
            {
                return "MapPostTick component census: DISABLED. Patching every loaded MapComponent override at bootstrap can JIT mod methods before a game world exists; this triggered MoreFactionInteraction.FactionInteractionTimeSeperator and caused an initialization failure on every map tick. Aggregate Map.MapPostTick timing remains active.\n";
            }
            List<KeyValuePair<string, Stat>> rows;
            lock (Sync)
            {
                rows = Stats.OrderByDescending(kv => kv.Value.MaxUs)
                    .ThenByDescending(kv => kv.Value.TotalUs).Take(20).ToList();
            }
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("MapPostTick component census: patched[components/foreignPostfixes]=")
              .Append(patchedComponents).Append('/').Append(patchedForeignPostfixes)
              .Append(", observed=").Append(Stats.Count).Append(", failures=").Append(failures).AppendLine();
            if (rows.Count == 0)
            {
                sb.AppendLine("MapPostTick component top: none");
                return sb.ToString();
            }
            sb.Append("MapPostTick component top: ");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i != 0) sb.Append("; ");
                Stat s = rows[i].Value;
                sb.Append(rows[i].Key).Append("[calls=").Append(s.Calls)
                  .Append(",totalMs=").Append((s.TotalUs / 1000.0).ToString("F2"))
                  .Append(",maxMs=").Append((s.MaxUs / 1000.0).ToString("F2"))
                  .Append(",>5/20/50=").Append(s.Over5).Append('/').Append(s.Over20).Append('/').Append(s.Over50)
                  .Append(']');
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private sealed class Stat
        {
            internal long Calls;
            internal long TotalUs;
            internal long MaxUs;
            internal long Over5;
            internal long Over20;
            internal long Over50;
        }
    }
}
