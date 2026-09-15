using Koh.Core.Text;

namespace Koh.Compiler.Frontends;

/// <summary>
/// What a frontend lowers from: either in-memory source text (the existing text-driven
/// frontends) or a compiled assembly plus its references (the CIL frontend). Exactly one of
/// <see cref="Text"/> or an assembly-only <see cref="FilePath"/> applies per factory; a frontend
/// that requires the other shape reports a diagnostic rather than throwing, since the input is
/// user-controlled (build configuration, CLI arguments).
/// </summary>
public sealed class CompilerInput
{
    /// <summary>The source file path (text frontends) or assembly path (assembly frontends).</summary>
    public string FilePath { get; }

    /// <summary>Source text, present only for text-driven frontends.</summary>
    public SourceText? Text { get; }

    /// <summary>Reference assembly paths, used only by assembly-driven frontends.</summary>
    public IReadOnlyList<string> ReferencePaths { get; }

    private CompilerInput(string filePath, SourceText? text, IReadOnlyList<string> referencePaths)
    {
        FilePath = filePath;
        Text = text;
        ReferencePaths = referencePaths;
    }

    /// <summary>Wraps in-memory source text for a text-driven frontend (e.g. <c>csharp</c>).</summary>
    public static CompilerInput FromSource(SourceText text) => new(text.FilePath, text, []);

    /// <summary>
    /// Wraps a compiled assembly and its references for an assembly-driven frontend (e.g.
    /// <c>cil</c>). <paramref name="referencePaths"/> lets the frontend resolve types/members
    /// defined outside <paramref name="assemblyPath"/> (e.g. Mono.Cecil's assembly resolver
    /// search directories).
    /// </summary>
    public static CompilerInput FromAssembly(
        string assemblyPath,
        IReadOnlyList<string> referencePaths
    ) => new(assemblyPath, null, referencePaths);
}
