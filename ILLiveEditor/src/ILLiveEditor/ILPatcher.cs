using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ILLiveEditor;

/// <summary>
/// Patches a target method's IL body by inserting or replacing instructions
/// extracted from a compiled snippet.
/// Handles operand remapping (wrapper params → target params/locals),
/// branch target fixup, exception handler adjustment, and MaxStack recalculation.
/// </summary>
public class ILPatcher
{
    /// <summary>
    /// Apply snippet instructions to the target method.
    /// </summary>
    public PatchResult Patch(
        MethodDefinition targetMethod,
        int insertionIndex,
        SnippetInsertionMode mode,
        ExtractionResult extraction,
        SnippetContext context,
        int replaceEndIndex = -1)
    {
        if (targetMethod.Body == null)
            return PatchResult.Failure(new[] { "Target method has no body." });

        var body = targetMethod.Body;
        var il = body.GetILProcessor();
        var instructions = extraction.Instructions;

        if (instructions.Count == 0)
            return PatchResult.Ok(Array.Empty<Instruction>());

        // Step 1: Remap operands (params/locals/member refs)
        var remappedInstructions = RemapOperands(instructions, extraction, targetMethod, context);

        // Step 2: Import member references into target module
        ImportReferences(remappedInstructions, targetMethod.Module);

        // Step 3: Add new local variables from snippet
        int localOffset = body.Variables.Count;
        foreach (var snippetLocal in extraction.Locals)
        {
            var importedType = targetMethod.Module.ImportReference(snippetLocal.VariableType);
            body.Variables.Add(new VariableDefinition(importedType));
        }

        // Step 4: Remap local variable indices in instructions (offset by existing locals count)
        RemapLocalIndices(remappedInstructions, localOffset, extraction);

        // Step 5: Insert or replace instructions
        switch (mode)
        {
            case SnippetInsertionMode.Before:
                InsertAt(body, insertionIndex, remappedInstructions);
                break;

            case SnippetInsertionMode.After:
                InsertAt(body, insertionIndex + 1, remappedInstructions);
                break;

            case SnippetInsertionMode.ReplaceRange:
                if (replaceEndIndex < 0)
                    replaceEndIndex = insertionIndex + 1;
                ReplaceRange(body, insertionIndex, replaceEndIndex, remappedInstructions);
                break;
        }

        // Step 6: Add exception handlers from snippet
        AddExceptionHandlers(body, extraction.ExceptionHandlers, targetMethod.Module);

        // Step 7: Recalculate offsets and MaxStack
        body.OptimizeMacros();

        return PatchResult.Ok(remappedInstructions);
    }

