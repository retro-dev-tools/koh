using Koh.Core.Syntax;

namespace Koh.Core.Binding;

/// <summary>
/// Classifies how an <see cref="ExpandedNode"/> was produced.
/// </summary>
public enum ExpansionKind
{
    /// <summary>Node from the original source file, no synthetic expansion.</summary>
    Source,
    /// <summary>Produced by macro body expansion.</summary>
    MacroExpansion,
    /// <summary>Produced by REPT loop iteration.</summary>
    ReptIteration,
    /// <summary>Produced by FOR loop iteration.</summary>
    ForIteration,
    /// <summary>Produced by INCLUDE directive.</summary>
    Include,
    /// <summary>Produced by EQUS text replay or interpolation-driven re-expansion.</summary>
    TextReplay
}

/// <summary>
/// Records where a synthetic expansion originated: the expansion type,
/// the source file containing the triggering directive, the directive's span,
/// and an optional name (macro name, FOR variable, etc.).
///
/// This is provenance metadata — it tells downstream consumers (binder, diagnostics,
/// IDE features) how a given ExpandedNode was produced, so they can report errors
/// that point back to the macro definition or loop header rather than to a synthetic
/// text position that no user ever wrote.
/// </summary>
public sealed record ExpansionOrigin(
    ExpansionKind Kind,
    string FilePath,
    TextSpan SourceSpan,
    string? Name = null)
{
    internal static ExpansionOrigin ForMacro(MacroDefinition macro)
        => new(ExpansionKind.MacroExpansion, macro.DefinitionFilePath, macro.DefinitionSpan, macro.Name);

    internal static ExpansionOrigin ForRept(string filePath, TextSpan headerSpan)
        => new(ExpansionKind.ReptIteration, filePath, headerSpan);

    internal static ExpansionOrigin ForFor(string filePath, TextSpan headerSpan, string? varName)
        => new(ExpansionKind.ForIteration, filePath, headerSpan, varName);

    internal static ExpansionOrigin ForInclude(string includedFilePath, TextSpan directiveSpan)
        => new(ExpansionKind.Include, includedFilePath, directiveSpan);
}
