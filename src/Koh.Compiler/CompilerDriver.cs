using Koh.Compiler.Backends;
using Koh.Compiler.Frontends;
using Koh.Compiler.Ir.Optimization;
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
    /// Compile <paramref name="source"/> with the given frontend and backend. Diagnostics from both
    /// phases accumulate into <paramref name="diagnostics"/>. The IR is optimized between the phases,
    /// skipped when the frontend reported an error (the IR may be incomplete — warnings don't block it).
    /// </summary>
    public static EmitModel Compile(
        IFrontend frontend,
        IBackend backend,
        SourceText source,
        DiagnosticBag diagnostics
    ) => Compile(frontend, backend, CompilerInput.FromSource(source), diagnostics);

    /// <summary>
    /// Compile <paramref name="input"/> with the given frontend and backend. Same pipeline as the
    /// <see cref="SourceText"/> overload, but accepts any <see cref="CompilerInput"/> shape
    /// (assembly-driven frontends included).
    /// </summary>
    public static EmitModel Compile(
        IFrontend frontend,
        IBackend backend,
        CompilerInput input,
        DiagnosticBag diagnostics
    )
    {
        var module = frontend.Lower(input, diagnostics);
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            IrOptimizer.Optimize(module);
        return backend.Compile(module, diagnostics);
    }
}
