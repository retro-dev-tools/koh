using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Core.Text;

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
    /// Lower parsed source to IR. Errors are reported into <paramref name="diagnostics"/>;
    /// on hard failure the returned module may be incomplete and callers should check the bag.
    /// </summary>
    IrModule Lower(SourceText source, DiagnosticBag diagnostics);
}
