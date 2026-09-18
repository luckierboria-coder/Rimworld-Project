using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CoinageDllPatcher <input Coinage.dll> <output Coinage.dll>");
    return 2;
}

string input = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
var rp = new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver };
var asm = AssemblyDefinition.ReadAssembly(input, rp);
var mod = asm.MainModule;

// Find Assembly-CSharp scope already referenced by Coinage.dll.
var gameScope = mod.AssemblyReferences.FirstOrDefault(a => a.Name == "Assembly-CSharp")
    ?? throw new InvalidOperationException("Assembly-CSharp reference not found in Coinage.dll.");

// Create helper: Coinage.XmlDrivenCoinRecipeValues.GetOutputCount()
// It reads RecipeDef Mill_Coinage.targetCountAdjustment at runtime.
// No yield constant is retained in Coinage.dll.
var helperType = mod.Types.FirstOrDefault(t => t.Namespace == "Coinage" && t.Name == "XmlDrivenCoinRecipeValues");
if (helperType != null)
    mod.Types.Remove(helperType);

helperType = new TypeDefinition(
    "Coinage",
    "XmlDrivenCoinRecipeValues",
    TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
    mod.TypeSystem.Object);
mod.Types.Add(helperType);

var helper = new MethodDefinition(
    "GetOutputCount",
    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
    mod.TypeSystem.Int32);
helperType.Methods.Add(helper);
helper.Body.InitLocals = true;

// Type refs.
var recipeDef = new TypeReference("Verse", "RecipeDef", mod, gameScope);
var defDatabaseOpen = new TypeReference("Verse", "DefDatabase\u00601", mod, gameScope);
var defDatabaseRecipe = new GenericInstanceType(defDatabaseOpen);
defDatabaseRecipe.GenericArguments.Add(recipeDef);

// static T GetNamedSilentFail(string defName)
var getNamedSilentFail = new MethodReference("GetNamedSilentFail", recipeDef, defDatabaseRecipe)
{
    HasThis = false
};
getNamedSilentFail.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));

// RecipeDef.targetCountAdjustment
var targetCountAdjustment = new FieldReference("targetCountAdjustment", mod.TypeSystem.Int32, recipeDef);

// InvalidOperationException(string)
var invalidOp = new TypeReference("System", "InvalidOperationException", mod, mod.TypeSystem.CoreLibrary);
var invalidCtor = new MethodReference(".ctor", mod.TypeSystem.Void, invalidOp) { HasThis = true };
invalidCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));

var recipeVar = new VariableDefinition(recipeDef);
var countVar = new VariableDefinition(mod.TypeSystem.Int32);
helper.Body.Variables.Add(recipeVar);
helper.Body.Variables.Add(countVar);

var il = helper.Body.GetILProcessor();
var haveRecipe = Instruction.Create(OpCodes.Ldloc, recipeVar);
var haveCount = Instruction.Create(OpCodes.Ldloc, countVar);

il.Append(Instruction.Create(OpCodes.Ldstr, "Mill_Coinage"));
il.Append(Instruction.Create(OpCodes.Call, getNamedSilentFail));
il.Append(Instruction.Create(OpCodes.Stloc, recipeVar));
il.Append(Instruction.Create(OpCodes.Ldloc, recipeVar));
il.Append(Instruction.Create(OpCodes.Brtrue_S, haveRecipe));
il.Append(Instruction.Create(OpCodes.Ldstr, "Coinage: RecipeDef 'Mill_Coinage' was not loaded when coin defs were generated."));
il.Append(Instruction.Create(OpCodes.Newobj, invalidCtor));
il.Append(Instruction.Create(OpCodes.Throw));
il.Append(haveRecipe);
il.Append(Instruction.Create(OpCodes.Ldfld, targetCountAdjustment));
il.Append(Instruction.Create(OpCodes.Stloc, countVar));
il.Append(Instruction.Create(OpCodes.Ldloc, countVar));
il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
il.Append(Instruction.Create(OpCodes.Bgt_S, haveCount));
il.Append(Instruction.Create(OpCodes.Ldstr, "Coinage: Mill_Coinage.targetCountAdjustment must be > 0."));
il.Append(Instruction.Create(OpCodes.Newobj, invalidCtor));
il.Append(Instruction.Create(OpCodes.Throw));
il.Append(haveCount);
il.Append(Instruction.Create(OpCodes.Ret));

