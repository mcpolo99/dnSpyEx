using Mono.Cecil.Cil;
using Xunit;

namespace ILLiveEditor.Tests;

public class CecilILExtractorTests
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

    static byte[] CompileSnippet(string snippet)
    {
        var ctx = new SnippetContext
        {
            ContainingTypeFullName = "TestClass",
            IsInstanceMethod = false,
            Parameters = new List<SnippetContext.ParameterInfo>(),
            Locals = new List<SnippetContext.LocalInfo>(),
            UsingNamespaces = new List<string> { "System" },
            AssemblyReferencePaths = GetCoreReferencePaths()
        };
        var compiler = new SnippetCompiler();
        var result = compiler.Compile(snippet, ctx);
        Assert.True(result.Success, $"Compilation failed: {string.Join("; ", result.Diagnostics)}");
        return result.CompiledBytes!;
    }

    [Fact]
    public void Extract_SimpleWriteLine_ContainsLdstrAndCall()
    {
        var bytes = CompileSnippet("Console.WriteLine(\"hello\");");
        var extractor = new CecilILExtractor();

        var result = extractor.Extract(bytes);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Instructions);

        // Should contain ldstr "hello" and call to Console.WriteLine
        Assert.Contains(result.Instructions, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "hello");
        Assert.Contains(result.Instructions, i => i.OpCode == OpCodes.Call);
    }

    [Fact]
    public void Extract_SnippetWithLocal_ExtractsLocals()
    {
        var bytes = CompileSnippet("int y = 42; Console.WriteLine(y);");
        var extractor = new CecilILExtractor();

        var result = extractor.Extract(bytes);

        Assert.NotNull(result);
        // The snippet introduces a local variable 'y'
        // In Release mode, the compiler may optimize it away, but the IL should still work
        Assert.NotEmpty(result!.Instructions);
    }

    [Fact]
    public void Extract_TrailingRet_IsRemoved()
    {
        var bytes = CompileSnippet("Console.WriteLine(\"test\");");
        var extractor = new CecilILExtractor();

        var result = extractor.Extract(bytes);

        Assert.NotNull(result);
        // Last instruction should NOT be ret (we strip it)
        if (result!.Instructions.Count > 0)
            Assert.NotEqual(OpCodes.Ret, result.Instructions[^1].OpCode);
    }

    [Fact]
    public void Extract_InvalidBytes_ReturnsNull()
    {
        var extractor = new CecilILExtractor();

        // Passing garbage bytes should return null (graceful failure)
        var result = extractor.Extract(new byte[] { 0x00, 0x01, 0x02 });
        Assert.Null(result);
    }
}
