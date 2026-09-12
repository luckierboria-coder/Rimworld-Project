using Mono.Cecil;

static class P
{
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

        using var asm = AssemblyDefinition.ReadAssembly(gameAsmPath, new ReaderParameters { ReadSymbols = false });
        ModuleDefinition mod = asm.MainModule;
        int failures = 0;

        TypeDefinition? FindType(string full)
        {
            foreach (TypeDefinition t in mod.Types)
            {
                TypeDefinition? hit = FindRecursive(t, full);
                if (hit != null) return hit;
            }
            return null;
        }

        TypeDefinition? ReqType(string full)
        {
            TypeDefinition? t = FindType(full);
            if (t == null)
            {
                Console.WriteLine("MISSING TYPE    " + full);
                failures++;
            }
            return t;
        }

        void Method(string type, string name, int? argc = null)
        {
            TypeDefinition? t = ReqType(type);
            if (t == null) return;
            var ms = t.Methods.Where(m => m.Name == name && (argc == null || m.Parameters.Count == argc)).ToArray();
            if (ms.Length == 0)
            {
                Console.WriteLine($"MISSING METHOD  {type}.{name}" + (argc == null ? "" : $" argc={argc}"));
                Console.WriteLine("  available: " + string.Join(", ", t.Methods.Select(m => m.Name).Distinct().OrderBy(x => x)));
                failures++;
            }
            else
            {
                Console.WriteLine($"OK METHOD       {type}.{name}: " + string.Join(" | ", ms.Select(Signature)));
            }
        }

        void Field(string type, string name)
        {
            TypeDefinition? t = ReqType(type);
            if (t == null) return;
            FieldDefinition? f = t.Fields.FirstOrDefault(x => x.Name == name);
            if (f == null)
            {
                Console.WriteLine($"MISSING FIELD   {type}.{name}");
                Console.WriteLine("  available: " + string.Join(", ", t.Fields.Select(x => x.Name).OrderBy(x => x)));
                failures++;
            }
            else Console.WriteLine($"OK FIELD        {type}.{name}: {f.FieldType.FullName}");
        }

        // Core detached renderer targets installed unconditionally by the backport.
        Method("Verse.Root_Play", "Update", 0);
        Method("Verse.Map", "MapUpdate", 0);
        Method("Verse.MapComponentUtility", "MapComponentUpdate", 1);
        Field("Verse.CameraDriver", "lastViewRect");
        Field("Verse.CameraDriver", "lastViewRectGetFrame");
        Method("Verse.MapDrawer", "MapMeshDrawerUpdate_First", 0);
        Method("Verse.FleckManager", "FleckManagerDraw", 0);

        // Selection overlay targets used by the Windows renderer feature.
        Method("RimWorld.Selector", "SelectorOnGUI", 0);
        Method("RimWorld.Selector", "Notify_DialogOpened", 0);
        Method("RimWorld.DragBox", "DragBoxOnGUI", 0);

        // TPS boost target.
        Method("Verse.TickManager", "TickManagerUpdate", 0);

        // Camera-control members relied on by the managed 1.5 shim.
        Field("Verse.CameraDriver", "lastViewRect");
        Field("Verse.CameraDriver", "lastViewRectGetFrame");
        Field("Verse.CameraDriver", "panner");

        Console.WriteLine($"AUDIT RESULT failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    static TypeDefinition? FindRecursive(TypeDefinition t, string full)
    {
        if (t.FullName.Replace('/', '+') == full || t.FullName == full) return t;
        foreach (TypeDefinition n in t.NestedTypes)
        {
            TypeDefinition? hit = FindRecursive(n, full);
            if (hit != null) return hit;
        }
        return null;
    }

    static string Signature(MethodDefinition m)
        => $"{m.DeclaringType.FullName}.{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName))})";
}
