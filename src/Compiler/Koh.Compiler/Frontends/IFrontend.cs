using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;

namespace Koh.Compiler.Frontends;

/// <summary>
/// A source language frontend. Adding a language means adding a directory under
/// <c>Frontends/</c> with one implementation of this interface and registering it in
/// <see cref="CompilerRegistry"/>. A frontend parses and binds its own syntax and lowers
/// to the target-independent <see cref="IrModule"/>; it knows nothing about SM83.
/// </summary>
public interface IFrontend
{
    /// <summary>Stable identifier, e.g. "csharp".</summary>
    string Name { get; }

    /// <summary>File extensions this frontend claims, e.g. ".cs".</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Lower <paramref name="input"/> to IR. Errors are reported into
    /// <paramref name="diagnostics"/>; on hard failure the returned module may be incomplete
    /// and callers should check the bag. A frontend that cannot consume the given
    /// <see cref="CompilerInput"/> shape (e.g. a text frontend handed an assembly-only input)
    /// reports a diagnostic rather than throwing.
    /// </summary>
    IrModule Lower(CompilerInput input, DiagnosticBag diagnostics);
}
