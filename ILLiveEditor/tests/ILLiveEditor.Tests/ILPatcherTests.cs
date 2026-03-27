using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace ILLiveEditor.Tests;

public class ILPatcherTests
{
    static List<string> GetCoreReferencePaths()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var paths = new List<string>();
        foreach (var name in new[] { "System.Runtime.dll", "System.Private.CoreLib.dll", "System.Console.dll", "netstandard.dll" })
        {
            var p = Path.Combine(runtimeDir, name);
            if (File.Exists(p)) paths.Add(p);
        }
        return paths;
    }

    /// <summary>
    /// Creates a simple test method with IL: nop, nop, ret
    /// </summary>
    static (AssemblyDefinition Assembly, MethodDefinition Method) CreateTestMethod()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("TestAsm", new Version(1, 0)),
            "TestModule", ModuleKind.Dll);

        var module = assembly.MainModule;
        var type = new TypeDefinition("TestNs", "TestClass",
            TypeAttributes.Class | TypeAttributes.Public,
            module.ImportReference(typeof(object)));
        module.Types.Add(type);

        var method = new MethodDefinition("TestMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            module.ImportReference(typeof(void)));
        type.Methods.Add(method);

        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Nop));
        il.Append(il.Create(OpCodes.Nop));
        il.Append(il.Create(OpCodes.Ret));

        return (assembly, method);
    }

    [Fact]
    public void Patch_InsertBefore_IncreasesInstructionCount()
    {
        var (assembly, method) = CreateTestMethod();
        int originalCount = method.Body.Instructions.Count; // 3: nop, nop, ret

        var snippetInstr = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldstr, "injected"),
            Instruction.Create(OpCodes.Pop)
        };

        var extraction = new ExtractionResult(
            snippetInstr,
            new List<VariableDefinition>(),
            new List<ExceptionHandlerInfo>(),
            method);

        var context = new SnippetContext
        {
            IsInstanceMethod = false,
            Parameters = new(),
            Locals = new(),
            UsingNamespaces = new(),
            AssemblyReferencePaths = new()
        };

        var patcher = new ILPatcher();
        var result = patcher.Patch(method, 0, SnippetInsertionMode.Before, extraction, context);

        Assert.True(result.Success);
        Assert.True(method.Body.Instructions.Count > originalCount);
        assembly.Dispose();
    }

    [Fact]
    public void Patch_InsertAfter_InsertsAtCorrectPosition()
    {
        var (assembly, method) = CreateTestMethod();

        var snippetInstr = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldstr, "after-first-nop"),
            Instruction.Create(OpCodes.Pop)
        };

        var extraction = new ExtractionResult(
            snippetInstr,
            new List<VariableDefinition>(),
            new List<ExceptionHandlerInfo>(),
            method);

        var context = new SnippetContext
        {
            IsInstanceMethod = false,
            Parameters = new(),
            Locals = new(),
            UsingNamespaces = new(),
            AssemblyReferencePaths = new()
        };

        var patcher = new ILPatcher();
        var result = patcher.Patch(method, 0, SnippetInsertionMode.After, extraction, context);

        Assert.True(result.Success);
        // After first nop (index 0), we should find our ldstr at index 1
        Assert.Equal(OpCodes.Nop, method.Body.Instructions[0].OpCode);
        assembly.Dispose();
    }

    [Fact]
    public void Patch_ReplaceRange_RemovesAndInserts()
    {
        var (assembly, method) = CreateTestMethod();
        // Original: nop(0), nop(1), ret(2)
        // Replace [0,2) = replace both nops with ldstr+pop

        var snippetInstr = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldstr, "replaced"),
            Instruction.Create(OpCodes.Pop)
        };

        var extraction = new ExtractionResult(
            snippetInstr,
            new List<VariableDefinition>(),
            new List<ExceptionHandlerInfo>(),
            method);

        var context = new SnippetContext
        {
            IsInstanceMethod = false,
            Parameters = new(),
            Locals = new(),
            UsingNamespaces = new(),
            AssemblyReferencePaths = new()
        };

        var patcher = new ILPatcher();
        var result = patcher.Patch(method, 0, SnippetInsertionMode.ReplaceRange, extraction, context, replaceEndIndex: 2);

        Assert.True(result.Success);
        // Should now be: ldstr, pop, ret (3 instructions)
        Assert.Equal(3, method.Body.Instructions.Count);
        Assert.Equal(OpCodes.Ret, method.Body.Instructions[^1].OpCode);
        assembly.Dispose();
    }

    [Fact]
    public void Patch_WithBranchTarget_RedirectsOnReplace()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("TestAsm", new Version(1, 0)),
            "TestModule", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("TestNs", "TestClass",
            TypeAttributes.Class | TypeAttributes.Public,
            module.ImportReference(typeof(object)));
        module.Types.Add(type);

        var method = new MethodDefinition("TestMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            module.ImportReference(typeof(void)));
        type.Methods.Add(method);

        var il = method.Body.GetILProcessor();
        var target = il.Create(OpCodes.Nop); // This will be replaced
        var brInstr = il.Create(OpCodes.Br, target);
        il.Append(brInstr);
        il.Append(target);
        il.Append(il.Create(OpCodes.Ret));

        // Replace the nop (index 1) with ldstr+pop
        var snippetInstr = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldstr, "new-target"),
            Instruction.Create(OpCodes.Pop)
        };

        var extraction = new ExtractionResult(
            snippetInstr,
            new List<VariableDefinition>(),
            new List<ExceptionHandlerInfo>(),
            method);

        var context = new SnippetContext
        {
            IsInstanceMethod = false,
            Parameters = new(),
            Locals = new(),
            UsingNamespaces = new(),
            AssemblyReferencePaths = new()
        };

        var patcher = new ILPatcher();
        var result = patcher.Patch(method, 1, SnippetInsertionMode.ReplaceRange, extraction, context, replaceEndIndex: 2);

        Assert.True(result.Success);
        // The branch should now point to the first replacement instruction
        var branchOperand = method.Body.Instructions[0].Operand as Instruction;
        Assert.NotNull(branchOperand);
        Assert.Equal(OpCodes.Ldstr, branchOperand!.OpCode);

        assembly.Dispose();
    }

    [Fact]
    public void Patch_MethodWithNoBody_Fails()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("TestAsm", new Version(1, 0)),
            "TestModule", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("TestNs", "TestClass",
            TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Abstract,
            module.ImportReference(typeof(object)));
        module.Types.Add(type);

        var method = new MethodDefinition("AbstractMethod",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual,
            module.ImportReference(typeof(void)));
        type.Methods.Add(method);

        var extraction = new ExtractionResult(
            new List<Instruction> { Instruction.Create(OpCodes.Nop) },
            new List<VariableDefinition>(),
            new List<ExceptionHandlerInfo>(),
            method);

        var context = new SnippetContext
        {
            IsInstanceMethod = false,
            Parameters = new(),
            Locals = new(),
            UsingNamespaces = new(),
            AssemblyReferencePaths = new()
        };

        var patcher = new ILPatcher();
        var result = patcher.Patch(method, 0, SnippetInsertionMode.Before, extraction, context);

        Assert.False(result.Success);
        assembly.Dispose();
    }
}
