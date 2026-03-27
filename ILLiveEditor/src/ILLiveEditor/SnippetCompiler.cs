using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ILLiveEditor;

/// <summary>
/// Compiles C# snippets into PE bytes using Roslyn.
/// The snippet is wrapped in a compilable method stub using the provided SnippetContext.
/// </summary>
public class SnippetCompiler
{
    /// <summary>
    /// Compile a C# snippet into a PE byte array.
    /// </summary>
    /// <param name="snippet">Raw C# statement(s) to compile.</param>
    /// <param name="context">Method context for wrapping the snippet.</param>
    /// <returns>Compiled PE bytes (or null on failure) and diagnostics.</returns>
    public CompilationOutput Compile(string snippet, SnippetContext context)
    {
        string wrapperSource = context.BuildWrapperSource(snippet);

        var syntaxTree = CSharpSyntaxTree.ParseText(wrapperSource);

        var references = new List<MetadataReference>();
        foreach (var path in context.AssemblyReferencePaths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch
            {
                // Skip unresolvable references
            }
        }

        var compilation = CSharpCompilation.Create(
            "__ILLiveEditorSnippet",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(true));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        var diagnostics = emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
            .Select(FormatDiagnostic)
            .ToList();

        if (!emitResult.Success)
            return new CompilationOutput(null, diagnostics, wrapperSource);

        return new CompilationOutput(ms.ToArray(), diagnostics, wrapperSource);
    }

    static string FormatDiagnostic(Diagnostic d)
    {
        var loc = d.Location.GetMappedLineSpan();
        return $"{d.Severity} {d.Id}: {d.GetMessage()} (line {loc.StartLinePosition.Line + 1})";
    }
}

/// <summary>
/// Output from the snippet compilation step.
/// </summary>
public class CompilationOutput
{
    public byte[]? CompiledBytes { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public string GeneratedSource { get; }

    public bool Success => CompiledBytes != null;

    public CompilationOutput(byte[]? compiledBytes, IReadOnlyList<string> diagnostics, string generatedSource)
    {
        CompiledBytes = compiledBytes;
        Diagnostics = diagnostics;
        GeneratedSource = generatedSource;
    }
}