// Locate ONLY the old hard-coded coin output count in CoinDefGenerator:
// ldc.i4.s 50 immediately feeding ThingDefCountClass::.ctor.
var candidates = new List<(MethodDefinition Method, Instruction Inst)>();
foreach (var type in mod.Types.SelectMany(AllTypes))
{
    foreach (var method in type.Methods)
    {
        if (!method.HasBody) continue;
        var ins = method.Body.Instructions;
        for (int i = 0; i < ins.Count; i++)
        {
            var x = ins[i];
            int? value = x.OpCode.Code switch
            {
                Code.Ldc_I4_S => (sbyte)x.Operand,
                Code.Ldc_I4 => (int)x.Operand,
                Code.Ldc_I4_0 => 0,
                Code.Ldc_I4_1 => 1,
                Code.Ldc_I4_2 => 2,
                Code.Ldc_I4_3 => 3,
                Code.Ldc_I4_4 => 4,
                Code.Ldc_I4_5 => 5,
                Code.Ldc_I4_6 => 6,
                Code.Ldc_I4_7 => 7,
                Code.Ldc_I4_8 => 8,
                Code.Ldc_I4_M1 => -1,
                _ => null
            };
            if (value != 50) continue;

            bool ctorSoon = false;
            for (int j = i + 1; j < Math.Min(ins.Count, i + 5); j++)
            {
                if (ins[j].Operand is MethodReference mr &&
                    ins[j].OpCode.Code == Code.Newobj &&
                    mr.Name == ".ctor" &&
                    mr.DeclaringType.Name.Contains("ThingDefCountClass", StringComparison.Ordinal))
                {
                    ctorSoon = true;
                    break;
                }
            }

            if (ctorSoon)
                candidates.Add((method, x));
        }
    }
}

Console.WriteLine("Hard-coded 50 candidates feeding ThingDefCountClass ctor:");
foreach (var c in candidates)
    Console.WriteLine("  " + c.Method.FullName + " @ IL_" + c.Inst.Offset.ToString("X4"));

if (candidates.Count != 1)
    throw new InvalidOperationException($"Expected exactly one hard-coded coin-yield site, found {candidates.Count}.");

var target = candidates[0];
if (!target.Method.FullName.Contains("CoinDefGenerator", StringComparison.Ordinal))
    throw new InvalidOperationException("The unique candidate is not inside CoinDefGenerator; refusing to patch.");

// Replace stack-producing constant with stack-producing call.
target.Inst.OpCode = OpCodes.Call;
target.Inst.Operand = helper;

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
asm.Write(output);

// Re-open and verify.
using var verify = AssemblyDefinition.ReadAssembly(output);
var verifyHelper = verify.MainModule.Types.FirstOrDefault(t => t.Namespace == "Coinage" && t.Name == "XmlDrivenCoinRecipeValues")
    ?? throw new InvalidOperationException("Verification failed: helper type absent.");
if (!verifyHelper.Methods.Any(m => m.Name == "GetOutputCount"))
    throw new InvalidOperationException("Verification failed: helper method absent.");

var remaining = 0;
foreach (var type in verify.MainModule.Types.SelectMany(AllTypes))
foreach (var method in type.Methods)
{
    if (!method.HasBody) continue;
    var ins = method.Body.Instructions;
    for (int i = 0; i < ins.Count; i++)
    {
        int? value = ins[i].OpCode.Code switch
        {
            Code.Ldc_I4_S => (sbyte)ins[i].Operand,
            Code.Ldc_I4 => (int)ins[i].Operand,
            _ => null
        };
        if (value != 50) continue;
        for (int j = i + 1; j < Math.Min(ins.Count, i + 5); j++)
        {
            if (ins[j].Operand is MethodReference mr && ins[j].OpCode.Code == Code.Newobj &&
                mr.Name == ".ctor" && mr.DeclaringType.Name.Contains("ThingDefCountClass", StringComparison.Ordinal))
                remaining++;
        }
    }
}
if (remaining != 0)
    throw new InvalidOperationException("Verification failed: hard-coded coin-yield site still present.");

Console.WriteLine($"Patched: {target.Method.FullName}");
Console.WriteLine("Coin output now comes from Mill_Coinage.targetCountAdjustment.");
Console.WriteLine("Ingredient cost remains XML-driven by Mill_Coinage.ingredients/count and IngredientValueGetter_Amount.");
Console.WriteLine("Output: " + output);
return 0;

static IEnumerable<TypeDefinition> AllTypes(TypeDefinition root)
{
    yield return root;
    foreach (var nested in root.NestedTypes)
        foreach (var x in AllTypes(nested))
            yield return x;
}
