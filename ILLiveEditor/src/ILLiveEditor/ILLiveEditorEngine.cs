using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ILLiveEditor;

/// <summary>
/// High-level API that orchestrates the full IL live editing pipeline:
/// C# snippet → Roslyn compile → Cecil IL extraction → operand remapping → method patching.
/// </summary>
public class ILLiveEditorEngine
{
    readonly SnippetCompiler _compiler = new();
    readonly CecilILExtractor _extractor = new();
    readonly ILPatcher _patcher = new();

    /// <summary>
    /// Patch a method in an assembly file and save the result.
    /// </summary>
    /// <param name="assemblyPath">Path to the target .NET assembly.</param>
    /// <param name="outputPath">Path to write the patched assembly. Can be same as assemblyPath.</param>
    /// <param name="typeFullName">Full name of the type containing the method (e.g. "MyNamespace.MyClass").</param>
    /// <param name="methodName">Name of the method to patch.</param>
    /// <param name="instructionIndex">Index of the target instruction in the method body.</param>
    /// <param name="csharpSnippet">C# code to inject.</param>
    /// <param name="mode">Insertion mode (Before, After, ReplaceRange).</param>
    /// <param name="replaceEndIndex">End index for ReplaceRange mode (exclusive). -1 = insertionIndex + 1.</param>
    /// <param name="assemblySearchPaths">Additional directories to search for referenced assemblies.</param>
    public PatchResult PatchAndSave(
        string assemblyPath,
        string outputPath,
        string typeFullName,
        string methodName,
        int instructionIndex,
        string csharpSnippet,
        SnippetInsertionMode mode = SnippetInsertionMode.Before,
        int replaceEndIndex = -1,
        params string[] assemblySearchPaths)
    {
        var readerParams = new ReaderParameters
        {
            ReadWrite = assemblyPath == outputPath,
            ReadSymbols = false // Don't fail if PDB is missing
        };

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);

        var method = FindMethod(assembly, typeFullName, methodName);
        if (method == null)
            return PatchResult.Failure(new[] { $"Method '{typeFullName}.{methodName}' not found in assembly." });

        var result = PatchMethod(method, instructionIndex, csharpSnippet, mode, replaceEndIndex, assemblySearchPaths);
        if (!result.Success)
            return result;

