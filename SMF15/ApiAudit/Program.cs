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
        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (string dll in Directory.GetFiles(refDir, "*.dll"))
            {
                try { modules.Add(AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadSymbols = false }).MainModule); }
                catch (BadImageFormatException) { }
            }

            int failures = 0;

            TypeDefinition? FindType(string full)
            {
                foreach (ModuleDefinition mod in modules)
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
                var ms = AllMethods(t).Where(m => m.Name == name && (argc == null || m.Parameters.Count == argc)).ToArray();
                if (ms.Length == 0)
                {
                    Console.WriteLine($"MISSING METHOD  {type}.{name}" + (argc == null ? "" : $" argc={argc}"));
                    failures++;
                }
                else Console.WriteLine($"OK METHOD       {type}.{name}: " + string.Join(" | ", ms.Select(Signature)));
            }

            void Field(string type, string name)
            {
                TypeDefinition? t = ReqType(type);
                if (t == null) return;
                FieldDefinition? f = AllFields(t).FirstOrDefault(x => x.Name == name);
                if (f == null)
                {
                    Console.WriteLine($"MISSING FIELD   {type}.{name}");
                    failures++;
                }
                else Console.WriteLine($"OK FIELD        {type}.{name}: {f.FieldType.FullName}");
            }

            void Property(string type, string name, bool requireGetter = true)
            {
                TypeDefinition? t = ReqType(type);
                if (t == null) return;
                PropertyDefinition? p = AllProperties(t).FirstOrDefault(x => x.Name == name);
                if (p == null || (requireGetter && p.GetMethod == null))
                {
                    Console.WriteLine($"MISSING PROPERTY {type}.{name}" + (requireGetter ? " getter" : ""));
                    failures++;
                }
                else Console.WriteLine($"OK PROPERTY     {type}.{name}: {p.PropertyType.FullName}");
            }

            // Coverage capture: all unconditional vanilla targets and private cache fields.
            Method("Verse.Root_Play", "Update", 0);
            Method("Verse.Map", "MapUpdate", 0);
            Method("Verse.MapComponentUtility", "MapComponentUpdate", 1);
            Field("Verse.CameraDriver", "lastViewRect");
            Field("Verse.CameraDriver", "lastViewRectGetFrame");
            Method("Verse.MapDrawer", "MapMeshDrawerUpdate_First", 0);
            Method("Verse.FleckManager", "FleckManagerDraw", 0);

            // GUI capture / performance overlay.
            Method("UnityEngine.GUIUtility", "BeginGUI", 3);
            Method("UnityEngine.GUIUtility", "EndGUI", 1);
            Method("UnityEngine.GUIUtility", "EndGUIFromException", 1);
            Property("UnityEngine.GUIUtility", "textFieldInput");
            Method("RimWorld.ThingOverlays", "ThingOverlaysOnGUI", 0);
            Method("RimWorld.GlobalControlsUtility", "DoDate");
            Property("UnityEngine.Material", "rawRenderQueue");
            foreach (string p in new[] { "blendMaterial", "blitMaterial", "roundedRectMaterial", "roundedRectWithColorPerBorderMaterial" })
                Property("UnityEngine.GUI", p);

            // Camera ownership: private fields and dynamically resolved members used before detached motion starts.
            foreach (string f in new[] {
                "rootPos", "rootSize", "velocity", "desiredDolly", "desiredDollyRaw", "desiredSize",
                "mouseTouchingScreenBottomEdgeStartTime", "dragTimeStamps", "panner"
            }) Field("Verse.CameraDriver", f);
            Property("Verse.CameraDriver", "AnythingPreventsCameraMotion");
            Property("Verse.CameraDriver", "ScrollWheelZoomRate");
            Method("Verse.CameraDriver", "Update", 0);
            Method("Verse.CameraDriver", "CameraDriverOnGUI", 0);
            Method("UnityEngine.LowLevel.PlayerLoop", "SetPlayerLoop", 1);

            // Selection overlay.
            Method("RimWorld.Selector", "SelectorOnGUI", 0);
            Method("RimWorld.Selector", "Notify_DialogOpened", 0);
            Method("RimWorld.DragBox", "DragBoxOnGUI", 0);

            // TPS boost.
            Method("Verse.TickManager", "TickManagerUpdate", 0);

            Console.WriteLine($"AUDIT RESULT failures={failures}");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            foreach (ModuleDefinition m in modules) m.Assembly.Dispose();
        }
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

    static IEnumerable<TypeDefinition> BaseChain(TypeDefinition t)
    {
        for (TypeDefinition? cur = t; cur != null;)
        {
            yield return cur;
            if (cur.BaseType == null) yield break;
            try { cur = cur.BaseType.Resolve(); } catch { yield break; }
        }
    }
    static IEnumerable<MethodDefinition> AllMethods(TypeDefinition t) => BaseChain(t).SelectMany(x => x.Methods);
    static IEnumerable<FieldDefinition> AllFields(TypeDefinition t) => BaseChain(t).SelectMany(x => x.Fields);
    static IEnumerable<PropertyDefinition> AllProperties(TypeDefinition t) => BaseChain(t).SelectMany(x => x.Properties);

    static string Signature(MethodDefinition m)
        => $"{m.DeclaringType.FullName}.{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName))})";
}
