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
    /// the name of the symbol that must be resolved by the linker. Also set for
    /// simple <c>Name &#x2B; constant</c> / <c>Name - constant</c> expressions (e.g.
    /// <c>ldh [hScratch&#x2B;4], a</c>); the constant goes in <see cref="SymbolOffset"/>.
    /// Null for complex expressions — those are resolved at assemble time or left as
    /// opaque patches for the existing unresolved-cross-file-expression path.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// Constant offset added to the resolved symbol address when applying the
    /// patch. Lets simple <c>Label &#x2B;/- N</c> operands resolve correctly even when
    /// the label itself is in a different section (e.g. HRAM scratch addressed
    /// from a ROMX section).
    /// </summary>
    public int SymbolOffset { get; init; }

    /// <summary>
    /// Right-shift to apply to (<see cref="SymbolName"/> address + <see cref="SymbolOffset"/>)
    /// before masking to the patch's byte width. Used by Absolute8 patches that came
    /// from <c>HIGH(label)</c> (shift = 8) so the high byte of a cross-section label can
    /// still be emitted at link time. Default 0 means "use the value as-is".
    /// </summary>
    public int SymbolShift { get; init; }
}
