using System.Reflection;

static class P
{
    static readonly BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static int Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: ApiAudit <RimWorld ref/net472 directory>");
            return 2;
        }

        string refDir = Path.GetFullPath(args[0]);
        string gameAsmPath = Path.Combine(refDir, "Assembly-CSharp.dll");
        if (!File.Exists(gameAsmPath))
        {
            Console.Error.WriteLine("Assembly-CSharp.dll not found: " + gameAsmPath);
            return 2;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in Directory.GetFiles(refDir, "*.dll")) paths.Add(Path.GetFullPath(p));
        string? tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (tpa != null)
            foreach (string p in tpa.Split(Path.PathSeparator)) paths.Add(p);

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        Assembly asm = mlc.LoadFromAssemblyPath(gameAsmPath);
        int failures = 0;

        Type ReqType(string full)
        {
            Type? t = asm.GetType(full, false);
            if (t == null)
            {
                Console.WriteLine("MISSING TYPE  " + full);
                failures++;
                throw new InvalidOperationException("required type missing: " + full);
            }
            return t;
        }

        void Method(string type, string name, int? argc = null)
        {
            Type t;
            try { t = ReqType(type); } catch { return; }
            var ms = t.GetMethods(Any).Where(m => m.Name == name && (argc == null || m.GetParameters().Length == argc)).ToArray();
            if (ms.Length == 0)
            {
                Console.WriteLine($"MISSING METHOD {type}.{name}" + (argc == null ? "" : $" argc={argc}"));
                Console.WriteLine("  available: " + string.Join(", ", t.GetMethods(Any).Select(m => m.Name).Distinct().OrderBy(x => x)));
                failures++;
            }
            else
            {
                Console.WriteLine($"OK METHOD      {type}.{name}: " + string.Join(" | ", ms.Select(Signature)));
            }
        }

        void Field(string type, string name)
        {
            Type t;
            try { t = ReqType(type); } catch { return; }
            FieldInfo? f = t.GetField(name, Any);
            if (f == null)
            {
                Console.WriteLine($"MISSING FIELD  {type}.{name}");
                Console.WriteLine("  available: " + string.Join(", ", t.GetFields(Any).Select(x => x.Name).OrderBy(x => x)));
                failures++;
            }
            else Console.WriteLine($"OK FIELD       {type}.{name}: {f.FieldType.FullName}");
        }

        // Core detached-renderer targets installed unconditionally.
        Method("Verse.Root_Play", "Update", 0);
        Method("Verse.Map", "MapUpdate", 0);
        Method("Verse.MapComponentUtility", "MapComponentOnDraw", 1);
        Field("Verse.CameraDriver", "lastViewRect");
        Field("Verse.CameraDriver", "lastViewRectGetFrame");
        Method("Verse.MapDrawer", "MapMeshDrawerUpdate_First", 0);
        Method("Verse.FleckManager", "FleckManagerDraw", 0);

        // Selection overlay hooks installed when the Windows native renderer exports the feature.
        Method("RimWorld.Selector", "SelectorOnGUI", 0);
        Method("RimWorld.Selector", "Notify_DialogOpened", 0);
        Method("RimWorld.DragBox", "DragBoxOnGUI", 0);

        // Frame-budget hook used by TPS Boost.
        Method("Verse.TickManager", "TickManagerUpdate", 0);

        Console.WriteLine($"AUDIT RESULT failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    static string Signature(MethodInfo m)
        => $"{m.DeclaringType?.FullName}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))})";
}