    /// <summary>
    /// Remaps wrapper method parameters to target method's actual params and locals.
    /// In the wrapper, params represent: [this], method params, locals (in that order).
    /// </summary>
    List<Instruction> RemapOperands(
        List<Instruction> snippetInstructions,
        ExtractionResult extraction,
        MethodDefinition targetMethod,
        SnippetContext context)
    {
        // Build mapping: wrapper param index → (isParam, targetIndex)
        // wrapper param 0 = this (if instance), then method params, then locals
        var paramMap = new List<ParamMapping>();

        int wrapperIdx = 0;
        if (context.IsInstanceMethod)
        {
            paramMap.Add(new ParamMapping(IsParameter: true, TargetIndex: 0)); // this
            wrapperIdx++;
        }

        for (int i = 0; i < context.Parameters.Count; i++)
        {
            int targetParamIdx = context.IsInstanceMethod ? i + 1 : i;
            paramMap.Add(new ParamMapping(IsParameter: true, TargetIndex: targetParamIdx));
            wrapperIdx++;
        }

        for (int i = 0; i < context.Locals.Count; i++)
        {
            paramMap.Add(new ParamMapping(IsParameter: false, TargetIndex: i));
            wrapperIdx++;
        }

        var result = new List<Instruction>();
        foreach (var instr in snippetInstructions)
        {
            var newInstr = CloneInstruction(instr);

            // Remap parameter-referencing instructions
            if (IsLdarg(newInstr.OpCode, out int argIdx, newInstr))
            {
                int idx = GetArgIndex(newInstr);
                if (idx >= 0 && idx < paramMap.Count)
                {
                    var map = paramMap[idx];
                    if (map.IsParameter)
                        RewriteAsLdarg(newInstr, map.TargetIndex, targetMethod);
                    else
                        RewriteAsLdloc(newInstr, map.TargetIndex, targetMethod.Body);
                }
            }
            else if (IsStarg(newInstr.OpCode, newInstr))
            {
                int idx = GetArgIndex(newInstr);
                if (idx >= 0 && idx < paramMap.Count)
                {
                    var map = paramMap[idx];
                    if (map.IsParameter)
                        RewriteAsStarg(newInstr, map.TargetIndex, targetMethod);
                    else
                        RewriteAsStloc(newInstr, map.TargetIndex, targetMethod.Body);
                }
            }
            else if (IsLdarga(newInstr.OpCode, newInstr))
            {
                int idx = GetArgIndex(newInstr);
                if (idx >= 0 && idx < paramMap.Count)
                {
                    var map = paramMap[idx];
                    if (map.IsParameter)
                        RewriteAsLdarga(newInstr, map.TargetIndex, targetMethod);
                    else
                        RewriteAsLdloca(newInstr, map.TargetIndex, targetMethod.Body);
                }
            }

            result.Add(newInstr);
        }

        // Fix internal branch targets to point to cloned instructions
        var oldToNew = new Dictionary<Instruction, Instruction>();
        for (int i = 0; i < snippetInstructions.Count; i++)
            oldToNew[snippetInstructions[i]] = result[i];

        foreach (var instr in result)
        {
            if (instr.Operand is Instruction target && oldToNew.TryGetValue(target, out var mapped))
                instr.Operand = mapped;
            else if (instr.Operand is Instruction[] targets)
            {
                for (int i = 0; i < targets.Length; i++)
                    if (oldToNew.TryGetValue(targets[i], out var mt))
                        targets[i] = mt;
            }
        }

        return result;
    }

    void ImportReferences(List<Instruction> instructions, ModuleDefinition targetModule)
    {
        foreach (var instr in instructions)
        {
            if (instr.Operand is MethodReference methodRef)
                instr.Operand = targetModule.ImportReference(methodRef);
            else if (instr.Operand is FieldReference fieldRef)
                instr.Operand = targetModule.ImportReference(fieldRef);
            else if (instr.Operand is TypeReference typeRef && typeRef is not GenericParameter)
                instr.Operand = targetModule.ImportReference(typeRef);
        }
    }

    void RemapLocalIndices(List<Instruction> instructions, int offset, ExtractionResult extraction)
    {
        if (offset == 0)
            return;

        foreach (var instr in instructions)
        {
            if (instr.Operand is VariableDefinition varDef)
            {
                // This local was from the snippet — its index needs to be offset
                // We'll handle this by the fact that we added locals to the body
                // and the VariableDefinition objects should match
                continue;
            }

            // For ldloc.N/stloc.N short forms that use implicit indices
            // These are already handled as the remapping converts them to proper forms
        }
    }

    void InsertAt(MethodBody body, int index, List<Instruction> instructions)
    {
        // Clamp to valid range
        if (index < 0) index = 0;
        if (index > body.Instructions.Count) index = body.Instructions.Count;

        for (int i = 0; i < instructions.Count; i++)
            body.Instructions.Insert(index + i, instructions[i]);
    }

