namespace ILLiveEditor;

/// <summary>
/// Specifies how snippet IL instructions are inserted relative to the target instruction.
/// </summary>
public enum SnippetInsertionMode
{
    /// <summary>Insert snippet instructions before the target instruction.</summary>
    Before,

    /// <summary>Insert snippet instructions after the target instruction.</summary>
    After,

    /// <summary>Replace a range of instructions [insertionIndex, replaceEndIndex) with snippet instructions.</summary>
    ReplaceRange
}
