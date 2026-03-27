using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Xunit;

namespace ILLiveEditor.Tests;

public class EndToEndTests
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
    /// Compile a C# source file into a DLL using Roslyn.
    /// </summary>
    static string CreateTestAssembly(string source, string name)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>();
        foreach (var asm in new[] { "System.Runtime.dll", "System.Private.CoreLib.dll", "System.Console.dll", "netstandard.dll" })
        {
            var p = Path.Combine(runtimeDir, asm);
            if (File.Exists(p)) refs.Add(MetadataReference.CreateFromFile(p));
        }

        var compilation = CSharpCompilation.Create(name,
            new[] { syntaxTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var outputPath = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid():N}.dll");
        var result = compilation.Emit(outputPath);
        Assert.True(result.Success, $"Test assembly compilation failed: {string.Join("; ", result.Diagnostics)}");
        return outputPath;
    }

    [Fact]
    public void FullPipeline_InjectWriteLine_ProducesValidAssembly()
    {
        var source = @"
using System;
public class Target {
    public static void Greet(string name) {
        Console.WriteLine(""Original: "" + name);
    }
}";
        var asmPath = CreateTestAssembly(source, "InjectTest");
        var outputPath = Path.ChangeExtension(asmPath, ".patched.dll");

        try
        {
            var engine = new ILLiveEditorEngine();

            // List instructions first
            var instructions = ILLiveEditorEngine.ListInstructions(asmPath, "Target", "Greet");
            Assert.NotEmpty(instructions);

            // Inject a Console.WriteLine before the first instruction
            var result = engine.PatchAndSave(
                asmPath, outputPath, "Target", "Greet",
                instructionIndex: 0,
                csharpSnippet: "Console.WriteLine(\"INJECTED\");",
                mode: SnippetInsertionMode.Before);

            Assert.True(result.Success, $"Patch failed: {string.Join("; ", result.Diagnostics)}");
            Assert.True(File.Exists(outputPath));

            // Verify the patched assembly is valid by reading it with Cecil
            using var patchedAsm = AssemblyDefinition.ReadAssembly(outputPath);
            var patchedMethod = patchedAsm.MainModule.GetType("Target").Methods
                .First(m => m.Name == "Greet");
            Assert.True(patchedMethod.Body.Instructions.Count > instructions.Count);
        }
        finally
        {
            TryDelete(asmPath);
            TryDelete(outputPath);
        }
    }

    [Fact]
    public void FullPipeline_PatchToBytes_ReturnsValidBytes()
    {
        var source = @"
using System;
public class Target2 {
    public static int GetValue() {
        return 42;
    }
}";
        var asmPath = CreateTestAssembly(source, "BytesTest");

        try
        {
            var engine = new ILLiveEditorEngine();
            var (result, bytes) = engine.PatchToBytes(
                asmPath, "Target2", "GetValue",
                instructionIndex: 0,
                csharpSnippet: "Console.WriteLine(\"before return\");",
                mode: SnippetInsertionMode.Before);

            Assert.True(result.Success, $"Patch failed: {string.Join("; ", result.Diagnostics)}");
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 0);

            // Verify bytes are valid PE
            using var ms = new MemoryStream(bytes);
            using var asm = AssemblyDefinition.ReadAssembly(ms);
            Assert.NotNull(asm.MainModule.GetType("Target2"));
        }
        finally
        {
            TryDelete(asmPath);
        }
    }

    [Fact]
    public void FullPipeline_ReplaceInstruction_Works()
    {
        var source = @"
using System;
public class Target3 {
    public static void DoWork() {
        Console.WriteLine(""original1"");
        Console.WriteLine(""original2"");
    }
}";
        var asmPath = CreateTestAssembly(source, "ReplaceTest");
        var outputPath = Path.ChangeExtension(asmPath, ".patched.dll");

        try
        {
            // Read the assembly to find instruction indices
            using var readAsm = AssemblyDefinition.ReadAssembly(asmPath);
            var readMethod = readAsm.MainModule.GetType("Target3").Methods.First(m => m.Name == "DoWork");
            int instrCount = readMethod.Body.Instructions.Count;
            readAsm.Dispose();

            var engine = new ILLiveEditorEngine();

            // Replace the first two instructions (ldstr + call for "original1") with a different message
            var result = engine.PatchAndSave(
                asmPath, outputPath, "Target3", "DoWork",
                instructionIndex: 0,
                csharpSnippet: "Console.WriteLine(\"replaced!\");",
                mode: SnippetInsertionMode.ReplaceRange,
                replaceEndIndex: 2); // Replace first 2 instructions

            Assert.True(result.Success, $"Patch failed: {string.Join("; ", result.Diagnostics)}");

            using var patchedAsm = AssemblyDefinition.ReadAssembly(outputPath);
            var patchedMethod = patchedAsm.MainModule.GetType("Target3").Methods.First(m => m.Name == "DoWork");
            Assert.NotNull(patchedMethod.Body);
        }
        finally
        {
            TryDelete(asmPath);
            TryDelete(outputPath);
        }
    }

    [Fact]
    public void FullPipeline_PreviewSnippet_ReturnsIL()
    {
        var source = @"
using System;
public class Target4 {
    public static void Simple() {
        Console.WriteLine(""hi"");
    }
}";
        var asmPath = CreateTestAssembly(source, "PreviewTest");

        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(asmPath);
            var method = asm.MainModule.GetType("Target4").Methods.First(m => m.Name == "Simple");

            var engine = new ILLiveEditorEngine();
            var (preview, diagnostics, generatedSource) = engine.PreviewSnippet(
                method, "Console.WriteLine(\"preview\");");

            Assert.DoesNotContain(diagnostics, d => d.StartsWith("Error"));
            Assert.NotEmpty(preview);
            Assert.Contains(preview, l => l.Contains("ldstr"));
            Assert.NotEmpty(generatedSource);
        }
        finally
        {
            TryDelete(asmPath);
        }
    }

    [Fact]
    public void FullPipeline_MethodNotFound_ReturnsFailure()
    {
        var source = @"
public class Target5 {
    public static void Exists() { }
}";
        var asmPath = CreateTestAssembly(source, "NotFoundTest");

        try
        {
            var engine = new ILLiveEditorEngine();
            var result = engine.PatchAndSave(
                asmPath, asmPath + ".out", "Target5", "DoesNotExist",
                instructionIndex: 0,
                csharpSnippet: "var x = 1;");

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Contains("not found"));
        }
        finally
        {
            TryDelete(asmPath);
            TryDelete(asmPath + ".out");
        }
    }

    [Fact]
    public void FullPipeline_CompilationError_ReturnsDiagnostics()
    {
        var source = @"
using System;
public class Target6 {
    public static void Work() {
        Console.WriteLine(""ok"");
    }
}";
        var asmPath = CreateTestAssembly(source, "CompErrorTest");

        try
        {
            var engine = new ILLiveEditorEngine();
            var result = engine.PatchAndSave(
                asmPath, asmPath + ".out", "Target6", "Work",
                instructionIndex: 0,
                csharpSnippet: "UndefinedType.CallNothing();");

            Assert.False(result.Success);
            Assert.NotEmpty(result.Diagnostics);
        }
        finally
        {
            TryDelete(asmPath);
            TryDelete(asmPath + ".out");
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