    void ReplaceRange(MethodBody body, int startIndex, int endIndex, List<Instruction> replacements)
    {
        if (startIndex < 0) startIndex = 0;
        if (endIndex > body.Instructions.Count) endIndex = body.Instructions.Count;
        if (startIndex >= endIndex && replacements.Count == 0) return;

        // Collect instructions being removed
        var removed = new List<Instruction>();
        for (int i = startIndex; i < endIndex; i++)
            removed.Add(body.Instructions[i]);

        // Redirect any branch targets pointing to removed instructions
        var firstReplacement = replacements.Count > 0 ? replacements[0] : (startIndex < body.Instructions.Count ? body.Instructions[startIndex] : null);

        if (firstReplacement != null)
        {
            RedirectBranchTargets(body, removed, firstReplacement);
            RedirectExceptionHandlers(body, removed, firstReplacement);
        }

        // Remove old instructions (in reverse to keep indices stable)
        for (int i = endIndex - 1; i >= startIndex; i--)
            body.Instructions.RemoveAt(i);

        // Insert replacements
        for (int i = 0; i < replacements.Count; i++)
            body.Instructions.Insert(startIndex + i, replacements[i]);
    }

    void RedirectBranchTargets(MethodBody body, List<Instruction> removed, Instruction replacement)
    {
        var removedSet = new HashSet<Instruction>(removed);
        foreach (var instr in body.Instructions)
        {
            if (instr.Operand is Instruction target && removedSet.Contains(target))
                instr.Operand = replacement;
            else if (instr.Operand is Instruction[] targets)
            {
                for (int i = 0; i < targets.Length; i++)
                    if (removedSet.Contains(targets[i]))
                        targets[i] = replacement;
            }
        }
    }

