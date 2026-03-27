using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ILLiveEditor;

/// <summary>
/// Extracts IL instructions from a compiled snippet assembly using Mono.Cecil.
/// </summary>
public class CecilILExtractor
{
    /// <summary>
    /// Load the compiled PE bytes and extract the snippet method's IL instructions and locals.
    /// </summary>
    /// <param name="compiledBytes">PE bytes from SnippetCompiler.</param>
    /// <returns>Extracted instructions and local variables, or null if extraction fails.</returns>
    public ExtractionResult? Extract(byte[] compiledBytes)
    {
        AssemblyDefinition assembly;
        try
        {
            var stream = new MemoryStream(compiledBytes);
            assembly = AssemblyDefinition.ReadAssembly(stream);
        }
        catch
        {
            return null;
        }

        using var _ = assembly;

        var snippetMethod = FindSnippetMethod(assembly);
        if (snippetMethod?.Body == null)
            return null;

        var body = snippetMethod.Body;

        // Clone instructions (we need independent copies for the target method)
        var instructions = new List<Instruction>();
        var instructionMap = new Dictionary<Instruction, Instruction>();

        foreach (var orig in body.Instructions)
        {
            var clone = CloneInstruction(orig);
            instructions.Add(clone);
            instructionMap[orig] = clone;
        }

        // Fix branch targets in cloned instructions
        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.Operand is Instruction target && instructionMap.TryGetValue(target, out var mappedTarget))
            {
                instr.Operand = mappedTarget;
            }
            else if (instr.Operand is Instruction[] targets)
            {
                var mappedTargets = new Instruction[targets.Length];
                for (int j = 0; j < targets.Length; j++)
                {
                    if (instructionMap.TryGetValue(targets[j], out var mt))
                        mappedTargets[j] = mt;
                    else
                        mappedTargets[j] = targets[j];
                }
                instr.Operand = mappedTargets;
            }
        }

        // Remove trailing ret if present (caller decides whether to keep it)
        if (instructions.Count > 0 && instructions[^1].OpCode == OpCodes.Ret)
            instructions.RemoveAt(instructions.Count - 1);

        // Collect local variables
        var locals = new List<VariableDefinition>();
        foreach (var v in body.Variables)
            locals.Add(new VariableDefinition(v.VariableType));

        // Collect exception handlers
        var handlers = new List<ExceptionHandlerInfo>();
        foreach (var eh in body.ExceptionHandlers)
        {
            handlers.Add(new ExceptionHandlerInfo
            {
                HandlerType = eh.HandlerType,
                CatchType = eh.CatchType,
                TryStart = eh.TryStart != null && instructionMap.TryGetValue(eh.TryStart, out var ts) ? ts : null,
                TryEnd = eh.TryEnd != null && instructionMap.TryGetValue(eh.TryEnd, out var te) ? te : null,
                HandlerStart = eh.HandlerStart != null && instructionMap.TryGetValue(eh.HandlerStart, out var hs) ? hs : null,
                HandlerEnd = eh.HandlerEnd != null && instructionMap.TryGetValue(eh.HandlerEnd, out var he) ? he : null,
                FilterStart = eh.FilterStart != null && instructionMap.TryGetValue(eh.FilterStart, out var fs) ? fs : null,
            });
        }

        return new ExtractionResult(instructions, locals, handlers, snippetMethod);
    }

    static MethodDefinition? FindSnippetMethod(AssemblyDefinition assembly)
    {
        foreach (var module in assembly.Modules)
        {
            var wrapperType = module.GetType("__ILLiveEditor.__Wrapper");
            if (wrapperType == null)
                continue;

            foreach (var method in wrapperType.Methods)
            {
                if (method.Name == "__Snippet")
                    return method;
            }
        }
        return null;
    }

    static Instruction CloneInstruction(Instruction orig)
    {
        var clone = Instruction.Create(OpCodes.Nop);
        clone.OpCode = orig.OpCode;
        clone.Operand = orig.Operand;
        return clone;
    }
}

/// <summary>
/// Result of IL extraction from a compiled snippet.
/// </summary>
public class ExtractionResult
{
    public List<Instruction> Instructions { get; }
    public List<VariableDefinition> Locals { get; }
    public List<ExceptionHandlerInfo> ExceptionHandlers { get; }
    public MethodDefinition SnippetMethod { get; }

    public ExtractionResult(
        List<Instruction> instructions,
        List<VariableDefinition> locals,
        List<ExceptionHandlerInfo> exceptionHandlers,
        MethodDefinition snippetMethod)
    {
        Instructions = instructions;
        Locals = locals;
        ExceptionHandlers = exceptionHandlers;
        SnippetMethod = snippetMethod;
    }
}

/// <summary>
/// Portable representation of an exception handler from the snippet.
/// </summary>
public class ExceptionHandlerInfo
{
    public ExceptionHandlerType HandlerType { get; set; }
    public TypeReference? CatchType { get; set; }
    public Instruction? TryStart { get; set; }
    public Instruction? TryEnd { get; set; }
    public Instruction? HandlerStart { get; set; }
    public Instruction? HandlerEnd { get; set; }
    public Instruction? FilterStart { get; set; }
}
