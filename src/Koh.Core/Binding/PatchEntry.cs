using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

public enum PatchKind
{
    Absolute8,
    Absolute16,
    Absolute32,
    Relative8,
}

public sealed class PatchEntry
{
    public required string SectionName { get; init; }
    public required int Offset { get; init; }
    public required GreenNodeBase? Expression { get; init; }
    public required PatchKind Kind { get; init; }
    /// <summary>
    /// Section-relative byte offset of the first byte AFTER the patched instruction.
    /// Used for <see cref="PatchKind.Relative8"/> patches to compute the branch displacement.
    /// Stored as an offset from byte 0 of the containing section (not an absolute address).
    /// </summary>
    public int PCAfterInstruction { get; init; }
    public TextSpan DiagnosticSpan { get; init; }
    public string? FilePath { get; init; }
    /// <summary>Global label anchor at the site where this patch was recorded, for local label resolution.</summary>
    public string? GlobalAnchorName { get; init; }
    /// <summary>
    /// For single-identifier operands (e.g. <c>jp Boot</c>, <c>call Foo</c>, <c>ld hl, X</c>),
    /// the name of the symbol that must be resolved by the linker.
    /// Null for complex expressions — those are resolved at assemble time or left as
    /// opaque patches for the existing unresolved-cross-file-expression path.
    /// </summary>
    public string? SymbolName { get; init; }
}