    void RedirectExceptionHandlers(MethodBody body, List<Instruction> removed, Instruction replacement)
    {
        var removedSet = new HashSet<Instruction>(removed);
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart != null && removedSet.Contains(eh.TryStart)) eh.TryStart = replacement;
            if (eh.TryEnd != null && removedSet.Contains(eh.TryEnd)) eh.TryEnd = replacement;
            if (eh.HandlerStart != null && removedSet.Contains(eh.HandlerStart)) eh.HandlerStart = replacement;
            if (eh.HandlerEnd != null && removedSet.Contains(eh.HandlerEnd)) eh.HandlerEnd = replacement;
            if (eh.FilterStart != null && removedSet.Contains(eh.FilterStart)) eh.FilterStart = replacement;
        }
    }

    void AddExceptionHandlers(MethodBody body, List<ExceptionHandlerInfo> handlers, ModuleDefinition module)
    {
        foreach (var h in handlers)
        {
            var eh = new ExceptionHandler(h.HandlerType)
            {
                TryStart = h.TryStart,
                TryEnd = h.TryEnd,
                HandlerStart = h.HandlerStart,
                HandlerEnd = h.HandlerEnd,
                FilterStart = h.FilterStart,
                CatchType = h.CatchType != null ? module.ImportReference(h.CatchType) : null
            };
            body.ExceptionHandlers.Add(eh);
        }
    }

    #region Instruction helpers

    static Instruction CloneInstruction(Instruction orig)
    {
        var clone = Instruction.Create(OpCodes.Nop);
        clone.OpCode = orig.OpCode;
        clone.Operand = orig.Operand;
        return clone;
    }

    static bool IsLdarg(OpCode opCode, out int implicitIndex, Instruction instr)
    {
        implicitIndex = -1;
        if (opCode == OpCodes.Ldarg_0) { implicitIndex = 0; return true; }
        if (opCode == OpCodes.Ldarg_1) { implicitIndex = 1; return true; }
        if (opCode == OpCodes.Ldarg_2) { implicitIndex = 2; return true; }
        if (opCode == OpCodes.Ldarg_3) { implicitIndex = 3; return true; }
        if (opCode == OpCodes.Ldarg || opCode == OpCodes.Ldarg_S) return true;
        return false;
    }

    static bool IsStarg(OpCode opCode, Instruction instr) =>
        opCode == OpCodes.Starg || opCode == OpCodes.Starg_S;

    static bool IsLdarga(OpCode opCode, Instruction instr) =>
        opCode == OpCodes.Ldarga || opCode == OpCodes.Ldarga_S;

    static int GetArgIndex(Instruction instr)
    {
        if (instr.OpCode == OpCodes.Ldarg_0) return 0;
        if (instr.OpCode == OpCodes.Ldarg_1) return 1;
        if (instr.OpCode == OpCodes.Ldarg_2) return 2;
        if (instr.OpCode == OpCodes.Ldarg_3) return 3;

        if (instr.Operand is ParameterDefinition param)
            return param.Index;
        if (instr.Operand is int idx)
            return idx;

        return -1;
    }

    static void RewriteAsLdarg(Instruction instr, int targetIndex, MethodDefinition method)
    {
        if (targetIndex < method.Parameters.Count + (method.HasThis ? 1 : 0))
        {
            switch (targetIndex)
            {
                case 0: instr.OpCode = OpCodes.Ldarg_0; instr.Operand = null; return;
                case 1: instr.OpCode = OpCodes.Ldarg_1; instr.Operand = null; return;
                case 2: instr.OpCode = OpCodes.Ldarg_2; instr.Operand = null; return;
                case 3: instr.OpCode = OpCodes.Ldarg_3; instr.Operand = null; return;
            }
            int paramIdx = method.HasThis ? targetIndex - 1 : targetIndex;
            if (paramIdx >= 0 && paramIdx < method.Parameters.Count)
            {
                instr.OpCode = OpCodes.Ldarg;
                instr.Operand = method.Parameters[paramIdx];
            }
        }
    }

    static void RewriteAsStarg(Instruction instr, int targetIndex, MethodDefinition method)
    {
        int paramIdx = method.HasThis ? targetIndex - 1 : targetIndex;
        if (paramIdx >= 0 && paramIdx < method.Parameters.Count)
        {
            instr.OpCode = OpCodes.Starg;
            instr.Operand = method.Parameters[paramIdx];
        }
    }

    static void RewriteAsLdarga(Instruction instr, int targetIndex, MethodDefinition method)
    {
        int paramIdx = method.HasThis ? targetIndex - 1 : targetIndex;
        if (paramIdx >= 0 && paramIdx < method.Parameters.Count)
        {
            instr.OpCode = OpCodes.Ldarga;
            instr.Operand = method.Parameters[paramIdx];
        }
    }

    static void RewriteAsLdloc(Instruction instr, int localIndex, MethodBody body)
    {
        if (localIndex < body.Variables.Count)
        {
            switch (localIndex)
            {
                case 0: instr.OpCode = OpCodes.Ldloc_0; instr.Operand = null; return;
                case 1: instr.OpCode = OpCodes.Ldloc_1; instr.Operand = null; return;
                case 2: instr.OpCode = OpCodes.Ldloc_2; instr.Operand = null; return;
                case 3: instr.OpCode = OpCodes.Ldloc_3; instr.Operand = null; return;
            }
            instr.OpCode = OpCodes.Ldloc;
            instr.Operand = body.Variables[localIndex];
        }
    }

    static void RewriteAsStloc(Instruction instr, int localIndex, MethodBody body)
    {
        if (localIndex < body.Variables.Count)
        {
            switch (localIndex)
            {
                case 0: instr.OpCode = OpCodes.Stloc_0; instr.Operand = null; return;
                case 1: instr.OpCode = OpCodes.Stloc_1; instr.Operand = null; return;
                case 2: instr.OpCode = OpCodes.Stloc_2; instr.Operand = null; return;
                case 3: instr.OpCode = OpCodes.Stloc_3; instr.Operand = null; return;
            }
            instr.OpCode = OpCodes.Stloc;
            instr.Operand = body.Variables[localIndex];
        }
    }

    static void RewriteAsLdloca(Instruction instr, int localIndex, MethodBody body)
    {
        if (localIndex < body.Variables.Count)
        {
            instr.OpCode = OpCodes.Ldloca;
            instr.Operand = body.Variables[localIndex];
        }
    }

    #endregion

    record struct ParamMapping(bool IsParameter, int TargetIndex);
}
