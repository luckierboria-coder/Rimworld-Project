using Mono.Cecil;
using Mono.Cecil.Cil;

static class P
{
    static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: ApiAudit <RimWorld ref/net472 directory> [staged Smf.Mod.dll]");
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
                if (t == null) { Console.WriteLine("MISSING TYPE    " + full); failures++; }
                return t;
            }
            void Method(string type, string name, int? argc = null)
            {
                TypeDefinition? t = ReqType(type); if (t == null) return;
                var ms = AllMethods(t).Where(m => m.Name == name && (argc == null || m.Parameters.Count == argc)).ToArray();
                if (ms.Length == 0) { Console.WriteLine($"MISSING METHOD  {type}.{name}" + (argc == null ? "" : $" argc={argc}")); failures++; }
                else Console.WriteLine($"OK METHOD       {type}.{name}: " + string.Join(" | ", ms.Select(Signature)));
            }
            void Field(string type, string name)
            {
                TypeDefinition? t = ReqType(type); if (t == null) return;
                FieldDefinition? f = AllFields(t).FirstOrDefault(x => x.Name == name);
                if (f == null) { Console.WriteLine($"MISSING FIELD   {type}.{name}"); failures++; }
                else Console.WriteLine($"OK FIELD        {type}.{name}: {f.FieldType.FullName}");
            }
            void Property(string type, string name)
            {
                TypeDefinition? t = ReqType(type); if (t == null) return;
                PropertyDefinition? p = AllProperties(t).FirstOrDefault(x => x.Name == name);
                if (p == null || p.GetMethod == null) { Console.WriteLine($"MISSING PROPERTY {type}.{name} getter"); failures++; }
                else Console.WriteLine($"OK PROPERTY     {type}.{name}: {p.PropertyType.FullName}");
            }

            Method("Verse.Root_Play", "Update", 0);
            Method("Verse.Map", "MapUpdate", 0);
            Method("Verse.MapComponentUtility", "MapComponentUpdate", 1);
            Field("Verse.CameraDriver", "lastViewRect");
            Field("Verse.CameraDriver", "lastViewRectGetFrame");
            Method("Verse.MapDrawer", "MapMeshDrawerUpdate_First", 0);
            Method("Verse.FleckManager", "FleckManagerDraw", 0);

            Method("UnityEngine.GUIUtility", "BeginGUI", 3);
            Method("UnityEngine.GUIUtility", "EndGUI", 1);
            Method("UnityEngine.GUIUtility", "EndGUIFromException", 1);
            Property("UnityEngine.GUIUtility", "textFieldInput");
            Method("Verse.ThingOverlays", "ThingOverlaysOnGUI", 0);
            Method("RimWorld.GlobalControlsUtility", "DoDate");
            Property("UnityEngine.Material", "rawRenderQueue");
            foreach (string p in new[] { "blendMaterial", "blitMaterial", "roundedRectMaterial", "roundedRectWithColorPerBorderMaterial" }) Property("UnityEngine.GUI", p);

            foreach (string f in new[] { "rootPos", "rootSize", "velocity", "desiredDolly", "desiredDollyRaw", "desiredSize", "mouseTouchingScreenBottomEdgeStartTime", "dragTimeStamps", "panner" }) Field("Verse.CameraDriver", f);
            Property("Verse.CameraDriver", "AnythingPreventsCameraMotion");
            Property("Verse.CameraDriver", "ScrollWheelZoomRate");
            Method("Verse.CameraDriver", "Update", 0);
            Method("Verse.CameraDriver", "CameraDriverOnGUI", 0);
            Method("UnityEngine.LowLevel.PlayerLoop", "SetPlayerLoop", 1);

            Method("RimWorld.Selector", "SelectorOnGUI", 0);
            Method("RimWorld.Selector", "Notify_DialogOpened", 0);
            Method("RimWorld.DragBox", "DragBoxOnGUI", 0);
            Method("Verse.TickManager", "TickManagerUpdate", 0);

            if (args.Length == 2)
            {
                string smfPath = Path.GetFullPath(args[1]);
                if (!File.Exists(smfPath)) { Console.WriteLine("MISSING STAGED ASSEMBLY " + smfPath); failures++; }
                else
                {
                    using var smf = AssemblyDefinition.ReadAssembly(smfPath, new ReaderParameters { ReadSymbols = false });
                    TypeDefinition? coverage = smf.MainModule.Types.FirstOrDefault(t => t.FullName == "SimplyMoreFPS.Rendering.MapCoverageCapture");
                    MethodDefinition? install = coverage?.Methods.FirstOrDefault(m => m.Name == "InstallHooks");
                    string[] strings = install?.Body?.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToArray() ?? Array.Empty<string>();
                    if (!strings.Contains("MapComponentUpdate")) { Console.WriteLine("STAGED IL MISSING MapComponentUpdate"); failures++; }
                    else Console.WriteLine("OK STAGED IL     MapCoverageCapture.InstallHooks -> MapComponentUpdate");
                    if (strings.Contains("MapComponentOnDraw")) { Console.WriteLine("STAGED IL LEAK   MapComponentOnDraw"); failures++; }
                    TypeDefinition? shader = smf.MainModule.Types.FirstOrDefault(t => t.FullName == "SimplyMoreFPS.Rendering.Shaders.GuiShaderBundleAssembler");
                    FieldDefinition? uv = shader?.Fields.FirstOrDefault(f => f.Name == "UnityVersion");
                    if (uv?.Constant as string != "2019.4.30f1") { Console.WriteLine("STAGED IL WRONG UnityVersion"); failures++; }
                    else Console.WriteLine("OK STAGED IL     UnityVersion=2019.4.30f1");
                }
            }

            Console.WriteLine($"AUDIT RESULT failures={failures}");
            return failures == 0 ? 0 : 1;
        }
        finally { foreach (ModuleDefinition m in modules) m.Assembly.Dispose(); }
    }

    static TypeDefinition? FindRecursive(TypeDefinition t, string full)
    {
        if (t.FullName.Replace('/', '+') == full || t.FullName == full) return t;
        foreach (TypeDefinition n in t.NestedTypes) { TypeDefinition? hit = FindRecursive(n, full); if (hit != null) return hit; }
        return null;
    }
    static IEnumerable<TypeDefinition> BaseChain(TypeDefinition t)
    {
        for (TypeDefinition? cur = t; cur != null;) { yield return cur; if (cur.BaseType == null) yield break; try { cur = cur.BaseType.Resolve(); } catch { yield break; } }
    }
    static IEnumerable<MethodDefinition> AllMethods(TypeDefinition t) => BaseChain(t).SelectMany(x => x.Methods);
    static IEnumerable<FieldDefinition> AllFields(TypeDefinition t) => BaseChain(t).SelectMany(x => x.Fields);
    static IEnumerable<PropertyDefinition> AllProperties(TypeDefinition t) => BaseChain(t).SelectMany(x => x.Properties);
    static string Signature(MethodDefinition m) => $"{m.DeclaringType.FullName}.{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName))})";
}