        assembly.Write(outputPath);
        return result;
    }

    /// <summary>
    /// Patch a method in an assembly and return the modified assembly as bytes.
    /// </summary>
    public (PatchResult Result, byte[]? PatchedAssembly) PatchToBytes(
        string assemblyPath,
        string typeFullName,
        string methodName,
        int instructionIndex,
        string csharpSnippet,
        SnippetInsertionMode mode = SnippetInsertionMode.Before,
        int replaceEndIndex = -1,
        params string[] assemblySearchPaths)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);

        var method = FindMethod(assembly, typeFullName, methodName);
        if (method == null)
            return (PatchResult.Failure(new[] { $"Method '{typeFullName}.{methodName}' not found." }), null);

        var result = PatchMethod(method, instructionIndex, csharpSnippet, mode, replaceEndIndex, assemblySearchPaths);
        if (!result.Success)
            return (result, null);

        using var ms = new MemoryStream();
        assembly.Write(ms);
        return (result, ms.ToArray());
    }

    /// <summary>
    /// Patch an already-loaded Cecil method in-place.
    /// The caller is responsible for saving the assembly afterwards.
    /// </summary>
    public PatchResult PatchMethod(
        MethodDefinition method,
        int instructionIndex,
        string csharpSnippet,
        SnippetInsertionMode mode = SnippetInsertionMode.Before,
        int replaceEndIndex = -1,
        params string[] assemblySearchPaths)
    {
        if (method.Body == null)
            return PatchResult.Failure(new[] { "Method has no body (abstract or extern)." });

        if (instructionIndex < 0 || instructionIndex >= method.Body.Instructions.Count)
            return PatchResult.Failure(new[] { $"Instruction index {instructionIndex} is out of range (method has {method.Body.Instructions.Count} instructions)." });

        // Step 1: Build context
        var context = SnippetContext.FromMethod(method, assemblySearchPaths);

        // Step 2: Compile snippet
        var compilation = _compiler.Compile(csharpSnippet, context);
        if (!compilation.Success)
            return PatchResult.Failure(compilation.Diagnostics.ToList());

        // Step 3: Extract IL
        var extraction = _extractor.Extract(compilation.CompiledBytes!);
        if (extraction == null)
            return PatchResult.Failure(new[] { "Failed to extract IL from compiled snippet. Could not find __Snippet method." });

        // Step 4: Patch
        return _patcher.Patch(method, instructionIndex, mode, extraction, context, replaceEndIndex);
    }

    /// <summary>
    /// List the IL instructions of a method (useful for choosing insertion points).
    /// </summary>
    public static IReadOnlyList<(int Index, string Offset, string OpCode, string Operand)> ListInstructions(
        string assemblyPath,
        string typeFullName,
        string methodName)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var method = FindMethod(assembly, typeFullName, methodName);
        if (method?.Body == null)
            return Array.Empty<(int, string, string, string)>();

        return method.Body.Instructions
            .Select((instr, i) => (
                i,
                $"IL_{instr.Offset:X4}",
                instr.OpCode.Name,
                FormatOperand(instr)))
            .ToList();
    }

    /// <summary>
    /// Compile a snippet and return the IL preview without patching.
    /// Useful for showing what IL would be injected.
    /// </summary>
    public (IReadOnlyList<string> ILPreview, IReadOnlyList<string> Diagnostics, string GeneratedSource) PreviewSnippet(
        MethodDefinition method,
        string csharpSnippet,
        params string[] assemblySearchPaths)
    {
        var context = SnippetContext.FromMethod(method, assemblySearchPaths);
        var compilation = _compiler.Compile(csharpSnippet, context);

        if (!compilation.Success)
            return (Array.Empty<string>(), compilation.Diagnostics, compilation.GeneratedSource);

        var extraction = _extractor.Extract(compilation.CompiledBytes!);
        if (extraction == null)
            return (Array.Empty<string>(), new[] { "Failed to extract IL." }, compilation.GeneratedSource);

        var preview = extraction.Instructions
            .Select(i => $"{i.OpCode.Name} {FormatOperand(i)}")
            .ToList();

        return (preview, compilation.Diagnostics, compilation.GeneratedSource);
    }

    static MethodDefinition? FindMethod(AssemblyDefinition assembly, string typeFullName, string methodName)
    {
        foreach (var module in assembly.Modules)
        {
            var type = module.GetType(typeFullName);
            if (type == null)
            {
                // Try with nested type syntax (Foo/Bar → Foo.Bar)
                type = module.GetType(typeFullName.Replace('.', '/'));
            }
            if (type == null) continue;

            foreach (var method in type.Methods)
            {
                if (method.Name == methodName)
                    return method;
            }
        }
        return null;
    }

    static string FormatOperand(Instruction instr)
    {
        if (instr.Operand == null)
            return "";
        if (instr.Operand is Instruction target)
            return $"IL_{target.Offset:X4}";
        if (instr.Operand is Instruction[] targets)
            return string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}"));
        if (instr.Operand is string s)
            return $"\"{s}\"";
        if (instr.Operand is MethodReference mr)
            return $"{mr.DeclaringType}::{mr.Name}";
        if (instr.Operand is FieldReference fr)
            return $"{fr.DeclaringType}::{fr.Name}";
        if (instr.Operand is TypeReference tr)
            return tr.FullName;
        if (instr.Operand is VariableDefinition vd)
            return $"V_{vd.Index}";
        if (instr.Operand is ParameterDefinition pd)
            return pd.Name;
        return instr.Operand.ToString() ?? "";
    }
}
