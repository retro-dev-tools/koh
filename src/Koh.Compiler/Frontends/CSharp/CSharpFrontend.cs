using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// The C# frontend. Per the design, this does not implement a C# parser: it uses Roslyn
/// (<c>Microsoft.CodeAnalysis.CSharp</c>) to parse and bind, rejects any construct outside
/// the supported systems subset ("Koh C#") with a precise diagnostic, and lowers the rest to
/// <see cref="IrModule"/>. Roslyn is a Phase 3 dependency; this stub establishes the
/// registration and contract. See
/// <c>docs/superpowers/specs/2026-07-05-csharp-frontend-compiler-platform-design.md</c>.
/// </summary>
public sealed class CSharpFrontend : IFrontend
{
    public string Name => "csharp";

    public IReadOnlyList<string> Extensions => [".cs"];

    public IrModule Lower(SourceText source, DiagnosticBag diagnostics) =>
        throw new NotImplementedException(
            "C# frontend lowering is Phase 3 (see the compiler-platform design spec).");
}
