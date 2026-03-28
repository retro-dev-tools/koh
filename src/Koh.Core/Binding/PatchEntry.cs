using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

public enum PatchKind
{
    Absolute8,
    Absolute16,
    Relative8,
}

public sealed class PatchEntry
{
    public required string SectionName { get; init; }
    public required int Offset { get; init; }
    public required GreenNodeBase? Expression { get; init; }
    public required PatchKind Kind { get; init; }
    public int PCAfterInstruction { get; init; }
    public TextSpan DiagnosticSpan { get; init; }
    public string? FilePath { get; init; }
    /// <summary>Global label anchor at the site where this patch was recorded, for local label resolution.</summary>
    public string? GlobalAnchorName { get; init; }
}
