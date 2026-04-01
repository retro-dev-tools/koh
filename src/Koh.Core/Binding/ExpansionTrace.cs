using System.Collections.Immutable;
using Koh.Core.Syntax;

namespace Koh.Core.Binding;

public enum ExpansionKind
{
    MacroExpansion,
    ReptIteration,
    ForIteration,
    Include,
    TextReplay
}

public enum TextReplayReason
{
    MacroParameterConcatenation,
    UniqueLabelSubstitution,
    ForTokenShapingSubstitution,
    EqusReplay
}

public sealed record ExpansionFrame(
    ExpansionKind Kind,
    string FilePath,
    TextSpan SourceSpan,
    string? Name = null,
    int? Iteration = null,
    TextReplayReason? ReplayReason = null)
{
    internal static ExpansionFrame ForMacro(MacroDefinition macro)
        => new(ExpansionKind.MacroExpansion, macro.DefinitionFilePath,
               macro.DefinitionSpan, macro.Name);

    public static ExpansionFrame ForRept(string filePath, TextSpan span, int iteration)
        => new(ExpansionKind.ReptIteration, filePath, span, Iteration: iteration);

    public static ExpansionFrame ForFor(string filePath, TextSpan span,
        string? varName, int iteration)
        => new(ExpansionKind.ForIteration, filePath, span, varName, iteration);

    public static ExpansionFrame ForInclude(string filePath, TextSpan span)
        => new(ExpansionKind.Include, filePath, span);

    public static ExpansionFrame ForTextReplay(string filePath, TextSpan sourceSpan,
        TextReplayReason reason)
        => new(ExpansionKind.TextReplay, filePath, sourceSpan, ReplayReason: reason);
}

public sealed record ExpansionTrace(ImmutableArray<ExpansionFrame> Frames)
{
    public static readonly ExpansionTrace Empty = new([]);
    public bool IsEmpty => Frames.IsDefaultOrEmpty;
    public ExpansionFrame? Current => IsEmpty ? null : Frames[^1];
    public int Depth => Frames.Length;

    public ExpansionTrace Push(ExpansionFrame frame) => new(Frames.Add(frame));

    public bool ContainsKind(ExpansionKind kind)
    {
        foreach (var f in Frames)
            if (f.Kind == kind) return true;
        return false;
    }

    public ExpansionFrame? FindNearest(ExpansionKind kind)
    {
        for (int i = Frames.Length - 1; i >= 0; i--)
            if (Frames[i].Kind == kind) return Frames[i];
        return null;
    }
}
