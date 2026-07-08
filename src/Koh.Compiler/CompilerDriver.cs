using Koh.Compiler.Backends;
using Koh.Compiler.Frontends;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Compiler;

/// <summary>
/// Orchestrates one compilation: source through a frontend to IR, then through a backend to
/// an <see cref="EmitModel"/>. The backend owns legalization and instruction selection, so
/// the driver stays a thin, target-agnostic pipeline. The resulting <see cref="EmitModel"/>
/// is written to <c>.kobj</c> and handed to <c>Koh.Linker.Core</c> exactly like assembler
/// output.
/// </summary>
public static class CompilerDriver
{
    /// <summary>
    /// Compile <paramref name="source"/> with the given frontend and backend. Diagnostics from
    /// both phases accumulate into <paramref name="diagnostics"/>.
    /// </summary>
    public static EmitModel Compile(
        IFrontend frontend,
        IBackend backend,
        SourceText source,
        DiagnosticBag diagnostics
    )
    {
        var module = frontend.Lower(source, diagnostics);
        return backend.Compile(module, diagnostics);
    }
}
