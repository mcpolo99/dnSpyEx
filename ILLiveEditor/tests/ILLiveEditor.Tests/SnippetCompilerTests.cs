using Xunit;

namespace ILLiveEditor.Tests;

public class SnippetCompilerTests
{
    static SnippetContext CreateSimpleContext()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var ctx = new SnippetContext
        {
            ContainingTypeFullName = "TestClass",
            IsInstanceMethod = false,
            Parameters = new List<SnippetContext.ParameterInfo>
            {
                new("name", "string", false, false),
                new("count", "int", false, false),
            },
            Locals = new List<SnippetContext.LocalInfo>
            {
                new("x", "int"),
            },
            UsingNamespaces = new List<string> { "System" },
            AssemblyReferencePaths = GetCoreReferencePaths()
        };
        return ctx;
    }

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

    [Fact]
    public void Compile_SimpleSnippet_Succeeds()
    {
        var compiler = new SnippetCompiler();
        var ctx = CreateSimpleContext();

        var result = compiler.Compile("Console.WriteLine(\"hello\");", ctx);

        Assert.True(result.Success, $"Compilation failed: {string.Join("; ", result.Diagnostics)}");
        Assert.NotNull(result.CompiledBytes);
        Assert.True(result.CompiledBytes!.Length > 0);
    }

    [Fact]
    public void Compile_SnippetWithParamAccess_Succeeds()
    {
        var compiler = new SnippetCompiler();
        var ctx = CreateSimpleContext();

        var result = compiler.Compile("Console.WriteLine(name);", ctx);

        Assert.True(result.Success, $"Compilation failed: {string.Join("; ", result.Diagnostics)}");
    }

    [Fact]
    public void Compile_SnippetWithLocalAccess_Succeeds()
    {
        var compiler = new SnippetCompiler();
        var ctx = CreateSimpleContext();

        var result = compiler.Compile("x = x + 1;", ctx);

        Assert.True(result.Success, $"Compilation failed: {string.Join("; ", result.Diagnostics)}");
    }

    [Fact]
    public void Compile_SyntaxError_ReportsDiagnostics()
    {
        var compiler = new SnippetCompiler();
        var ctx = CreateSimpleContext();

        var result = compiler.Compile("this is not valid C# code !!!", ctx);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Compile_EmptySnippet_Succeeds()
    {
        var compiler = new SnippetCompiler();
        var ctx = CreateSimpleContext();

        var result = compiler.Compile("", ctx);

        Assert.True(result.Success, $"Compilation failed: {string.Join("; ", result.Diagnostics)}");
    }

    [Fact]
    public void BuildWrapperSource_InstanceMethod_IncludesThis()
    {
        var ctx = new SnippetContext
        {
            ContainingTypeFullName = "MyNamespace.Foo",
            IsInstanceMethod = true,
            Parameters = new List<SnippetContext.ParameterInfo>
            {
                new("name", "string", false, false)
            },
            Locals = new List<SnippetContext.LocalInfo>(),
            UsingNamespaces = new List<string> { "System" },
            AssemblyReferencePaths = new List<string>()
        };

        var source = ctx.BuildWrapperSource("Console.WriteLine(name);");

        Assert.Contains("MyNamespace.Foo __this", source);
        Assert.Contains("string name", source);
    }

    [Fact]
    public void BuildWrapperSource_StaticMethod_NoThis()
    {
        var ctx = new SnippetContext
        {
            ContainingTypeFullName = "Foo",
            IsInstanceMethod = false,
            Parameters = new List<SnippetContext.ParameterInfo>(),
            Locals = new List<SnippetContext.LocalInfo>(),
            UsingNamespaces = new List<string> { "System" },
            AssemblyReferencePaths = new List<string>()
        };

        var source = ctx.BuildWrapperSource("var x = 1;");

        Assert.DoesNotContain("__this", source);
    }
}
