using Mono.Cecil.Cil;

namespace ILLiveEditor;

/// <summary>
/// Result of an IL patching operation.
/// </summary>
public class PatchResult
{
    public bool Success { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<Instruction> InjectedInstructions { get; }

    public PatchResult(bool success, IReadOnlyList<string>? diagnostics = null, IReadOnlyList<Instruction>? injectedInstructions = null)
    {
        Success = success;
        Diagnostics = diagnostics ?? Array.Empty<string>();
        InjectedInstructions = injectedInstructions ?? Array.Empty<Instruction>();
    }

    public static PatchResult Failure(IReadOnlyList<string> diagnostics) =>
        new(false, diagnostics);

    public static PatchResult Ok(IReadOnlyList<Instruction> injected) =>
        new(true, injectedInstructions: injected);
}
